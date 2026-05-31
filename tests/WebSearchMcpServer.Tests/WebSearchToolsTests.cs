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
        public void Lists_search_and_extract_tools()
        {
            var server = Harness.NewWebServer("k", FakeCurl(SearchResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsList(1));

            JArray tools = (JArray)msgs[0]["result"]["tools"];
            var names = new System.Collections.Generic.List<string>();
            foreach (JToken t in tools) names.Add((string)t["name"]);
            Assert.Contains("search", names);
            Assert.Contains("extract", names);
            Assert.Equal(2, names.Count);
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
