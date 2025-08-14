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

        // Build request body for non-stream and stream=true modes
        private static string BuildRequestBody(string model, IList<ChatMessage> messages, bool stream)
        {
            var payload = new Dictionary<string, object>();
            payload["model"] = string.IsNullOrEmpty(model) ? "openai/gpt-4o" : model;
            payload["stream"] = stream;
            var msgs = new List<Dictionary<string, string>>();
            foreach (var m in messages)
            {
                msgs.Add(new Dictionary<string, string> { { "role", m.Role }, { "content", m.Content } });
            }
            payload["messages"] = msgs;
            var ser = new JavaScriptSerializer();
            return ser.Serialize(payload);
        }

        public string CreateCompletion(string model, IList<ChatMessage> messages)
        {
            string body = BuildRequestBody(model, messages, false);
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
                string output = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                // Clean up temp file
                try { if (!string.IsNullOrEmpty(dataFile) && File.Exists(dataFile)) File.Delete(dataFile); }
                catch { }
                if (!string.IsNullOrEmpty(err) && output.Length == 0)
                    throw new Exception("curl error: " + err);
                return output;
            }
        }

        public void CreateCompletionStream(string model, IList<ChatMessage> messages, Action<string> onDelta, Action onDone, Action<string> onError)
        {
            string body = BuildRequestBody(model, messages, true);
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

                // Read lines as they arrive (SSE: lines like "data: {...}")
                var sr = proc.StandardOutput;
                var er = proc.StandardError;
                string line;
                var ser = new JavaScriptSerializer();
                bool sawAnyDelta = false;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
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
                    if (!string.IsNullOrEmpty(err) && !sawAnyDelta)
                    {
                        Logger.Log("Stream", "curl stderr (no deltas): " + err);
                        if (onError != null) onError(err.Trim());
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
            sb.Append("https://openrouter.ai/api/v1/chat/completions ");
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
            sb.Append("https://openrouter.ai/api/v1/chat/completions ");
            sb.Append("-H \"Content-Type: application/json\" ");
            string apiKeyForCurl = _apiKey;
            sb.Append("-H \"Authorization: Bearer ").Append(apiKeyForCurl).Append("\" ");
            sb.Append("-N ");
            sb.Append("-d \"").Append(bodyEscaped).Append("\"");
            try { Logger.Log("Stream", "curl args prepared for request (inline fallback)."); }
            catch { }
            return sb.ToString();
        }
    }
}
