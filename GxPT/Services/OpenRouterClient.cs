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
                        mm["content"] = m.Content != null ? m.Content : string.Empty;
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
                        mm["content"] = m.Content != null ? m.Content : string.Empty;
                    }
                    msgs.Add(mm);
                }
            }
            payload["messages"] = msgs;

            // Expose tools (tool_choice left at the provider default of "auto").
            if (tools != null && tools.Count > 0)
            {
                var toolList = new List<object>();
                foreach (var t in tools) toolList.Add(t);
                payload["tools"] = toolList;
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

                    // only: include non-empty list
                    if (props.ProviderOnly != null && props.ProviderOnly.Count > 0)
                    {
                        var onlyList = new List<string>();
                        foreach (var s in props.ProviderOnly)
                            if (!string.IsNullOrEmpty(s)) onlyList.Add(s);
                        if (onlyList.Count > 0) provider["only"] = onlyList;
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
                // Clean up temp files (request body and auth config)
                CleanupTempFiles(tempFiles);
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
                               ClientProperties props, Action<ChatCompletionChunk> onChunk, Action<string> onError)
        {
            if (props == null) props = new ClientProperties();
            if (!props.Stream.HasValue) props.Stream = true;
            string body = BuildRequestBody(model, messages, tools, props);
            StreamRawChunks(body, onChunk, onError);
        }

        public void CreateCompletionStream(string model, IList<ChatMessage> messages, Action<string> onDelta, Action onDone, Action<string> onError)
        {
            // Backward-compatible API: defaults to stream
            CreateCompletionStream(model, messages, null, onDelta, onDone, onError);
        }

        // Text-only streaming wrapper over StreamRawChunks (no tools): forwards content deltas.
        public void CreateCompletionStream(string model, IList<ChatMessage> messages, ClientProperties props, Action<string> onDelta, Action onDone, Action<string> onError)
        {
            if (props == null) props = new ClientProperties();
            if (!props.Stream.HasValue) props.Stream = true;
            string body = BuildRequestBody(model, messages, null, props);

            bool failed = false;
            StreamRawChunks(body,
                delegate(ChatCompletionChunk chunk)
                {
                    if (chunk != null && chunk.choices != null)
                    {
                        foreach (var ch in chunk.choices)
                        {
                            string content = (ch != null && ch.delta != null) ? ch.delta.content : null;
                            if (!string.IsNullOrEmpty(content) && onDelta != null) onDelta(content);
                        }
                    }
                },
                delegate(string err) { failed = true; if (onError != null) onError(err); });

            if (!failed && onDone != null) onDone();
        }

        // The shared curl + SSE streaming loop. Each "data:" line is parsed (Newtonsoft) into a
        // ChatCompletionChunk and handed to onChunk; if the whole stream yields no chunks, the body
        // is inspected for a JSON error and onError is called.
        public void StreamRawChunks(string body, Action<ChatCompletionChunk> onChunk, Action<string> onError)
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

                // Read lines as they arrive (SSE: lines like "data: {...}") with explicit UTF-8 decoding
                var utf8 = new UTF8Encoding(false, false);
                var sr = new StreamReader(proc.StandardOutput.BaseStream, utf8, false);
                var er = new StreamReader(proc.StandardError.BaseStream, utf8, false);
                string line;
                bool sawAnyChunk = false;
                var stdoutBuf = new StringBuilder();
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    // Buffer all stdout; if no chunks are seen, we'll inspect this for a JSON error body.
                    if (line.Length > 0)
                    {
                        try { stdoutBuf.AppendLine(line); }
                        catch { }
                    }
                    if (line.Length == 0) continue;
                    if (line.StartsWith("data:")) line = line.Substring(5).Trim();
                    if (line == "[DONE]") break;

                    ChatCompletionChunk chunk = null;
                    try { chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(line); }
                    catch { continue; } // Ignore malformed keepalive/heartbeat lines
                    if (chunk != null)
                    {
                        sawAnyChunk = true;
                        if (onChunk != null) onChunk(chunk);
                    }
                }
                try { proc.WaitForExit(); }
                catch { }

                // If the process produced no chunks, surface an error from the body/stderr.
                string err = null;
                try { err = er.ReadToEnd(); }
                catch { }
                if (!sawAnyChunk)
                {
                    string rawBody = null;
                    try { rawBody = stdoutBuf.ToString().Trim(); }
                    catch { }

                    string extractedMsg = null;
                    if (!string.IsNullOrEmpty(rawBody))
                    {
                        try { extractedMsg = ExtractErrorMessage(SafeParse(rawBody)); }
                        catch { }
                        try { Logger.Log("HTTP", "curl error body (no chunks): " + rawBody); }
                        catch { }
                        if (!string.IsNullOrEmpty(extractedMsg))
                            try { Logger.Log("HTTP", "error.message: " + extractedMsg); }
                            catch { }
                    }
                    if (!string.IsNullOrEmpty(err))
                        try { Logger.Log("Stream", "curl stderr (no chunks): " + err); }
                        catch { }

                    if (onError != null)
                    {
                        var msg = !string.IsNullOrEmpty(extractedMsg) ? extractedMsg : (!string.IsNullOrEmpty(err) ? err : "Request failed");
                        onError(msg.Trim());
                    }
                    CleanupTempFiles(tempFiles);
                    return;
                }

                CleanupTempFiles(tempFiles);
            }
            catch (Exception ex)
            {
                Logger.Log("Stream", "Exception: " + ex.Message);
                CleanupTempFiles(tempFiles);
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
        public IList<string> ProviderOnly { get; set; }
        public decimal? ProviderMaxPricePrompt { get; set; }
        public decimal? ProviderMaxPriceCompletion { get; set; }
    }
}
