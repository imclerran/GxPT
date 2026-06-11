using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    internal sealed class OpenRouterClient : IChatStreamer
    {
        private readonly string _apiKey;
        private readonly string _curlPath;

        public OpenRouterClient(string apiKey, string curlPath)
        {
            _apiKey = apiKey ?? string.Empty;
            _curlPath = curlPath;
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrEmpty(_apiKey) && File.Exists(_curlPath); }
        }

        // Build the request body (Newtonsoft, D16). Emits the messages (with assistant tool_calls and
        // tool-role tool_call_id where present), the optional tools array, and provider options.
        // The emoji-suppression system message is always prepended.
        internal static string BuildRequestBody(string model, IList<ChatMessage> messages, IList<JObject> tools, ClientProperties props)
        {
            var payload = new Dictionary<string, object>();
            payload["model"] = string.IsNullOrEmpty(model) ? "openai/gpt-4o" : model;
            bool streamFlag = (props != null && props.Stream.HasValue) ? props.Stream.Value : false;
            payload["stream"] = streamFlag;

            // Ask OpenRouter to return token-usage accounting (including prompt-cache read counts) -
            // on streaming requests it arrives on the final SSE chunk. This is how cache hits are
            // verified: see the "Usage" log line in StreamRawChunks.
            var usageOpt = new Dictionary<string, object>();
            usageOpt["include"] = true;
            payload["usage"] = usageOpt;

            // cache_control breakpoints are only emitted for providers that use explicit caching;
            // everywhere else flagged messages serialize as plain strings (see ContentValue).
            bool cacheable = ModelSupportsCacheControl((string)payload["model"]);

            var msgs = new List<object>();
            // Prepend a system instruction to avoid emoji in all responses.
            var sys = new Dictionary<string, object>();
            sys["role"] = "system";
            sys["content"] = "Do not use emojis or emoticons in any response. Use plain text only.";
            msgs.Add(sys);

            if (messages != null)
            {
                foreach (var m in messages)
                {
                    var mm = new Dictionary<string, object>();
                    mm["role"] = m.Role;

                    if (m.Role == "tool")
                    {
                        // Tool result, keyed back to the assistant's call id.
                        mm["content"] = ContentValue(m, cacheable);
                        mm["tool_call_id"] = m.ToolCallId;
                    }
                    else if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                    {
                        // Assistant turn requesting tool calls (content may be null alongside calls).
                        mm["content"] = string.IsNullOrEmpty(m.Content) ? null : (object)m.Content;
                        var calls = new List<object>();
                        foreach (var c in m.ToolCalls)
                        {
                            var fn = new Dictionary<string, object>();
                            fn["name"] = c.Name;
                            fn["arguments"] = string.IsNullOrEmpty(c.ArgumentsJson) ? "{}" : c.ArgumentsJson;
                            var call = new Dictionary<string, object>();
                            call["id"] = c.Id;
                            call["type"] = "function";
                            call["function"] = fn;
                            calls.Add(call);
                        }
                        mm["tool_calls"] = calls;
                    }
                    else
                    {
                        mm["content"] = ContentValue(m, cacheable);
                    }
                    msgs.Add(mm);
                }
            }
            payload["messages"] = msgs;

            // Expose tools (tool_choice defaults to the provider's "auto"; the orchestrator's cap
            // wrap-up sets "none" so the prompt prefix - which starts with the tools array - stays
            // byte-identical to the loop's requests and re-reads the turn's prompt cache).
            if (tools != null && tools.Count > 0)
            {
                var toolList = new List<object>();
                foreach (var t in tools) toolList.Add(t);
                payload["tools"] = toolList;
                if (props != null && !string.IsNullOrEmpty(props.ToolChoice))
                    payload["tool_choice"] = props.ToolChoice;
            }

            // Optionally add provider object if any of the provider properties are set.
            try
            {
                if (props != null)
                {
                    var provider = new Dictionary<string, object>();

                    // data_collection: include if set (true => allow, false => deny)
                    if (props.ProviderDataCollectionAllowed.HasValue)
                        provider["data_collection"] = props.ProviderDataCollectionAllowed.Value ? "allow" : "deny";

                    // zdr (zero data retention): OpenRouter treats the per-request flag as an OR with
                    // account/guardrail settings — it can only ensure ZDR for this request, never
                    // disable it. So we emit "zdr": true only when on, and omit it otherwise.
                    if (props.Zdr.HasValue && props.Zdr.Value)
                        provider["zdr"] = true;

                    // only: include non-empty list
                    if (props.ProviderOnly != null && props.ProviderOnly.Count > 0)
                    {
                        var onlyList = new List<string>();
                        foreach (var s in props.ProviderOnly)
                            if (!string.IsNullOrEmpty(s)) onlyList.Add(s);
                        if (onlyList.Count > 0) provider["only"] = onlyList;
                    }

                    // order: sticky cache-routing preference (see ClientProperties.ProviderOrder).
                    if (props.ProviderOrder != null && props.ProviderOrder.Count > 0)
                    {
                        var orderList = new List<string>();
                        foreach (var s in props.ProviderOrder)
                            if (!string.IsNullOrEmpty(s)) orderList.Add(s);
                        if (orderList.Count > 0) provider["order"] = orderList;
                    }

                    // max_price: include any provided fields
                    var maxPrice = new Dictionary<string, object>();
                    if (props.ProviderMaxPricePrompt.HasValue)
                        maxPrice["prompt"] = props.ProviderMaxPricePrompt.Value;
                    if (props.ProviderMaxPriceCompletion.HasValue)
                        maxPrice["completion"] = props.ProviderMaxPriceCompletion.Value;
                    if (maxPrice.Count > 0) provider["max_price"] = maxPrice;

                    if (provider.Count > 0) payload["provider"] = provider;
                }
            }
            catch { }

            return JsonConvert.SerializeObject(payload);
        }

        // A message's content value: a plain string normally; a one-part content array carrying
        // cache_control {type: ephemeral} when the message is flagged as a cache breakpoint and the
        // model's provider supports explicit caching (the array form is the OpenAI-compatible shape
        // OpenRouter requires for cache_control). Empty content always stays a plain string - there
        // is nothing to cache and Anthropic rejects empty text parts.
        private static object ContentValue(ChatMessage m, bool modelSupportsCaching)
        {
            string text = m.Content != null ? m.Content : string.Empty;
            if (!modelSupportsCaching || !m.CacheControl || text.Length == 0) return text;

            var part = new Dictionary<string, object>();
            part["type"] = "text";
            part["text"] = text;
            var cc = new Dictionary<string, object>();
            cc["type"] = "ephemeral";
            part["cache_control"] = cc;
            return new List<object> { part };
        }

        // Providers that use explicit cache_control breakpoints via OpenRouter (Anthropic requires
        // them; Gemini accepts them on top of its implicit caching). OpenAI/DeepSeek/Grok cache
        // automatically on a stable prefix and need no annotation. OpenRouter documents unsupported
        // cache_control as ignored, but gating by vendor prefix costs nothing and removes any risk
        // of an unknown provider rejecting the content-part form. "~"-prefixed model aliases resolve
        // to the same vendor, so the prefix is stripped before matching.
        internal static bool ModelSupportsCacheControl(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string m = StripModelAliasPrefix(model);
            return m.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase)
                || m.StartsWith("google/", StringComparison.OrdinalIgnoreCase);
        }

        // Providers whose prompt caching (explicit cache_control OR implicit/automatic prefix
        // caching) makes a byte-stable prompt prefix valuable. Gates reveal-set eviction in the
        // orchestrator: on these providers an evicted tool def changes the tools array (position 0
        // of the prompt) and re-bills the conversation's whole transcript at full price, which costs
        // far more than the stale def it saves - so the revealed set stays append-only. Providers
        // not listed here get no caching benefit from a stable prefix, so trimming stale defs is a
        // pure token saving there.
        internal static bool ModelSupportsPromptCaching(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string m = StripModelAliasPrefix(model);
            return m.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase)  // explicit (cache_control)
                || m.StartsWith("google/", StringComparison.OrdinalIgnoreCase)    // implicit + cache_control
                || m.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)    // automatic (>=1024 tokens)
                || m.StartsWith("deepseek/", StringComparison.OrdinalIgnoreCase)  // automatic
                || m.StartsWith("x-ai/", StringComparison.OrdinalIgnoreCase)      // automatic (Grok)
                || m.StartsWith("qwen/", StringComparison.OrdinalIgnoreCase)      // automatic (context cache)
                || m.StartsWith("minimax/", StringComparison.OrdinalIgnoreCase)   // automatic
                || m.StartsWith("moonshotai/", StringComparison.OrdinalIgnoreCase); // automatic (Kimi)
        }

        // "~"-prefixed model aliases (e.g. "~anthropic/claude-sonnet-latest") resolve to the same
        // vendor as their bare form; strip the marker before prefix-matching.
        private static string StripModelAliasPrefix(string model)
        {
            return model.StartsWith("~", StringComparison.Ordinal) ? model.Substring(1) : model;
        }

        public string CreateCompletion(string model, IList<ChatMessage> messages)
        {
            // Backward-compatible API: defaults to non-stream
            return CreateCompletion(model, messages, null);
        }

        // New overload accepting ClientProperties
        public string CreateCompletion(string model, IList<ChatMessage> messages, ClientProperties props)
        {
            // Ensure stream flag defaults to false for non-stream API
            if (props == null) props = new ClientProperties();
            if (!props.Stream.HasValue) props.Stream = false;
            string body = BuildRequestBody(model, messages, null, props);
            List<string> tempFiles;
            string args = BuildCurlArgs(body, out tempFiles);

            var psi = new ProcessStartInfo
            {
                FileName = _curlPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                // Explicitly decode stdout/stderr as UTF-8 to avoid mojibake on non-UTF8 system locales
                var utf8 = new UTF8Encoding(false, false);
                string output;
                string err;
                using (var outReader = new StreamReader(p.StandardOutput.BaseStream, utf8, false))
                {
                    output = outReader.ReadToEnd();
                }
                using (var errReader = new StreamReader(p.StandardError.BaseStream, utf8, false))
                {
                    err = errReader.ReadToEnd();
                }
                p.WaitForExit();
                int exitCode = -1;
                try { exitCode = p.ExitCode; }
                catch { }
                // Clean up temp files (request body and auth config)
                CleanupTempFiles(tempFiles);

                // Log a response summary so non-stream calls (e.g. conversation naming) leave a
                // diagnostic trail. The streaming path logs "curl exit=...", but this path was silent
                // after the request, making a failed/empty completion invisible in the log.
                try { Logger.Log("HTTP", "completion exit=" + exitCode + " outLen=" + (output != null ? output.Length : 0) + (string.IsNullOrEmpty(err) ? string.Empty : " stderrLen=" + err.Length)); }
                catch { }

                // If curl signaled an HTTP error but kept the body (with --fail-with-body), log the body and any extracted message for debugging
                try
                {
                    if (!string.IsNullOrEmpty(err) && output.Length > 0)
                    {
                        Logger.Log("HTTP", "curl error body: " + output);
                        try
                        {
                            var msg2 = ExtractErrorMessage(SafeParse(output));
                            if (!string.IsNullOrEmpty(msg2))
                                Logger.Log("HTTP", "error.message: " + msg2);
                        }
                        catch { }
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(err) && output.Length == 0)
                    throw new Exception("curl error: " + err);
                return output;
            }
        }

        // IChatStreamer: build the body (with tools) then stream parsed chunks. Used by the
        // McpChatOrchestrator; the assembler reassembles tool_calls and forwards text deltas.
        public void StreamChat(string model, IList<ChatMessage> messages, IList<JObject> tools,
                               ClientProperties props, Action<ChatCompletionChunk> onChunk, Action<string> onError,
                               RequestCancellation cancel)
        {
            if (props == null) props = new ClientProperties();
            if (!props.Stream.HasValue) props.Stream = true;
            string body = BuildRequestBody(model, messages, tools, props);
            StreamRawChunks(body, WrapWithProviderReport(props, onChunk), onError, cancel);
        }

        // Decorates a chunk sink to report the serving provider (once, from the first chunk that
        // carries it) via props.ProviderServedCallback. Identity when no callback is registered.
        private static Action<ChatCompletionChunk> WrapWithProviderReport(
            ClientProperties props, Action<ChatCompletionChunk> onChunk)
        {
            if (props == null || props.ProviderServedCallback == null) return onChunk;
            Action<string> report = props.ProviderServedCallback;
            bool[] reported = new bool[1]; // closure-mutable flag (C# 3.5: no local functions)
            return delegate(ChatCompletionChunk chunk)
            {
                if (!reported[0] && chunk != null && !string.IsNullOrEmpty(chunk.provider))
                {
                    reported[0] = true;
                    try { report(chunk.provider); }
                    catch { }
                }
                if (onChunk != null) onChunk(chunk);
            };
        }

        public void CreateCompletionStream(string model, IList<ChatMessage> messages, Action<string> onDelta, Action onDone, Action<string> onError)
        {
            // Backward-compatible API: defaults to stream
            CreateCompletionStream(model, messages, null, onDelta, onDone, onError, null);
        }

        // Text-only streaming wrapper over StreamRawChunks (no tools): forwards content deltas.
        // cancel (may be null) lets the caller kill the in-flight request mid-stream.
        public void CreateCompletionStream(string model, IList<ChatMessage> messages, ClientProperties props, Action<string> onDelta, Action onDone, Action<string> onError, RequestCancellation cancel)
        {
            if (props == null) props = new ClientProperties();
            if (!props.Stream.HasValue) props.Stream = true;
            string body = BuildRequestBody(model, messages, null, props);

            bool failed = false;
            StreamRawChunks(body,
                WrapWithProviderReport(props, delegate(ChatCompletionChunk chunk)
                {
                    if (chunk != null && chunk.choices != null)
                    {
                        foreach (var ch in chunk.choices)
                        {
                            string content = (ch != null && ch.delta != null) ? ch.delta.content : null;
                            if (!string.IsNullOrEmpty(content) && onDelta != null) onDelta(content);
                        }
                    }
                }),
                delegate(string err) { failed = true; if (onError != null) onError(err); },
                cancel);

            // A user-initiated stop returns from StreamRawChunks without an error (the connection was
            // dropped on purpose). onDone still fires so the caller finalizes whatever text streamed;
            // it inspects the cancel handle to decide how to treat an empty result.
            if (!failed && onDone != null) onDone();
        }

        // The shared curl + SSE streaming loop. Each "data:" line is parsed (Newtonsoft) into a
        // ChatCompletionChunk and handed to onChunk; if the whole stream yields no chunks, the body
        // is inspected for a JSON error and onError is called. When cancel is non-null its curl
        // process is registered so a Stop click can kill it; a kill is reported as a clean stop (no
        // onError) rather than a request failure.
        public void StreamRawChunks(string body, Action<ChatCompletionChunk> onChunk, Action<string> onError, RequestCancellation cancel)
        {
            List<string> tempFiles;
            string args = BuildCurlArgs(body, out tempFiles);

            var psi = new ProcessStartInfo
            {
                FileName = _curlPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                var proc = new Process();
                proc.StartInfo = psi;
                proc.EnableRaisingEvents = false;
                proc.Start();
                // Register the live process so a Stop click can kill it (dropping the connection,
                // which unblocks the ReadLine loop below). If Cancel already fired, this kills now.
                if (cancel != null) cancel.Attach(proc);

                // Read lines as they arrive (SSE: lines like "data: {...}") with explicit UTF-8 decoding
                var utf8 = new UTF8Encoding(false, false);
                var sr = new StreamReader(proc.StandardOutput.BaseStream, utf8, false);
                var er = new StreamReader(proc.StandardError.BaseStream, utf8, false);
                string line;
                bool sawAnyChunk = false;
                int chunkCount = 0;
                ChatCompletionChunk.UsageInfo usage = null;
                var stdoutBuf = new StringBuilder();
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    // Buffer all stdout; if the request failed we'll inspect this for a JSON error body.
                    if (line.Length > 0)
                    {
                        try { stdoutBuf.AppendLine(line); }
                        catch { }
                    }
                    if (line.Length == 0) continue;
                    // SSE events are "data: {...}" lines. Anything else (curl's --fail-with-body HTTP
                    // error body, SSE ':' heartbeats, etc.) is NOT a content chunk: skip it here so it
                    // can't be mis-parsed as an empty chunk and silently swallow the failure. It stays
                    // buffered above for post-loop error extraction.
                    bool isData = line.StartsWith("data:");
                    if (!isData) continue;
                    line = line.Substring(5).Trim();
                    if (line == "[DONE]") break;

                    ChatCompletionChunk chunk = null;
                    try { chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(line); }
                    catch { continue; } // Ignore malformed keepalive/heartbeat lines
                    if (chunk != null)
                    {
                        sawAnyChunk = true;
                        chunkCount++;
                        if (chunk.usage != null) usage = chunk.usage; // final SSE chunk carries usage
                        if (onChunk != null) onChunk(chunk);
                    }
                }
                int exitCode = -1;
                try { proc.WaitForExit(); }
                catch { }
                try { exitCode = proc.ExitCode; }
                catch { }

                string err = null;
                try { err = er.ReadToEnd(); }
                catch { }
                string rawBody = null;
                try { rawBody = stdoutBuf.ToString().Trim(); }
                catch { }
                string extractedMsg = null;
                if (!string.IsNullOrEmpty(rawBody))
                {
                    try { extractedMsg = ExtractErrorMessage(SafeParse(rawBody)); }
                    catch { }
                }

                try { Logger.Log("Stream", "curl exit=" + exitCode + " chunks=" + chunkCount + (sawAnyChunk ? string.Empty : " (no chunks)")); }
                catch { }

                // Prompt-cache telemetry: cached = prompt-prefix tokens served from the provider's
                // cache (discounted). cached=0 on a long conversation means the prefix missed - look
                // for a cache invalidator (tools array changed, non-deterministic serialization).
                try
                {
                    if (usage != null)
                    {
                        int? cached = (usage.prompt_tokens_details != null)
                            ? usage.prompt_tokens_details.cached_tokens : null;
                        Logger.Log("Usage", "prompt=" + (usage.prompt_tokens.HasValue ? usage.prompt_tokens.Value.ToString() : "?")
                            + " cached=" + (cached.HasValue ? cached.Value.ToString() : "?")
                            + " completion=" + (usage.completion_tokens.HasValue ? usage.completion_tokens.Value.ToString() : "?"));
                    }
                }
                catch { }

                // User-initiated stop: killing curl makes it exit non-zero / end without [DONE], which
                // would otherwise look like a failure. Treat it as a clean stop - keep whatever
                // streamed, raise no error - so the caller finalizes the partial answer.
                if (cancel != null && cancel.IsCancelled)
                {
                    try { Logger.Log("Stream", "stream cancelled by user (chunks=" + chunkCount + ")"); }
                    catch { }
                    cancel.Detach();
                    CleanupTempFiles(tempFiles);
                    return;
                }

                // Treat the request as failed if curl reported a non-zero exit (--fail-with-body makes
                // it exit non-zero on every HTTP error), if no content chunks arrived at all, or if the
                // body carried a JSON error payload. Any of these means the user got no real answer.
                bool failed = !sawAnyChunk || exitCode != 0 || !string.IsNullOrEmpty(extractedMsg);
                if (failed)
                {
                    if (!string.IsNullOrEmpty(rawBody))
                        try { Logger.Log("HTTP", "curl error body: " + rawBody); }
                        catch { }
                    if (!string.IsNullOrEmpty(extractedMsg))
                        try { Logger.Log("HTTP", "error.message: " + extractedMsg); }
                        catch { }
                    if (!string.IsNullOrEmpty(err))
                        try { Logger.Log("Stream", "curl stderr: " + err); }
                        catch { }

                    if (onError != null)
                    {
                        string msg = !string.IsNullOrEmpty(extractedMsg)
                            ? extractedMsg
                            : (!string.IsNullOrEmpty(err)
                                ? err
                                : (exitCode != 0 ? ("Request failed (curl exit " + exitCode + ")") : "Request failed"));
                        onError(msg.Trim());
                    }
                    if (cancel != null) cancel.Detach();
                    CleanupTempFiles(tempFiles);
                    return;
                }

                if (cancel != null) cancel.Detach();
                CleanupTempFiles(tempFiles);
            }
            catch (Exception ex)
            {
                Logger.Log("Stream", "Exception: " + ex.Message);
                if (cancel != null) cancel.Detach();
                CleanupTempFiles(tempFiles);
                // A kill mid-read can surface as an IOException here; if the user asked to stop, that's
                // expected - swallow it rather than reporting a request failure.
                if (cancel != null && cancel.IsCancelled) return;
                if (onError != null) onError(ex.Message);
            }
        }

        private string BuildCurlArgs(string jsonBody, out List<string> tempFiles)
        {
            tempFiles = new List<string>();

            try { Logger.Log("Stream", "Request JSON: " + jsonBody); }
            catch { }
            try { Logger.Log("Stream", "Request JSON length=" + (jsonBody != null ? jsonBody.Length : 0)); }
            catch { }

            string tempDir = Path.GetTempPath();
            var utf8NoBom = new UTF8Encoding(false);

            // Write the request body to a temp file to avoid command-line length/quoting issues.
            string bodyPath = Path.Combine(tempDir, "gxpt_body_" + Guid.NewGuid().ToString("N") + ".json");
            bool bodyWritten = false;
            try
            {
                File.WriteAllText(bodyPath, jsonBody ?? string.Empty, utf8NoBom);
                bodyWritten = true;
                tempFiles.Add(bodyPath);
            }
            catch (Exception ex)
            {
                // Fallback: if writing fails, pass the body inline as a last resort.
                try { Logger.Log("Stream", "Temp body write failed: " + ex.Message); }
                catch { }
            }

            // Write the Authorization header to a curl config file so the API key never appears
            // on the process command line (where any local process could read it).
            string configPath = Path.Combine(tempDir, "gxpt_cfg_" + Guid.NewGuid().ToString("N") + ".txt");
            bool configWritten = false;
            try
            {
                // curl config file directive: header = "Authorization: Bearer <key>"
                string cfg = "header = \"Authorization: Bearer " + EscapeForCurlConfig(_apiKey) + "\"\n";
                File.WriteAllText(configPath, cfg, utf8NoBom);
                configWritten = true;
                tempFiles.Add(configPath);
            }
            catch (Exception ex)
            {
                try { Logger.Log("Stream", "Auth config write failed: " + ex.Message); }
                catch { }
            }

            var sb = new StringBuilder();
            // -sS: silent but still show errors; --fail-with-body: fail on HTTP errors but keep body on stdout
            sb.Append("-sS --fail-with-body https://openrouter.ai/api/v1/chat/completions ");
            sb.Append("-H \"Content-Type: application/json\" ");
            if (configWritten)
            {
                // -K/--config reads the Authorization header from the temp file (off the command line).
                sb.Append("-K \"").Append(configPath).Append("\" ");
            }
            else
            {
                // Last-resort fallback so requests still work if the temp dir is unwritable.
                sb.Append("-H \"Authorization: Bearer ").Append(_apiKey).Append("\" ");
            }
            sb.Append("-N "); // important for streaming to disable buffering
            if (bodyWritten)
                sb.Append("-d @\"").Append(bodyPath).Append("\"");
            else
                sb.Append("-d \"").Append((jsonBody ?? string.Empty).Replace("\"", "\\\"")).Append("\"");
            try { Logger.Log("Stream", "curl args prepared for request."); }
            catch { }
            return sb.ToString();
        }

        // Escape a value for inclusion inside double quotes in a curl config file.
        // Within double quotes, curl processes the escape sequences \\, \", \t, \n, \r and \v.
        private static string EscapeForCurlConfig(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // Best-effort deletion of temp files created for a request (body and auth config).
        private static void CleanupTempFiles(IEnumerable<string> paths)
        {
            if (paths == null) return;
            foreach (var p in paths)
            {
                try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p); }
                catch { }
            }
        }

        private static JObject SafeParse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JObject.Parse(json); }
            catch { return null; }
        }

        // Extracts a human-readable error message from common JSON error shapes.
        internal static string ExtractErrorMessage(JObject root)
        {
            if (root == null) return null;
            try
            {
                // { "error": { "message": "..." } } (or error/detail)
                JToken errorObj = root["error"];
                if (errorObj != null && errorObj.Type == JTokenType.Object)
                {
                    string m = AsString(errorObj["message"]);
                    if (!string.IsNullOrEmpty(m)) return m;
                    string e2 = AsString(errorObj["error"]);
                    if (!string.IsNullOrEmpty(e2)) return e2;
                    string d = AsString(errorObj["detail"]);
                    if (!string.IsNullOrEmpty(d)) return d;
                }

                // { "message": "..." }
                string msg = AsString(root["message"]);
                if (!string.IsNullOrEmpty(msg)) return msg;
                // { "detail": "..." }
                string det = AsString(root["detail"]);
                if (!string.IsNullOrEmpty(det)) return det;
            }
            catch { }
            return null;
        }

        private static string AsString(JToken t)
        {
            if (t == null || t.Type != JTokenType.String) return null;
            return (string)t;
        }
    }

    // DTO for configuring request options per-call.
    public sealed class ClientProperties
    {
        // When null, the caller/method will set a sensible default based on the API used.
        public bool? Stream { get; set; }

        // Provider options
        public bool? ProviderDataCollectionAllowed { get; set; }
        // Zero data retention: when true, emits provider.zdr=true so OpenRouter only routes to
        // endpoints with a zero-retention policy. Null/false omits it (no effect on routing).
        public bool? Zdr { get; set; }
        public IList<string> ProviderOnly { get; set; }
        // order: provider preference (sticky cache routing). OpenRouter tries these endpoints first
        // and still falls back to others on failure (allow_fallbacks defaults true) - a preference,
        // not a pin like `only`. Callers put the provider that served the conversation's previous
        // request at the head so routing follows the warm prompt cache (caches are per provider).
        public IList<string> ProviderOrder { get; set; }
        // Invoked (at most once per request, from the stream-reading thread) with the name of the
        // provider that served the request, as reported by OpenRouter on the response chunks. Hosts
        // use it to remember the conversation's cache-warm provider for the next request's
        // ProviderOrder. Never invoked when OpenRouter omits the field.
        public Action<string> ProviderServedCallback { get; set; }
        public decimal? ProviderMaxPricePrompt { get; set; }
        public decimal? ProviderMaxPriceCompletion { get; set; }
        // tool_choice for the request ("none", "auto", ...). Null leaves it at the provider default.
        // Only emitted when a tools array is present. The orchestrator's cap wrap-up uses "none" to
        // forbid further tool calls while keeping the tools array (and so the cached prompt prefix)
        // identical to the loop's requests.
        public string ToolChoice { get; set; }
    }
}
