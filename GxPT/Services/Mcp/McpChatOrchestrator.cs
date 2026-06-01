using System;
using System.Collections.Generic;
using System.Text;
using Mcp35.Client;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Errors;
using Mcp35.Core.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The host-side tool-call loop (phase 4): call the model, reassemble streamed tool_calls, run
    // them through the registry/approval/connection, feed results back as tool-role messages, and
    // re-call until the model produces a final answer (bounded by MaxIterations). Runs on a worker
    // thread; the UI callbacks marshal to the form. Approval is pluggable (phase-4 stub → phase-6
    // tiered policy) and called at the one right point.
    internal sealed class McpChatOrchestrator
    {
        public const int DefaultMaxIterations = 8;
        public const int DefaultCallTimeoutMs = 60000;

        private readonly IChatStreamer _streamer;
        private readonly McpToolRegistry _registry;
        private readonly IToolApprovalPolicy _approval;
        private readonly string _model;
        private readonly ILogSink _log;
        private readonly int _maxIterations;
        private readonly int _callTimeoutMs;

        // Optional hook to transform history into the messages actually sent (e.g. inline file
        // attachments) without mutating the persisted history. Identity transform when null. Must
        // preserve assistant ToolCalls and tool-role ToolCallId.
        public Func<IList<ChatMessage>, IList<ChatMessage>> RequestMessageTransform { get; set; }

        // Provider data-collection (ZDR) preference applied to every request in the turn. Null leaves
        // it unset (provider default).
        public bool? ProviderDataCollectionAllowed { get; set; }

        public McpChatOrchestrator(IChatStreamer streamer, McpToolRegistry registry,
                                   IToolApprovalPolicy approval, string model, ILogSink log)
            : this(streamer, registry, approval, model, log, DefaultMaxIterations, DefaultCallTimeoutMs)
        {
        }

        public McpChatOrchestrator(IChatStreamer streamer, McpToolRegistry registry,
                                   IToolApprovalPolicy approval, string model, ILogSink log,
                                   int maxIterations, int callTimeoutMs)
        {
            if (streamer == null) throw new ArgumentNullException("streamer");
            _streamer = streamer;
            _registry = registry;
            _approval = approval != null ? approval : new AllowAllApprovalPolicy();
            _model = model;
            _log = log != null ? log : NullLogSink.Instance;
            _maxIterations = maxIterations > 0 ? maxIterations : DefaultMaxIterations;
            _callTimeoutMs = callTimeoutMs > 0 ? callTimeoutMs : DefaultCallTimeoutMs;
        }

        // Run one user turn to completion: appends the user message, then loops model<->tools,
        // mutating history in place (so the conversation keeps assistant tool_calls + tool results).
        public void RunTurn(IList<ChatMessage> history, string userText, IToolLoopUi ui)
        {
            if (history == null) throw new ArgumentNullException("history");
            history.Add(new ChatMessage("user", userText));
            RunTurn(history, ui);
        }

        // Same loop, but over a history whose last message is already the user's turn — the host's
        // chat path adds the user message to the conversation itself. history is mutated in place.
        public void RunTurn(IList<ChatMessage> history, IToolLoopUi ui)
        {
            if (history == null) throw new ArgumentNullException("history");

            for (int iter = 0; iter < _maxIterations; iter++)
            {
                IList<JObject> tools = _registry != null ? _registry.ExposedFunctionDefs() : null;
                string manifest = _registry != null ? _registry.NamesManifestSystemMessage() : null;

                // The names manifest rides as an extra system message in front of history; it is not
                // persisted (rebuilt each request from the live catalog).
                List<ChatMessage> requestMessages = new List<ChatMessage>();
                if (!string.IsNullOrEmpty(manifest))
                    requestMessages.Add(new ChatMessage("system", manifest));
                // Build the sent messages from history, optionally transformed (e.g. attachments
                // inlined). The transform must not drop tool_calls / tool_call_id.
                IList<ChatMessage> contextMessages = RequestMessageTransform != null
                    ? RequestMessageTransform(history) : history;
                requestMessages.AddRange(contextMessages);

                ClientProperties props = new ClientProperties();
                props.Stream = true;
                props.ProviderDataCollectionAllowed = ProviderDataCollectionAllowed;

                Action<string> textSink = (ui != null) ? new Action<string>(ui.AppendTextDelta) : null;
                ToolCallAssembler asm = new ToolCallAssembler(textSink);
                bool errored = false;
                string errMessage = null;

                _streamer.StreamChat(_model, requestMessages, tools, props,
                    asm.OnChunk,
                    delegate(string err) { errored = true; errMessage = err; });
                asm.Finish();

                if (errored)
                {
                    // A streaming/transport error already failed this request; surface and stop.
                    if (ui != null) ui.OnError(errMessage);
                    return;
                }

                if (!asm.ProducedToolCalls)
                {
                    history.Add(new ChatMessage("assistant", asm.Text));
                    if (ui != null) ui.Complete();
                    return;
                }

                // The assistant turn that requested the calls must be recorded with its tool_calls,
                // or the follow-up tool messages have nothing to answer.
                ChatMessage assistantMsg = new ChatMessage("assistant", asm.Text);
                assistantMsg.ToolCalls = asm.Calls;
                history.Add(assistantMsg);

                // Serial execution (phase 4): one call fully handled before the next.
                for (int c = 0; c < asm.Calls.Count; c++)
                {
                    ToolCall call = asm.Calls[c];
                    if (ui != null) ui.OnToolCall(call.Name, call.ArgumentsJson);

                    bool isError;
                    string result = ExecuteCall(call, out isError);

                    if (ui != null) ui.OnToolResult(call.Name, result, isError);
                    ChatMessage toolMsg = new ChatMessage("tool", result);
                    toolMsg.ToolCallId = call.Id;
                    history.Add(toolMsg);
                }
                // Loop: re-call the model with the tool results in context.
            }

            // Bounded: hitting the cap returns a note rather than looping unbounded.
            history.Add(new ChatMessage("assistant", "[Tool-call limit reached.]"));
            if (ui != null) ui.Complete();
        }

        // Executes one tool call, returning the text to feed back as the tool message content.
        // Failures are returned as content (not thrown) so the model can recover; isError flags the
        // UI marker. reveal_tools is handled locally without an MCP round-trip.
        private string ExecuteCall(ToolCall call, out bool isError)
        {
            isError = false;

            if (_registry != null && _registry.IsRevealTools(call.Name))
                return _registry.Reveal(ParseRevealNames(call.ArgumentsJson));

            McpServerConnection conn;
            string toolName;
            if (_registry == null || !_registry.TryResolve(call.Name, out conn, out toolName))
            {
                isError = true;
                return "[Unknown tool: " + call.Name + "]";
            }

            JObject args;
            if (!TryParseArgs(call.ArgumentsJson, out args))
            {
                isError = true;
                return "[Invalid tool arguments: not valid JSON.]";
            }

            ApprovalDecision decision = _approval.Check(call.Name, args);
            if (decision == ApprovalDecision.Deny)
            {
                isError = true;
                return "[Call denied by user.]";
            }

            try
            {
                CallToolResult res = conn.CallTool(toolName, args, _callTimeoutMs);
                isError = (res != null && res.IsError);
                return FormatResult(res);
            }
            catch (McpTransportException ex)
            {
                _log.Log("mcp", "transport fault calling '" + call.Name + "': " + ex.Message);
                isError = true;
                return "[Server unavailable.]";
            }
            catch (McpTimeoutException ex)
            {
                _log.Log("mcp", "timeout calling '" + call.Name + "': " + ex.Message);
                isError = true;
                return "[Tool timed out.]";
            }
            catch (McpException ex)
            {
                isError = true;
                return "[Tool error: " + ex.Message + "]";
            }
        }

        // ---- helpers ----

        private static bool TryParseArgs(string argumentsJson, out JObject args)
        {
            args = null;
            try
            {
                if (string.IsNullOrEmpty(argumentsJson))
                {
                    args = new JObject();
                    return true;
                }
                JToken t = JToken.Parse(argumentsJson);
                if (t.Type == JTokenType.Object) { args = (JObject)t; return true; }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string[] ParseRevealNames(string argumentsJson)
        {
            List<string> names = new List<string>();
            try
            {
                JObject o = JObject.Parse(argumentsJson);
                JToken arr = o["names"];
                if (arr != null && arr.Type == JTokenType.Array)
                {
                    foreach (JToken n in (JArray)arr)
                    {
                        if (n != null && n.Type == JTokenType.String) names.Add((string)n);
                    }
                }
            }
            catch
            {
                // malformed reveal args → no names; Reveal returns an empty def list.
            }
            return names.ToArray();
        }

        // CallToolResult.content[] → a single string for the tool message. Text blocks are
        // concatenated; non-text blocks become a short placeholder; structuredContent (if any) is
        // appended as compact JSON. isError content is still returned verbatim (the model sees it).
        private static string FormatResult(CallToolResult res)
        {
            if (res == null) return string.Empty;

            StringBuilder sb = new StringBuilder();
            if (res.Content != null)
            {
                for (int i = 0; i < res.Content.Count; i++)
                {
                    ContentBlock b = res.Content[i];
                    if (b == null) continue;
                    string text;
                    if (b.TryGetText(out text))
                        sb.Append(text);
                    else
                        sb.Append(Placeholder(b));
                }
            }

            if (res.StructuredContent != null)
            {
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(res.StructuredContent.ToString(Formatting.None));
            }
            return sb.ToString();
        }

        private static string Placeholder(ContentBlock b)
        {
            string type = b.Type != null ? b.Type : "unknown";
            if (type == "resource" || type == "resource_link")
            {
                string uri = null;
                if (b.Raw != null)
                {
                    JToken direct = b.Raw["uri"];
                    if (direct != null && direct.Type == JTokenType.String) uri = (string)direct;
                    if (uri == null)
                    {
                        JToken resource = b.Raw["resource"];
                        if (resource != null && resource.Type == JTokenType.Object)
                        {
                            JToken nested = resource["uri"];
                            if (nested != null && nested.Type == JTokenType.String) uri = (string)nested;
                        }
                    }
                }
                return "[resource: " + (uri != null ? uri : "?") + "]";
            }
            return "[" + type + "]";
        }
    }
}
