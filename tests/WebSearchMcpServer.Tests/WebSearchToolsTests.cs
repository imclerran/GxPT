using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace WebSearchMcpServer.Tests
{
    public class WebSearchToolsTests : IDisposable
    {
        private static bool IsWindows
        {
            get { return Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX; }
        }

        // Mirrors CurlRunner's status marker (kept in sync intentionally).
        private const string StatusMarker = "__GXPT_HTTP_STATUS__";

        private readonly string _dir;

        public WebSearchToolsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "webmcp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>A fake curl that prints a canned JSON body + the status marker on stdout.</summary>
        private string FakeCurl(string json, int status)
        {
            string jsonFile = Path.Combine(_dir, "resp_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(jsonFile, json);

            if (IsWindows)
            {
                string path = Path.Combine(_dir, "fakecurl.cmd");
                string script =
                    "@echo off\r\n" +
                    "type \"" + jsonFile + "\"\r\n" +
                    "echo " + StatusMarker + status + "\r\n";
                File.WriteAllText(path, script);
                return path;
            }
            else
            {
                string path = Path.Combine(_dir, "fakecurl.sh");
                string script =
                    "#!/bin/sh\n" +
                    "cat \"" + jsonFile + "\"\n" +
                    "printf '%s%d' '" + StatusMarker + "' " + status + "\n";
                File.WriteAllText(path, script);
                try { System.Diagnostics.Process.Start("/bin/chmod", "+x \"" + path + "\"").WaitForExit(); } catch { }
                return path;
            }
        }

        private const string SearchResponse =
            "{\"answer\":\"An answer.\",\"results\":[" +
            "{\"title\":\"First\",\"url\":\"https://a.test\",\"content\":\"one\",\"score\":0.9}," +
            "{\"title\":\"Second\",\"url\":\"https://b.test\",\"content\":\"two\",\"score\":0.5}]}";

        private const string ExtractResponse =
            "{\"results\":[{\"url\":\"https://a.test\",\"raw_content\":\"# Heading\\nFull body text.\"}]," +
            "\"failed_results\":[]}";

        // ---- listing ----

        [Fact]
        public void Lists_search_extract_get_and_http_tools()
        {
            var server = Harness.NewWebServer("k", FakeCurl(SearchResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsList(1));

            JArray tools = (JArray)msgs[0]["result"]["tools"];
            var names = new System.Collections.Generic.List<string>();
            foreach (JToken t in tools) names.Add((string)t["name"]);
            Assert.Contains("search", names);
            Assert.Contains("extract", names);
            Assert.Contains("get", names);
            Assert.Contains("http", names);
            Assert.Equal(4, names.Count);
        }

        // ---- search handler ----

        [Fact]
        public void Search_happy_path_returns_condensed_results()
        {
            var server = Harness.NewWebServer("k", FakeCurl(SearchResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "hello")));

            Assert.False(Harness.IsError(msgs[0]));
            JObject s = Harness.Structured(msgs[0]);
            Assert.Equal("An answer.", (string)s["answer"]);
            Assert.Equal(2, ((JArray)s["results"]).Count);
            Assert.Equal("https://a.test", (string)s["results"][0]["url"]);
        }

        [Fact]
        public void Search_missing_key_returns_error()
        {
            var server = Harness.NewWebServer(null, FakeCurl(SearchResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "x")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("API key", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Search_empty_query_returns_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl(SearchResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Search_non_200_returns_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl("{\"detail\":\"unauthorized\"}", 401));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "x")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("401", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Search_bad_json_returns_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl("not json", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "x")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("parse", Harness.Text(msgs[0]));
        }

        // ---- extract handler ----

        [Fact]
        public void Extract_happy_path_returns_page_content()
        {
            var server = Harness.NewWebServer("k", FakeCurl(ExtractResponse, 200));
            JObject args = new JObject();
            args["urls"] = new JArray("https://a.test");
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "extract", args));

            Assert.False(Harness.IsError(msgs[0]));
            JObject s = Harness.Structured(msgs[0]);
            Assert.Single((JArray)s["results"]);
            Assert.Contains("Full body text", (string)s["results"][0]["raw_content"]);
        }

        [Fact]
        public void Extract_accepts_single_url_string()
        {
            var server = Harness.NewWebServer("k", FakeCurl(ExtractResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "extract", Harness.Args("urls", "https://a.test")));
            Assert.False(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Extract_no_urls_returns_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl(ExtractResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "extract", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
        }

        // ---- get handler ----

        // web__get runs the SSRF guard, which DNS-resolves hostnames; these use a public IP literal so
        // the host check is deterministic and offline (the fake curl ignores the URL anyway).
        [Fact]
        public void Get_happy_path_returns_status_and_body()
        {
            var server = Harness.NewWebServer("k", FakeCurl("{\"ok\":true}", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "get", Harness.Args("url", "https://93.184.216.34/v1")));

            Assert.False(Harness.IsError(msgs[0]));
            JObject s = Harness.Structured(msgs[0]);
            Assert.Equal(200, (int)s["status"]);
            Assert.Contains("\"ok\":true", (string)s["body"]);
        }

        [Fact]
        public void Get_works_without_tavily_key()
        {
            // The raw GET tool talks to arbitrary URLs, not Tavily, so it needs no GXPT_WEB_SEARCH_KEY.
            var server = Harness.NewWebServer(null, FakeCurl("hello", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "get", Harness.Args("url", "https://93.184.216.34/")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("hello", (string)Harness.Structured(msgs[0])["body"]);
        }

        [Fact]
        public void Get_non_http_url_returns_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl("x", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "get", Harness.Args("url", "file:///etc/passwd")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("http", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Get_missing_url_returns_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl("x", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "get", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Theory]
        [InlineData("http://127.0.0.1/")]            // loopback
        [InlineData("http://169.254.169.254/")]      // cloud metadata (link-local)
        [InlineData("http://10.0.0.5/")]             // RFC1918 private
        [InlineData("http://192.168.1.1/admin")]     // RFC1918 private
        public void Get_refuses_non_public_host(string url)
        {
            var server = Harness.NewWebServer("k", FakeCurl("secret", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "get", Harness.Args("url", url)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("public", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Http_allows_internal_host_because_it_is_gated()
        {
            // web__http carries no SSRF guard (it's gated; the user approves the exact URL), so it can
            // reach localhost / internal dev targets.
            var server = Harness.NewWebServer("k", FakeCurl("{\"ok\":true}", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "http",
                Harness.Args("url", "http://127.0.0.1:8080/api", "method", "POST")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(200, (int)Harness.Structured(msgs[0])["status"]);
        }

        // ---- http handler ----

        [Fact]
        public void Http_happy_path_returns_status_and_body()
        {
            var server = Harness.NewWebServer("k", FakeCurl("{\"ok\":true}", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "http", Harness.Args("url", "https://api.test/v1")));

            Assert.False(Harness.IsError(msgs[0]));
            JObject s = Harness.Structured(msgs[0]);
            Assert.Equal(200, (int)s["status"]);
            Assert.Contains("\"ok\":true", (string)s["body"]);
        }

        [Fact]
        public void Http_works_without_tavily_key()
        {
            // The raw HTTP tool talks to arbitrary URLs, not Tavily, so it needs no GXPT_WEB_SEARCH_KEY.
            var server = Harness.NewWebServer(null, FakeCurl("hello", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "http", Harness.Args("url", "https://api.test/")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("hello", (string)Harness.Structured(msgs[0])["body"]);
        }

        [Fact]
        public void Http_error_status_is_returned_as_data_not_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl("{\"error\":\"not found\"}", 404));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "http", Harness.Args("url", "https://api.test/missing")));

            Assert.False(Harness.IsError(msgs[0])); // 4xx is data, not a tool failure
            JObject s = Harness.Structured(msgs[0]);
            Assert.Equal(404, (int)s["status"]);
            Assert.Contains("not found", (string)s["body"]);
        }

        [Fact]
        public void Http_missing_curl_returns_error()
        {
            var server = Harness.NewWebServer("k", null);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "http", Harness.Args("url", "https://api.test/")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("curl", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Http_non_http_url_returns_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl("x", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "http", Harness.Args("url", "file:///etc/passwd")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("http", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Http_missing_url_returns_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl("x", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "http", new JObject()));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Http_unsupported_method_returns_error()
        {
            var server = Harness.NewWebServer("k", FakeCurl("x", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "http",
                Harness.Args("url", "https://api.test/", "method", "TRACE")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Http_rejects_get_and_points_to_web_get()
        {
            var server = Harness.NewWebServer("k", FakeCurl("x", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "http",
                Harness.Args("url", "https://api.test/", "method", "GET")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("web__get", Harness.Text(msgs[0]));
        }

        // ---- secret hygiene + lifecycle ----

        [Fact]
        public void Api_key_never_appears_in_output()
        {
            const string secret = "super-secret-tavily-token-xyz";
            var server = Harness.NewWebServer(secret, FakeCurl(SearchResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "hello")));
            Assert.DoesNotContain(secret, msgs[0].ToString());
        }

        [Fact]
        public void Server_survives_an_error_and_answers_next_request()
        {
            var server = Harness.NewWebServer(null, FakeCurl(SearchResponse, 200)); // no key → error
            var msgs = Harness.Exchange(server,
                Harness.ToolsCall(1, "search", Harness.Args("query", "x")),
                Harness.Request(2, "ping", null));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.NotNull(msgs[1]["result"]);
        }
    }
}
