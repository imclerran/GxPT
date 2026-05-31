using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Mcp35.Core.Diagnostics;

namespace Mcp35.Core.Transport
{
    /// <summary>
    /// A reusable curl wrapper distilled from GxPT's OpenRouterClient (D9). HTTP plumbing for
    /// .NET 3.5, which can't reliably negotiate TLS 1.2 natively — so anything hitting a modern
    /// HTTPS endpoint (GitHub, Tavily) shells out to curl. Pure BCL + injected paths, so it lives
    /// in Core without a native build dependency. Shared by the client's HttpTransport (phase 8)
    /// and WebSearchMcpServer (phase 7). See mcp35-core-spec.md §6.
    /// </summary>
    public sealed class CurlRunner
    {
        // No BOM; the request body / config must be plain UTF-8 for curl.
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        // Decode responses tolerantly (no throw on invalid bytes).
        private static readonly UTF8Encoding Utf8Decode = new UTF8Encoding(false, false);

        private readonly string _curlPath;
        private readonly string _caBundlePath;
        private readonly ILogSink _log;

        public CurlRunner(string curlPath, string caBundlePath, ILogSink log)
        {
            _curlPath = curlPath;
            _caBundlePath = caBundlePath;
            _log = log ?? NullLogSink.Instance;
        }

        /// <summary>Run a buffered request; the full response body is read into <see cref="CurlResult.Body"/>.</summary>
        public CurlResult Run(CurlRequest req)
        {
            if (req == null) throw new ArgumentNullException("req");

            List<string> tempFiles = new List<string>();
            try
            {
                string args = BuildArgs(req, false, tempFiles);
                ProcessStartInfo psi = NewStartInfo(args);

                using (Process p = Process.Start(psi))
                {
                    string body, stderr;
                    using (StreamReader r = new StreamReader(p.StandardOutput.BaseStream, Utf8Decode, false))
                        body = r.ReadToEnd();
                    using (StreamReader e = new StreamReader(p.StandardError.BaseStream, Utf8Decode, false))
                        stderr = e.ReadToEnd();
                    WaitWithTimeout(p, req.TimeoutMs);

                    // curl's -w status marker is written to stdout after the body; split it off
                    // so it neither pollutes Body nor is mistaken for response content.
                    int status;
                    body = SplitStatusMarker(body, out status);

                    CurlResult result = new CurlResult();
                    result.Body = body;
                    result.Stderr = stderr;
                    result.HttpStatus = status;
                    return result;
                }
            }
            finally
            {
                CleanupTempFiles(tempFiles);
            }
        }

        /// <summary>
        /// Run a streaming request (adds <c>-N</c>); each stdout line is delivered to
        /// <paramref name="onLine"/> as it arrives (SSE). Calls <paramref name="onDone"/> at EOF,
        /// or <paramref name="onError"/> with curl's stderr on failure.
        /// </summary>
        public void RunStreaming(CurlRequest req, Action<string> onLine, Action onDone, Action<string> onError)
        {
            if (req == null) throw new ArgumentNullException("req");

            List<string> tempFiles = new List<string>();
            try
            {
                string args = BuildArgs(req, true, tempFiles);
                ProcessStartInfo psi = NewStartInfo(args);

                using (Process p = Process.Start(psi))
                {
                    // Drain stderr on a worker thread so it can't block the stdout pump.
                    string capturedErr = null;
                    Thread errThread = new Thread(delegate ()
                    {
                        try
                        {
                            using (StreamReader e = new StreamReader(p.StandardError.BaseStream, Utf8Decode, false))
                                capturedErr = e.ReadToEnd();
                        }
                        catch (Exception ex) { _log.Log("curl", "stderr drain: " + ex.Message); }
                    });
                    errThread.IsBackground = true;
                    errThread.Start();

                    bool sawAnyLine = false;
                    using (StreamReader r = new StreamReader(p.StandardOutput.BaseStream, Utf8Decode, false))
                    {
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            sawAnyLine = true;
                            if (onLine != null) onLine(line);
                        }
                    }

                    WaitWithTimeout(p, req.TimeoutMs);
                    errThread.Join(2000);

                    if (!sawAnyLine && !string.IsNullOrEmpty(capturedErr))
                    {
                        if (onError != null) onError(capturedErr);
                        return;
                    }
                    if (onDone != null) onDone();
                }
            }
            catch (Exception ex)
            {
                if (onError != null) onError(ex.Message);
            }
            finally
            {
                CleanupTempFiles(tempFiles);
            }
        }

        // ---- argument construction ----

        private string BuildArgs(CurlRequest req, bool streaming, List<string> tempFiles)
        {
            StringBuilder sb = new StringBuilder();
            // -sS: silent but still show errors; --fail-with-body: fail on HTTP errors but keep body.
            sb.Append("-sS --fail-with-body ");

            // For buffered requests, append the HTTP status as a trailing stdout marker we split
            // off afterwards. Skip it when streaming so it can't be mistaken for an SSE line.
            if (!streaming)
                sb.Append("-w \"").Append(StatusMarker).Append("%{http_code}\" ");

            if (!string.IsNullOrEmpty(_caBundlePath))
                sb.Append("--cacert \"").Append(_caBundlePath).Append("\" ");

            string method = string.IsNullOrEmpty(req.Method) ? "POST" : req.Method;
            sb.Append("-X ").Append(method).Append(" ");

            if (streaming) sb.Append("-N ");

            if (req.TimeoutMs > 0)
            {
                // curl wants whole seconds; round up so a sub-second cap still allows one second.
                int secs = (req.TimeoutMs + 999) / 1000;
                sb.Append("--max-time ").Append(secs.ToString(CultureInfo.InvariantCulture)).Append(" ");
            }

            // Headers go into a -K config file so secrets never touch the command line.
            if (req.Headers != null && req.Headers.Count > 0)
            {
                string cfgPath = WriteTempFile("gxpt_curlcfg_", ".txt", BuildHeaderConfig(req.Headers));
                if (cfgPath != null)
                {
                    tempFiles.Add(cfgPath);
                    sb.Append("-K \"").Append(cfgPath).Append("\" ");
                }
            }

            // Body → temp file, passed via --data-binary @file (avoids cmd-line length/quoting).
            if (req.BodyJson != null)
            {
                string bodyPath = WriteTempFile("gxpt_curlbody_", ".json", req.BodyJson);
                if (bodyPath != null)
                {
                    tempFiles.Add(bodyPath);
                    sb.Append("--data-binary @\"").Append(bodyPath).Append("\" ");
                }
            }

            sb.Append("\"").Append(req.Url).Append("\"");
            return sb.ToString();
        }

        private static string BuildHeaderConfig(IDictionary<string, string> headers)
        {
            StringBuilder cfg = new StringBuilder();
            foreach (KeyValuePair<string, string> h in headers)
            {
                // curl config: header = "Name: value"  (within quotes curl honors \\ and \")
                cfg.Append("header = \"")
                   .Append(EscapeForCurlConfig(h.Key))
                   .Append(": ")
                   .Append(EscapeForCurlConfig(h.Value))
                   .Append("\"\n");
            }
            return cfg.ToString();
        }

        // ---- helpers ----

        private ProcessStartInfo NewStartInfo(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _curlPath;
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            return psi;
        }

        private void WaitWithTimeout(Process p, int timeoutMs)
        {
            // curl's own --max-time bounds the request; this is a backstop so we never block forever.
            int wait = timeoutMs > 0 ? timeoutMs + 5000 : 0;
            if (wait > 0)
            {
                if (!p.WaitForExit(wait))
                {
                    try { if (!p.HasExited) p.Kill(); }
                    catch (Exception ex) { _log.Log("curl", "kill: " + ex.Message); }
                }
            }
            else
            {
                p.WaitForExit();
            }
        }

        /// <summary>
        /// The trailing stdout marker our <c>-w</c> appends; e.g. "__GXPT_HTTP_STATUS__200".
        /// Deliberately alphanumeric+underscore (no shell metacharacters) so it survives being
        /// emitted by any shell/echo and is vanishingly unlikely to collide with real body content.
        /// </summary>
        private const string StatusMarker = "__GXPT_HTTP_STATUS__";

        /// <summary>
        /// Find the trailing status marker in curl's stdout, parse the code, and return the body
        /// with the marker removed. If the marker is absent (e.g. curl never ran), returns the
        /// input unchanged with status 0.
        /// </summary>
        private static string SplitStatusMarker(string stdout, out int status)
        {
            status = 0;
            if (string.IsNullOrEmpty(stdout)) return stdout;

            int i = stdout.LastIndexOf(StatusMarker, StringComparison.Ordinal);
            if (i < 0) return stdout;

            int start = i + StatusMarker.Length;
            int end = start;
            while (end < stdout.Length && char.IsDigit(stdout[end])) end++;
            if (end > start)
            {
                int code;
                if (int.TryParse(stdout.Substring(start, end - start), out code)) status = code;
            }

            // Strip the marker (and a single preceding newline, if present) from the body.
            int cut = i;
            if (cut > 0 && stdout[cut - 1] == '\n') cut--;
            if (cut > 0 && stdout[cut - 1] == '\r') cut--;
            return stdout.Substring(0, cut);
        }

        private string WriteTempFile(string prefix, string ext, string content)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + ext);
                File.WriteAllText(path, content ?? string.Empty, Utf8NoBom);
                return path;
            }
            catch (Exception ex)
            {
                _log.Log("curl", "temp write failed: " + ex.Message);
                return null;
            }
        }

        private static string EscapeForCurlConfig(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private void CleanupTempFiles(IEnumerable<string> paths)
        {
            if (paths == null) return;
            foreach (string p in paths)
            {
                try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p); }
                catch (Exception ex) { _log.Log("curl", "temp cleanup: " + ex.Message); }
            }
        }
    }

    /// <summary>A curl request: URL + method + optional JSON body and headers.</summary>
    public sealed class CurlRequest
    {
        public string Url;
        public string Method = "POST";

        /// <summary>JSON body; written to a temp file and passed via <c>--data-binary @file</c>.</summary>
        public string BodyJson;

        /// <summary>Headers (incl. secrets); written to a <c>-K</c> config file, never the command line.</summary>
        public IDictionary<string, string> Headers;

        /// <summary>Adds <c>-N</c> when used with <see cref="CurlRunner.RunStreaming"/>.</summary>
        public bool Stream;

        public int TimeoutMs;
    }

    public sealed class CurlResult
    {
        public int HttpStatus;
        public string Body;
        public string Stderr;
    }
}
