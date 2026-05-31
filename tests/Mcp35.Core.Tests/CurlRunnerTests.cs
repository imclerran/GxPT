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

        // Mirrors CurlRunner's internal status marker (kept in sync intentionally).
        private const string StatusMarker = "__GXPT_HTTP_STATUS__";

        /// <summary>
        /// Write a fake "curl" that prints the body followed by the status marker — exactly what
        /// CurlRunner's real <c>-w</c> appends to stdout. The runner then splits the marker off.
        /// </summary>
        private string FakeCurl(string body, int status)
        {
            if (IsWindows)
            {
                string path = Path.Combine(_dir, "fakecurl.cmd");
                // echo prints the body + CRLF; the second echo prints the marker line.
                string script =
                    "@echo off\r\n" +
                    "echo " + body + "\r\n" +
                    "echo " + StatusMarker + status + "\r\n";
                File.WriteAllText(path, script);
                return path;
            }
            else
            {
                string path = Path.Combine(_dir, "fakecurl.sh");
                string script =
                    "#!/bin/sh\n" +
                    "echo '" + body.Replace("'", "'\\''") + "'\n" +
                    "printf '%s%d' '" + StatusMarker + "' " + status + "\n";
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
            // The status marker must be split off, not left polluting the body.
            Assert.DoesNotContain(StatusMarker, result.Body);
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

        [Fact]
        public void Run_populates_headers_dictionary_even_when_empty()
        {
            // The fake curl ignores -D, so no headers are written, but Run must still return a
            // non-null Headers map so callers can GetHeader without null checks.
            string curl = FakeCurl("ok", 200);
            var runner = new CurlRunner(curl, null, null);
            CurlResult result = runner.Run(Req("https://example.test/x"));
            Assert.NotNull(result.Headers);
            Assert.Null(result.GetHeader("Content-Type")); // absent → null, no throw
        }

        [Fact]
        public void GetHeader_is_case_insensitive()
        {
            CurlResult r = new CurlResult();
            r.Headers = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            r.Headers["Mcp-Session-Id"] = "abc";
            Assert.Equal("abc", r.GetHeader("mcp-session-id"));
            Assert.Equal("abc", r.GetHeader("MCP-SESSION-ID"));
            Assert.Null(r.GetHeader("nope"));
        }
    }
}
