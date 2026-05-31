using System;
using System.IO;
using Newtonsoft.Json.Linq;
using SerperMcpServer;
using Xunit;

namespace SerperMcpServer.Tests
{
    public class SerperToolsTests : IDisposable
    {
        private static bool IsWindows
        {
            get { return Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX; }
        }

        // Mirrors CurlRunner's status marker (kept in sync intentionally).
        private const string StatusMarker = "__GXPT_HTTP_STATUS__";

        private readonly string _dir;

        public SerperToolsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "serpermcp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        /// <summary>A fake curl that prints a canned JSON body + the status marker on stdout.</summary>
        private string FakeCurl(string json, int status)
        {
            if (IsWindows)
            {
                string path = Path.Combine(_dir, "fakecurl.cmd");
                // Write the canned JSON to a sibling file and 'type' it (avoids cmd quoting hell),
                // then echo the status marker line.
                string jsonFile = Path.Combine(_dir, "resp.json");
                File.WriteAllText(jsonFile, json);
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
                string jsonFile = Path.Combine(_dir, "resp.json");
                File.WriteAllText(jsonFile, json);
                string script =
                    "#!/bin/sh\n" +
                    "cat \"" + jsonFile + "\"\n" +
                    "printf '%s%d' '" + StatusMarker + "' " + status + "\n";
                File.WriteAllText(path, script);
                try { System.Diagnostics.Process.Start("/bin/chmod", "+x \"" + path + "\"").WaitForExit(); } catch { }
                return path;
            }
        }

        private const string SampleResponse =
            "{\"organic\":[" +
            "{\"title\":\"First\",\"link\":\"https://a.test\",\"snippet\":\"one\",\"position\":1}," +
            "{\"title\":\"Second\",\"link\":\"https://b.test\",\"snippet\":\"two\"}]," +
            "\"answerBox\":{\"answer\":\"42\",\"snippet\":\"the answer\"}," +
            "\"knowledgeGraph\":{\"title\":\"Thing\",\"type\":\"Concept\",\"description\":\"desc\"}}";

        // ---- Criterion 1: listing ----

        [Fact]
        public void Lists_the_search_tool_with_schema()
        {
            var server = Harness.NewSerperServer("test-key", FakeCurl(SampleResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsList(1));

            JArray tools = (JArray)msgs[0]["result"]["tools"];
            Assert.Single(tools);
            Assert.Equal("search", (string)tools[0]["name"]);
            Assert.Equal("string", (string)tools[0]["inputSchema"]["properties"]["query"]["type"]);
        }

        // ---- Criterion 3: happy path + condense ----

        [Fact]
        public void Search_happy_path_returns_condensed_structured_results()
        {
            var server = Harness.NewSerperServer("test-key", FakeCurl(SampleResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "hello")));

            Assert.False(Harness.IsError(msgs[0]));
            JObject s = Harness.Structured(msgs[0]);
            JArray organic = (JArray)s["organic"];
            Assert.Equal(2, organic.Count);
            Assert.Equal("First", (string)organic[0]["title"]);
            Assert.Equal("https://a.test", (string)organic[0]["link"]);
            // condensed: position dropped, only title/link/snippet retained
            Assert.Null(organic[0]["position"]);
            Assert.Equal("42", (string)s["answerBox"]["answer"]);
            Assert.Equal("Thing", (string)s["knowledgeGraph"]["title"]);
        }

        [Fact]
        public void Condense_is_pure_and_projects_only_expected_fields()
        {
            JObject raw = JObject.Parse(SampleResponse);
            JObject c = SerperTools.Condense(raw);

            Assert.Equal(2, ((JArray)c["organic"]).Count);
            foreach (JToken o in (JArray)c["organic"])
            {
                // exactly title/link/snippet
                Assert.NotNull(o["title"]);
                Assert.NotNull(o["link"]);
                Assert.Null(o["position"]);
            }
        }

        [Fact]
        public void Condense_handles_missing_sections()
        {
            JObject raw = JObject.Parse("{\"searchParameters\":{\"q\":\"x\"}}"); // no organic/boxes
            JObject c = SerperTools.Condense(raw);
            Assert.Empty((JArray)c["organic"]);
            Assert.Null(c["answerBox"]);
            Assert.Null(c["knowledgeGraph"]);
        }

        // ---- Criterion 3: failure modes ----

        [Fact]
        public void Missing_key_returns_error()
        {
            var server = Harness.NewSerperServer(null, FakeCurl(SampleResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "x")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("API key", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Missing_curl_returns_error()
        {
            var server = Harness.NewSerperServer("test-key", null);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "x")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("curl", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Non_200_status_returns_error()
        {
            var server = Harness.NewSerperServer("test-key", FakeCurl("{\"message\":\"rate limited\"}", 429));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "x")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("429", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Unparseable_response_returns_error()
        {
            var server = Harness.NewSerperServer("test-key", FakeCurl("not json at all", 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "x")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("parse", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Empty_query_returns_error()
        {
            var server = Harness.NewSerperServer("test-key", FakeCurl(SampleResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Api_key_never_appears_in_output()
        {
            const string secret = "super-secret-serper-key-12345";
            var server = Harness.NewSerperServer(secret, FakeCurl(SampleResponse, 200));
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "search", Harness.Args("query", "hello")));

            string all = msgs[0].ToString();
            Assert.DoesNotContain(secret, all);
        }
    }
}
