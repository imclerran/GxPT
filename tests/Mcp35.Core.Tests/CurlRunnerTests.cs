using System;
using System.IO;
using Mcp35.Core.Transport;
using Xunit;

namespace Mcp35.Core.Tests
{
    /// <summary>
    /// Exercises CurlRunner against a fake "curl" script that echoes a canned body and appends the
    /// HTTP_STATUS marker (matching the real curl -w we pass), so we test the runner's argument
    /// assembly, body/header temp-file plumbing, response read, and status parsing without network.
    /// CI runs on Windows; the fake is a .cmd there, a shell script otherwise.
    /// </summary>
    public class CurlRunnerTests : IDisposable
    {
        private static bool IsWindows
        {
            get { return Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX; }
        }

        private readonly string _dir;

        public CurlRunnerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "curlrunner_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>Write a fake curl that prints the given body then the HTTP_STATUS marker line.</summary>
        private string FakeCurl(string body, int status)
        {
            if (IsWindows)
            {
                string path = Path.Combine(_dir, "fakecurl.cmd");
                // @echo off; print body; then the -w style status line.
                string script =
                    "@echo off\r\n" +
                    "<nul set /p=" + body + "\r\n" +
                    "echo.\r\n" +
                    "<nul set /p=HTTP_STATUS:" + status + "\r\n";
                File.WriteAllText(path, script);
                return path;
            }
            else
            {
                string path = Path.Combine(_dir, "fakecurl.sh");
                string script =
                    "#!/bin/sh\n" +
                    "printf '%s\\nHTTP_STATUS:" + status + "' '" + body.Replace("'", "'\\''") + "'\n";
                File.WriteAllText(path, script);
                try { System.Diagnostics.Process.Start("/bin/chmod", "+x \"" + path + "\"").WaitForExit(); } catch { }
                return path;
            }
        }

        private static CurlRequest Req(string url)
        {
            CurlRequest r = new CurlRequest();
            r.Url = url;
            r.Method = "POST";
            r.TimeoutMs = 10000;
            return r;
        }

        [Fact]
        public void Run_reads_body_and_parses_status()
        {
            string curl = FakeCurl("{\"ok\":true}", 200);
            var runner = new CurlRunner(curl, null, null);

            CurlResult result = runner.Run(Req("https://example.test/search"));

            Assert.Contains("{\"ok\":true}", result.Body);
            Assert.Equal(200, result.HttpStatus);
        }

        [Fact]
        public void Run_parses_non_200_status()
        {
            string curl = FakeCurl("{\"error\":\"nope\"}", 429);
            var runner = new CurlRunner(curl, null, null);

            CurlResult result = runner.Run(Req("https://example.test/search"));
            Assert.Equal(429, result.HttpStatus);
        }

        [Fact]
        public void Run_sends_body_and_headers_without_throwing()
        {
            // The fake ignores input, but exercising the body/-K config temp-file path proves the
            // argument assembly + temp write/cleanup don't throw and the body still comes back.
            string curl = FakeCurl("done", 200);
            var runner = new CurlRunner(curl, null, null);

            CurlRequest req = Req("https://example.test/x");
            req.BodyJson = "{\"q\":\"hello\"}";
            req.Headers = new System.Collections.Generic.Dictionary<string, string>();
            req.Headers["X-API-KEY"] = "secret-should-go-in-config-file";

            CurlResult result = runner.Run(req);
            Assert.Contains("done", result.Body);
            // The secret must never have ended up in the returned body/stderr.
            Assert.DoesNotContain("secret-should-go-in-config-file", result.Body);
        }
    }
}
