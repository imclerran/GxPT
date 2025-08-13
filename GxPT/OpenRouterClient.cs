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
            string args = BuildCurlArgs(body);

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
                if (!string.IsNullOrEmpty(err) && output.Length == 0)
                    throw new Exception("curl error: " + err);
                return output;
            }
        }

        public void CreateCompletionStream(string model, IList<ChatMessage> messages, Action<string> onDelta, Action onDone, Action<string> onError)
        {
            string body = BuildRequestBody(model, messages, true);
            string args = BuildCurlArgs(body);

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

                // Read lines as they arrive (SSE: lines like "data: {...}")
                var sr = proc.StandardOutput;
                string line;
                var ser = new JavaScriptSerializer();
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
                                if (!string.IsNullOrEmpty(content) && onDelta != null)
                                    onDelta(content);
                            }
                        }
                    }
                    catch
                    {
                        // If JSON parse fails, ignore that line
                        if (onError != null) onError("Failed to parse stream line.");
                    }
                }
                try { proc.WaitForExit(); } catch { }
                if (onDone != null) onDone();
            }
            catch (Exception ex)
            {
                if (onError != null) onError(ex.Message);
            }
        }

        private string BuildCurlArgs(string jsonBody)
        {
            // Escape quotes for curl -d "..." on Windows
            string bodyEscaped = jsonBody.Replace("\"", "\\\"");
            var sb = new StringBuilder();
            sb.Append("https://openrouter.ai/api/v1/chat/completions ");
            sb.Append("-H \"Content-Type: application/json\" ");
            sb.Append("-H \"Authorization: Bearer ").Append(_apiKey).Append("\" ");
            sb.Append("-N "); // important for streaming to disable buffering
            sb.Append("-d \"").Append(bodyEscaped).Append("\"");
            return sb.ToString();
        }
    }
}
