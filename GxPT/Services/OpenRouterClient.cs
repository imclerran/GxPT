using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GxPT
{
    internal sealed class OpenRouterClient
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

        // Build request body using provided client properties (including stream and provider options)
        private static string BuildRequestBody(string model, IList<ChatMessage> messages, ClientProperties props)
        {
            var payload = new Dictionary<string, object>();
            payload["model"] = string.IsNullOrEmpty(model) ? "openai/gpt-4o" : model;
            bool streamFlag = (props != null && props.Stream.HasValue) ? props.Stream.Value : false;
            payload["stream"] = streamFlag;
            var msgs = new List<Dictionary<string, string>>();
            // Prepend a system instruction to avoid emoji in all responses
            msgs.Add(new Dictionary<string, string> {
                { "role", "system" },
                { "content", "Do not use emojis or emoticons in any response. Use plain text only." }
            });
            if (messages != null)
            {
                foreach (var m in messages)
                {
                    msgs.Add(new Dictionary<string, string> { { "role", m.Role }, { "content", m.Content } });
                }
            }
            payload["messages"] = msgs;

            // Optionally add provider object if any of the provider properties are set
            try
            {
                if (props == null)
                {
                    var ser0 = new JavaScriptSerializer();
                    return ser0.Serialize(payload);
                }
                var provider = new Dictionary<string, object>();

                // data_collection: include if set (true => allow, false => deny)
                if (props.ProviderDataCollectionAllowed.HasValue)
                {
                    provider["data_collection"] = props.ProviderDataCollectionAllowed.Value ? "allow" : "deny";
                }

                // only: include non-empty list
                if (props.ProviderOnly != null && props.ProviderOnly.Count > 0)
                {
                    // copy to a new list to avoid serializing non-generic types
                    var onlyList = new List<string>();
                    foreach (var s in props.ProviderOnly)
                    {
                        if (!string.IsNullOrEmpty(s)) onlyList.Add(s);
                    }
                    if (onlyList.Count > 0)
                        provider["only"] = onlyList;
                }

                // max_price: include any provided fields
                var maxPrice = new Dictionary<string, object>();
                if (props.ProviderMaxPricePrompt.HasValue)
                    maxPrice["prompt"] = props.ProviderMaxPricePrompt.Value;
                if (props.ProviderMaxPriceCompletion.HasValue)
                    maxPrice["completion"] = props.ProviderMaxPriceCompletion.Value;
                if (maxPrice.Count > 0)
                    provider["max_price"] = maxPrice;

                if (provider.Count > 0)
                {
                    payload["provider"] = provider;
                }
            }
            catch { }
            var ser = new JavaScriptSerializer();
            return ser.Serialize(payload);
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
            string body = BuildRequestBody(model, messages, props);
            string dataFile;
            string args = BuildCurlArgs(body, out dataFile);

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
                // Clean up temp file
                try { if (!string.IsNullOrEmpty(dataFile) && File.Exists(dataFile)) File.Delete(dataFile); }
                catch { }
                // If curl signaled an HTTP error but kept the body (with --fail-with-body), log the body and any extracted message for debugging
                try
                {
                    if (!string.IsNullOrEmpty(err) && output.Length > 0)
                    {
                        Logger.Log("HTTP", "curl error body: " + output);
                        try
                        {
                            var ser2 = new JavaScriptSerializer();
                            var dict2 = ser2.Deserialize<Dictionary<string, object>>(output);
                            var msg2 = ExtractErrorMessage(dict2);
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

        public void CreateCompletionStream(string model, IList<ChatMessage> messages, Action<string> onDelta, Action onDone, Action<string> onError)
        {
            // Backward-compatible API: defaults to stream
            CreateCompletionStream(model, messages, null, onDelta, onDone, onError);
        }

        // New overload accepting ClientProperties
        public void CreateCompletionStream(string model, IList<ChatMessage> messages, ClientProperties props, Action<string> onDelta, Action onDone, Action<string> onError)
        {
            if (props == null) props = new ClientProperties();
            // Ensure stream flag defaults to true for stream API
            if (!props.Stream.HasValue) props.Stream = true;
            string body = BuildRequestBody(model, messages, props);
            string dataFile;
            string args = BuildCurlArgs(body, out dataFile);

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
                Logger.Log("Stream", "Starting curl for model=" + model + ", messages=" + (messages != null ? messages.Count : 0));
                proc.Start();

                // Read lines as they arrive (SSE: lines like "data: {...}") with explicit UTF-8 decoding
                var utf8 = new UTF8Encoding(false, false);
                var sr = new StreamReader(proc.StandardOutput.BaseStream, utf8, false);
                var er = new StreamReader(proc.StandardError.BaseStream, utf8, false);
                string line;
                var ser = new JavaScriptSerializer();
                bool sawAnyDelta = false;
                var stdoutBuf = new StringBuilder();
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    // Buffer all stdout; if no deltas are seen, we'll inspect this for a JSON error body.
                    if (line.Length > 0)
                    {
                        try { stdoutBuf.AppendLine(line); }
                        catch { }
                    }
                    if (line.Length == 0) continue;
                    if (line.StartsWith("data:")) line = line.Substring(5).Trim();
                    if (line == "[DONE]") break;
                    try
                    {
                        var chunk = ser.Deserialize<ChatCompletionChunk>(line);
                        if (chunk != null && chunk.choices != null)
                        {
                            foreach (var ch in chunk.choices)
                            {
                                string content = (ch != null && ch.delta != null) ? ch.delta.content : null;
                                if (!string.IsNullOrEmpty(content))
                                {
                                    sawAnyDelta = true;
                                    if (onDelta != null) onDelta(content);
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Ignore malformed keepalive/heartbeat lines
                        continue;
                    }
                }
                try { proc.WaitForExit(); }
                catch { }
                // If process wrote errors and we produced no deltas, surface error
                try
                {
                    string err = er.ReadToEnd();
                    if (!sawAnyDelta)
                    {
                        // Prefer any JSON error body sent on stdout (with --fail-with-body)
                        string rawBody = null;
                        try { rawBody = stdoutBuf.ToString().Trim(); }
                        catch { }

                        string userMsg = null;
                        string extractedMsg = null;
                        if (!string.IsNullOrEmpty(rawBody))
                        {
                            try
                            {
                                // Attempt to parse common error shapes
                                var dict = ser.Deserialize<Dictionary<string, object>>(rawBody);
                                extractedMsg = ExtractErrorMessage(dict);
                                userMsg = extractedMsg ?? rawBody;
                                try { Logger.Log("HTTP", "curl error body (no deltas): " + rawBody); }
                                catch { }
                                try
                                {
                                    if (!string.IsNullOrEmpty(extractedMsg))
                                        Logger.Log("HTTP", "error.message: " + extractedMsg);
                                }
                                catch { }
                            }
                            catch
                            {
                                // Not JSON; fall back to raw body text
                                userMsg = rawBody;
                                try { Logger.Log("HTTP", "curl non-JSON body (no deltas): " + rawBody); }
                                catch { }
                            }
                        }

                        // Log stderr too for diagnostics
                        if (!string.IsNullOrEmpty(err))
                        {
                            try { Logger.Log("Stream", "curl stderr (no deltas): " + err); }
                            catch { }
                        }

                        if (onError != null)
                        {
                            // Prefer API error.message if present; otherwise fall back to curl's stderr summary
                            var msg = !string.IsNullOrEmpty(extractedMsg) ? extractedMsg : (!string.IsNullOrEmpty(err) ? err : "Request failed");
                            onError(msg.Trim());
                        }
                        // Clean up temp file
                        try { if (!string.IsNullOrEmpty(dataFile) && File.Exists(dataFile)) File.Delete(dataFile); }
                        catch { }
                        return;
                    }
                }
                catch { }

                if (onDone != null) onDone();
                // Clean up temp file
                try { if (!string.IsNullOrEmpty(dataFile) && File.Exists(dataFile)) File.Delete(dataFile); }
                catch { }
            }
            catch (Exception ex)
            {
                Logger.Log("Stream", "Exception: " + ex.Message);
                if (onError != null) onError(ex.Message);
            }
        }

        private string BuildCurlArgs(string jsonBody, out string dataFilePath)
        {
            // Escape quotes for curl -d "..." on Windows
            try { Logger.Log("Stream", "Request JSON: " + jsonBody); }
            catch { }
            try { Logger.Log("Stream", "Request JSON length=" + (jsonBody != null ? jsonBody.Length : 0)); }
            catch { }

            // Write body to a temp file to avoid command-line length/quoting issues
            string tempDir = Path.GetTempPath();
            string fileName = "gxpt_body_" + Guid.NewGuid().ToString("N") + ".json";
            string filePath = Path.Combine(tempDir, fileName);
            try
            {
                var utf8NoBom = new UTF8Encoding(false);
                File.WriteAllText(filePath, jsonBody ?? string.Empty, utf8NoBom);
            }
            catch (Exception ex)
            {
                // Fallback: if writing fails, we still try inline (last resort)
                try { Logger.Log("Stream", "Temp body write failed: " + ex.Message); }
                catch { }
                dataFilePath = null;
                return BuildCurlArgsInline(jsonBody);
            }
            dataFilePath = filePath;

            var sb = new StringBuilder();
            // -sS: silent but still show errors; --fail-with-body: fail on HTTP errors but keep body on stdout
            sb.Append("-sS --fail-with-body https://openrouter.ai/api/v1/chat/completions ");
            sb.Append("-H \"Content-Type: application/json\" ");
            string apiKeyForCurl = _apiKey;
            sb.Append("-H \"Authorization: Bearer ").Append(apiKeyForCurl).Append("\" ");
            sb.Append("-N "); // important for streaming to disable buffering
            // Use @file to pass the body
            sb.Append("-d @\"").Append(filePath).Append("\"");
            try { Logger.Log("Stream", "curl args prepared for request."); }
            catch { }
            return sb.ToString();
        }

        // Last-resort inline args builder, used only if temp-file write fails
        private string BuildCurlArgsInline(string jsonBody)
        {
            string bodyEscaped = (jsonBody ?? string.Empty).Replace("\"", "\\\"");
            var sb = new StringBuilder();
            // -sS: silent but still show errors; --fail-with-body: fail on HTTP errors but keep body on stdout
            sb.Append("-sS --fail-with-body https://openrouter.ai/api/v1/chat/completions ");
            sb.Append("-H \"Content-Type: application/json\" ");
            string apiKeyForCurl = _apiKey;
            sb.Append("-H \"Authorization: Bearer ").Append(apiKeyForCurl).Append("\" ");
            sb.Append("-N ");
            sb.Append("-d \"").Append(bodyEscaped).Append("\"");
            try { Logger.Log("Stream", "curl args prepared for request (inline fallback)."); }
            catch { }
            return sb.ToString();
        }

        // Extracts a human-readable error message from common JSON error shapes (C# 3.0 compatible)
        private static string ExtractErrorMessage(Dictionary<string, object> root)
        {
            if (root == null) return null;
            try
            {
                // { "error": { "message": "..." } }
                object errorObj;
                if (root.TryGetValue("error", out errorObj))
                {
                    var errDict = errorObj as Dictionary<string, object>;
                    if (errDict != null)
                    {
                        object v;
                        if (errDict.TryGetValue("message", out v))
                        {
                            var s = v as string;
                            if (!string.IsNullOrEmpty(s)) return s;
                        }
                        if (errDict.TryGetValue("error", out v))
                        {
                            var s2 = v as string;
                            if (!string.IsNullOrEmpty(s2)) return s2;
                        }
                        if (errDict.TryGetValue("detail", out v))
                        {
                            var s3 = v as string;
                            if (!string.IsNullOrEmpty(s3)) return s3;
                        }
                    }
                }

                // { "message": "..." }
                object msgVal;
                if (root.TryGetValue("message", out msgVal))
                {
                    var msgStr = msgVal as string;
                    if (!string.IsNullOrEmpty(msgStr)) return msgStr;
                }
                // { "detail": "..." }
                object detVal;
                if (root.TryGetValue("detail", out detVal))
                {
                    var detStr = detVal as string;
                    if (!string.IsNullOrEmpty(detStr)) return detStr;
                }
            }
            catch { }
            return null;
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
