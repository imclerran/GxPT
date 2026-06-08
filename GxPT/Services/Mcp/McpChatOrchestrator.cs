using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public const int DefaultMaxIterations = 25;
        public const int DefaultCallTimeoutMs = 60000;

        // Tool-result content returned when the user denies a call. Lives here (this file is linked
        // into the test project) so the transcript renderer can recognize a denied call; McpMarkers
        // references it.
        internal const string DeniedResultText = "[Call denied by user.]";

        // Agentic behavior guidance, prepended as a system message on tool-enabled turns only
        // (this orchestrator runs solely when at least one tool is available). Kept short to
        // limit token cost. Reinforces four things: act through the tools, don't return a null/
        // evasive answer when a tool could resolve the question, treat a denial as scoped to that
        // one call rather than a permanent ban, and don't volunteer a rundown of tools unasked.
        internal const string AgentSystemPrompt =
            "You are an AI assistant operating as an agent with access to tools. Use them "
            + "proactively to accomplish the user's request instead of asking the user to do what "
            + "you can do yourself, and instead of guessing when a tool could give you the answer.\n\n"
            + "Before saying you don't know or can't help, consider whether one of your tools could "
            + "answer the question - if so, use it. Do not return an empty or evasive response when "
            + "investigation is possible; make a genuine attempt with the tools available before "
            + "reporting that something cannot be done.\n\n"
            + "When a tool call is denied or cancelled, treat it as a refusal of that specific "
            + "call in that specific moment, not a permanent ban on the tool. You may try the same "
            + "tool again later with adjusted arguments or once the situation changes.\n\n"
            + "Do not list or describe your tools or capabilities unless the user asks. Reply to "
            + "greetings and casual messages naturally and briefly, and bring up what you can do "
            + "only when it is relevant to the user's request.";

        // Per-turn workspace block, built from WorkingDir and injected as its own ephemeral system
        // message right after the agent prompt (only when a workspace is set). Kept separate from
        // AgentSystemPrompt because the path is dynamic; absent entirely when there is no workspace,
        // so a workspace-less turn leaves no trace of one in context. Tells the model where it is
        // running so questions about "the project/code/files" go to disk before the web.
        internal static string WorkspaceSystemMessage(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            return "You are running in this workspace directory: `" + workingDir + "`. When the user "
                + "asks about files, code, or the project without naming an external source, they "
                + "mean this workspace - look here first.";
        }

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

        // Optional provider of the persistent-memory system block (the current .gxpt/memory.md index
        // plus its framing), rebuilt from disk each request and injected as an ephemeral, non-persisted
        // system message - the same treatment as the names manifest. Returns null/empty when memory is
        // disabled or there is no workspace. All memory framing lives here (never in AgentSystemPrompt)
        // so a disabled memory system leaves no trace in context (design M5/M6).
        public Func<string> MemorySystemMessageProvider { get; set; }

        // Optional provider of the skills manifest system block (the always-on slug/description list plus
        // its framing), rebuilt each request and injected as an ephemeral system message ordered after
        // the memory block and before the MCP names manifest (design sec.5). Null/empty => no skills
        // block, so a skill-less conversation leaves no trace in context.
        public Func<string> SkillsManifestSystemMessageProvider { get; set; }

        // Optional skills meta-tool surface (open_skill). When set and it has skills, open_skill is
        // exposed in the tools array and handled locally without an MCP round-trip, like reveal_tools.
        public SkillTools SkillTools { get; set; }

        // Provider data-collection preference applied to every request in the turn. Null leaves
        // it unset (provider default).
        public bool? ProviderDataCollectionAllowed { get; set; }

        // Zero data retention for every request in this turn. When true, emits provider.zdr=true so
        // OpenRouter routes only to zero-retention endpoints. Null/false leaves routing unconstrained.
        public bool? Zdr { get; set; }

        // Called when a turn exhausts its iteration budget with tool calls still pending. The argument
        // is the number of model iterations completed so far. Return true to grant another full budget
        // (the user chose to keep going), false to wrap up. Null => wrap up. The host wires this to an
        // in-transcript confirmation similar to the tool-approval prompt; it blocks the turn until the
        // user answers, which is correct (the user is present).
        public Func<int, bool> ContinuationDecider { get; set; }

        // The working directory of the conversation running this turn. Resolution of workdir-scoped
        // tools (files/git/command) is routed to the server bound to THIS folder, so concurrent turns
        // in different tabs hit their own folders' servers. Null = no workspace (scoped tools won't
        // resolve); workdir-independent tools (web/github/custom) resolve regardless.
        public string WorkingDir { get; set; }

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

            // Short id so a turn's lines can be followed in the log even when tabs run concurrently
            // (the ThreadPool thread id is reused across turns and can't be relied on for this).
            string turnId = Guid.NewGuid().ToString("N").Substring(0, 6);
            _log.Log("mcp", "[turn " + turnId + "] start: model=" + _model + ", history=" + history.Count
                + " msg(s), maxIterations=" + _maxIterations);

            // The cap is a budget rather than a fixed loop bound so the user can grant another batch
            // when it's reached (ContinuationDecider) instead of dead-ending the turn.
            int budget = _maxIterations;
            for (int iter = 0; ; iter++)
            {
                if (iter >= budget)
                {
                    bool cont = (ContinuationDecider != null) && ContinuationDecider(iter);
                    if (cont)
                    {
                        budget += _maxIterations;
                        _log.Log("mcp", "[turn " + turnId + "] iteration cap reached at " + iter
                            + "; user chose to continue (budget now " + budget + ")");
                    }
                    else
                    {
                        _log.Log("mcp", "[turn " + turnId + "] iteration cap reached at " + iter
                            + "; wrapping up");
                        RunCapWrapUp(history, ui, turnId);
                        return;
                    }
                }

                // MCP tools (reveal_tools + revealed defs) and their names manifest only when a server
                // actually contributes tools; a skills-only turn skips both and offers just open_skill.
                bool hasMcpTools = _registry != null && _registry.HasTools;
                IList<JObject> tools = hasMcpTools ? _registry.ExposedFunctionDefs() : null;
                string manifest = hasMcpTools ? _registry.NamesManifestSystemMessage() : null;
                if (SkillTools != null && SkillTools.HasSkills)
                {
                    if (tools == null) tools = new List<JObject>();
                    tools.Add(SkillTools.OpenSkillDef());
                }
                _log.Log("mcp", "[turn " + turnId + "] iteration " + (iter + 1) + "/" + budget
                    + ": requesting model with " + (tools != null ? tools.Count : 0) + " exposed tool(s)");

                // The names manifest rides as an extra system message in front of history; it is not
                // persisted (rebuilt each request from the live catalog).
                // Ephemeral system messages, ordered stable -> volatile for prompt-cache reuse:
                // constant agent prompt, then the workspace block (constant for the turn), then
                // memory (changes rarely within a turn), then the skills manifest, then the MCP names
                // manifest (rebuilt every request). None are persisted into history.
                List<ChatMessage> requestMessages = new List<ChatMessage>();
                requestMessages.Add(new ChatMessage("system", AgentSystemPrompt));
                string workspaceBlock = WorkspaceSystemMessage(WorkingDir);
                if (!string.IsNullOrEmpty(workspaceBlock))
                    requestMessages.Add(new ChatMessage("system", workspaceBlock));
                if (MemorySystemMessageProvider != null)
                {
                    string memoryBlock = MemorySystemMessageProvider();
                    if (!string.IsNullOrEmpty(memoryBlock))
                        requestMessages.Add(new ChatMessage("system", memoryBlock));
                }
                if (SkillsManifestSystemMessageProvider != null)
                {
                    string skillsBlock = SkillsManifestSystemMessageProvider();
                    if (!string.IsNullOrEmpty(skillsBlock))
                        requestMessages.Add(new ChatMessage("system", skillsBlock));
                }
                if (!string.IsNullOrEmpty(manifest))
                    requestMessages.Add(new ChatMessage("system", manifest));
                // Build the sent messages from history, optionally transformed (e.g. attachments
                // inlined). The transform must not drop tool_calls / tool_call_id.
                IList<ChatMessage> contextMessages = RequestMessageTransform != null
                    ? RequestMessageTransform(history) : history;
                requestMessages.AddRange(contextMessages);

                bool errored;
                string errMessage;
                ToolCallAssembler asm = StreamOnce(requestMessages, tools, ui, out errored, out errMessage);
                if (errored)
                {
                    _log.Log("mcp", "[turn " + turnId + "] aborted on iteration " + (iter + 1)
                        + ": stream error: " + (errMessage ?? "(none)"));
                    if (ui != null) ui.OnError(errMessage);
                    return;
                }
                LogResponse(turnId, iter, asm);

                // Degenerate response (no tool calls AND no text, but no error): some providers emit an
                // empty completion on a transient hiccup. Retry the same request once before giving up.
                if (!asm.ProducedToolCalls && IsEmptyText(asm.Text))
                {
                    _log.Log("mcp", "[turn " + turnId + "] empty response (no tool calls, no text); retrying once");
                    asm = StreamOnce(requestMessages, tools, ui, out errored, out errMessage);
                    if (errored)
                    {
                        _log.Log("mcp", "[turn " + turnId + "] aborted on iteration " + (iter + 1)
                            + " (retry): stream error: " + (errMessage ?? "(none)"));
                        if (ui != null) ui.OnError(errMessage);
                        return;
                    }
                    LogResponse(turnId, iter, asm);
                }

                if (!asm.ProducedToolCalls)
                {
                    if (IsEmptyText(asm.Text))
                    {
                        // Still empty after a retry: surface a clear, resumable notice rather than
                        // completing with a silent empty bubble.
                        string emptyNotice = "The model returned an empty response. Please try again.";
                        history.Add(new ChatMessage("assistant", emptyNotice));
                        _log.Log("mcp", "[turn " + turnId + "] still empty after retry; surfaced notice");
                        if (ui != null) { ui.AppendTextDelta(emptyNotice); ui.Complete(); }
                        return;
                    }

                    history.Add(new ChatMessage("assistant", asm.Text));
                    _log.Log("mcp", "[turn " + turnId + "] complete: final answer after " + (iter + 1)
                        + " iteration(s), " + asm.Text.Length + " chars");
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
                    string result = ExecuteCall(call, turnId, out isError);

                    if (ui != null) ui.OnToolResult(call.Name, result, isError);
                    ChatMessage toolMsg = new ChatMessage("tool", result);
                    toolMsg.ToolCallId = call.Id;
                    history.Add(toolMsg);
                }
                // Loop: re-call the model with the tool results in context.
            }
        }

        // One streamed model request into a fresh assembler. Shared by the main loop, the
        // empty-response retry, and (with tools = null) the cap wrap-up.
        private ToolCallAssembler StreamOnce(IList<ChatMessage> requestMessages, IList<JObject> tools,
                                             IToolLoopUi ui, out bool errored, out string errMessage)
        {
            ClientProperties props = new ClientProperties();
            props.Stream = true;
            props.ProviderDataCollectionAllowed = ProviderDataCollectionAllowed;
            props.Zdr = Zdr;

            Action<string> textSink = (ui != null) ? new Action<string>(ui.AppendTextDelta) : null;
            ToolCallAssembler asm = new ToolCallAssembler(textSink);
            bool err = false;
            string emsg = null;
            _streamer.StreamChat(_model, requestMessages, tools, props,
                asm.OnChunk,
                delegate(string e) { err = true; emsg = e; });
            asm.Finish();
            errored = err;
            errMessage = emsg;
            return asm;
        }

        private void LogResponse(string turnId, int iter, ToolCallAssembler asm)
        {
            _log.Log("mcp", "[turn " + turnId + "] iteration " + (iter + 1) + " response: finish_reason="
                + (asm.FinishReason ?? "(none)")
                + ", toolCalls=" + (asm.ProducedToolCalls ? asm.Calls.Count : 0)
                + ", textLen=" + (asm.Text != null ? asm.Text.Length : 0)
                + (asm.Truncated ? " [TRUNCATED: model output cut off by length]" : ""));
        }

        // Cap reached and not continued: one final tool-less model call asking it to summarize and
        // ask how to proceed, so the turn ends with a readable assistant message rather than a
        // cryptic dead-end. The user can simply reply to keep going (a fresh budget next turn).
        private void RunCapWrapUp(IList<ChatMessage> history, IToolLoopUi ui, string turnId)
        {
            List<ChatMessage> requestMessages = new List<ChatMessage>();
            IList<ChatMessage> contextMessages = RequestMessageTransform != null
                ? RequestMessageTransform(history) : history;
            requestMessages.AddRange(contextMessages);
            // Sent as a user message, not system: Anthropic (via OpenRouter) hoists in-array system
            // messages to the top-level system parameter, which would leave the conversation ending
            // on a tool result and the model with nothing in-position to answer (it replies with a
            // near-empty acknowledgment). A trailing user turn keeps the instruction in place so the
            // model actually summarizes.
            requestMessages.Add(new ChatMessage("user",
                "You have reached the maximum number of tool calls allowed for this turn. Do not "
                + "request any more tools now. Briefly summarize what you have done so far and what "
                + "still remains, then ask the user how they would like to proceed."));

            // No tools offered, so the model must answer with text.
            bool errored;
            string errMessage;
            ToolCallAssembler asm = StreamOnce(requestMessages, null, ui, out errored, out errMessage);

            string text;
            if (errored || IsEmptyText(asm.Text))
            {
                if (errored)
                    _log.Log("mcp", "[turn " + turnId + "] wrap-up stream error: " + (errMessage ?? "(none)"));
                text = "I've reached the tool-call limit for this turn. Let me know how you'd like to proceed.";
                // StreamOnce streamed nothing usable, so emit the fallback to the UI ourselves.
                if (ui != null) ui.AppendTextDelta(text);
            }
            else
            {
                text = asm.Text;
                _log.Log("mcp", "[turn " + turnId + "] wrap-up complete (" + text.Length + " chars)");
            }

            history.Add(new ChatMessage("assistant", text));
            if (ui != null) ui.Complete();
        }

        private static bool IsEmptyText(string s)
        {
            return s == null || s.Trim().Length == 0;
        }

        // Executes one tool call, returning the text to feed back as the tool message content.
        // Failures are returned as content (not thrown) so the model can recover; isError flags the
        // UI marker. reveal_tools is handled locally without an MCP round-trip.
        private string ExecuteCall(ToolCall call, string turnId, out bool isError)
        {
            isError = false;

            if (_registry != null && _registry.IsRevealTools(call.Name))
            {
                string[] names = ParseRevealNames(call.ArgumentsJson);
                _log.Log("mcp", "[turn " + turnId + "] reveal_tools: " + names.Length + " name(s)");
                return _registry.Reveal(names);
            }

            // open_skill is a host meta-tool (no MCP round-trip): load skill bodies by slug. Same
            // {names:[...]} argument shape as reveal_tools, so the parser is reused.
            if (SkillTools != null && SkillTools.IsOpenSkill(call.Name))
            {
                string[] slugs = ParseRevealNames(call.ArgumentsJson);
                _log.Log("mcp", "[turn " + turnId + "] open_skill: " + slugs.Length + " name(s)");
                return SkillTools.Open(slugs);
            }

            McpServerConnection conn;
            string toolName;
            if (_registry == null || !_registry.TryResolve(call.Name, WorkingDir, out conn, out toolName))
            {
                isError = true;
                _log.Log("mcp", "[turn " + turnId + "] unresolved tool '" + call.Name + "' (workdir="
                    + (string.IsNullOrEmpty(WorkingDir) ? "(none)" : WorkingDir) + ")");
                return "[Unknown tool: " + call.Name + "]";
            }

            JObject args;
            if (!TryParseArgs(call.ArgumentsJson, out args))
            {
                isError = true;
                _log.Log("mcp", "[turn " + turnId + "] invalid arguments for '" + call.Name
                    + "' (not valid JSON)");
                return "[Invalid tool arguments: not valid JSON.]";
            }

            // Logged before the approval check: if the next line for this call is far behind in
            // wall-clock time but reports a small tool 'ms', the gap was the user's approval prompt.
            _log.Log("mcp", "[turn " + turnId + "] dispatch '" + call.Name + "' (args "
                + (call.ArgumentsJson != null ? call.ArgumentsJson.Length : 0) + " bytes)");

            ApprovalDecision decision = _approval.Check(call.Name, args);
            if (decision == ApprovalDecision.Deny)
            {
                isError = true;
                _log.Log("mcp", "[turn " + turnId + "] '" + call.Name + "' denied by approval policy");
                return DeniedResultText;
            }

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                CallToolResult res = conn.CallTool(toolName, args, _callTimeoutMs);
                sw.Stop();
                isError = (res != null && res.IsError);
                string formatted = FormatResult(res);
                _log.Log("mcp", "[turn " + turnId + "] '" + call.Name + "' -> "
                    + (isError ? "isError" : "ok") + " (" + (formatted != null ? formatted.Length : 0)
                    + " chars, " + sw.ElapsedMilliseconds + "ms)");
                return formatted;
            }
            catch (McpTransportException ex)
            {
                sw.Stop();
                _log.Log("mcp", "[turn " + turnId + "] transport fault calling '" + call.Name + "' after "
                    + sw.ElapsedMilliseconds + "ms: " + ex.Message);
                isError = true;
                return "[Server unavailable.]";
            }
            catch (McpTimeoutException ex)
            {
                sw.Stop();
                _log.Log("mcp", "[turn " + turnId + "] timeout calling '" + call.Name + "' after "
                    + sw.ElapsedMilliseconds + "ms: " + ex.Message);
                isError = true;
                return "[Tool timed out.]";
            }
            catch (McpException ex)
            {
                sw.Stop();
                _log.Log("mcp", "[turn " + turnId + "] tool error calling '" + call.Name + "' after "
                    + sw.ElapsedMilliseconds + "ms: " + ex.Message);
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
