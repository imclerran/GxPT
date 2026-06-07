using System;
using System.Collections.Generic;
using System.Text;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Mcp35.Server.Tests
{
    public class McpServerTests
    {
        private static McpServer NewServer()
        {
            return new McpServer(new Implementation { Name = "test", Version = "1.0" }, null);
        }

        private static JObject EchoSchema()
        {
            return SchemaBuilder.Object().Str("text", true).Build();
        }

        // ---- Criterion 1: handshake ----

        [Fact]
        public void Initialize_returns_negotiated_version_and_static_tools_capability()
        {
            var server = NewServer();
            var msgs = ServerHarness.Exchange(server, ServerHarness.Initialize(1, "2025-06-18"));

            Assert.Single(msgs);
            JObject result = (JObject)msgs[0]["result"];
            Assert.Equal("2025-06-18", (string)result["protocolVersion"]);          // echoed (>= floor)
            Assert.False((bool)result["capabilities"]["tools"]["listChanged"]);     // static set
            Assert.Equal("test", (string)result["serverInfo"]["name"]);
        }

        [Fact]
        public void Initialize_below_floor_falls_back_to_default_version()
        {
            var server = NewServer();
            var msgs = ServerHarness.Exchange(server, ServerHarness.Initialize(1, "2024-11-05"));

            JObject result = (JObject)msgs[0]["result"];
            Assert.Equal(ProtocolVersions.Default, (string)result["protocolVersion"]);
        }

        [Fact]
        public void Initialized_notification_produces_no_reply()
        {
            var server = NewServer();
            var msgs = ServerHarness.Exchange(server, ServerHarness.InitializedNotification());
            Assert.Empty(msgs);
        }

        // ---- Criterion 2: listing ----

        [Fact]
        public void ToolsList_returns_registered_tools_with_schema()
        {
            var server = NewServer();
            server.AddTool("echo", "Echoes text.", EchoSchema(),
                ctx => ToolResults.Text(ctx.Arguments.Value<string>("text")));

            var msgs = ServerHarness.Exchange(server, ServerHarness.ToolsList(2));

            JArray tools = (JArray)msgs[0]["result"]["tools"];
            Assert.Single(tools);
            Assert.Equal("echo", (string)tools[0]["name"]);
            Assert.Equal("Echoes text.", (string)tools[0]["description"]);
            // inputSchema preserved intact
            Assert.Equal("object", (string)tools[0]["inputSchema"]["type"]);
            Assert.Equal("string", (string)tools[0]["inputSchema"]["properties"]["text"]["type"]);
            Assert.Null(msgs[0]["result"]["nextCursor"]); // no paging
        }

        [Fact]
        public void ToolsList_emits_annotations_when_provided_and_omits_them_otherwise()
        {
            var server = NewServer();
            server.AddTool("peek", "Read only.", EchoSchema(), ToolAnnotations.ReadOnly(),
                ctx => ToolResults.Text("ok"));
            server.AddTool("plain", "No annotations.", EchoSchema(),
                ctx => ToolResults.Text("ok"));

            var msgs = ServerHarness.Exchange(server, ServerHarness.ToolsList(2));
            JArray tools = (JArray)msgs[0]["result"]["tools"];

            // Annotated tool carries the declared hints verbatim.
            Assert.Equal(true, (bool)tools[0]["annotations"]["readOnlyHint"]);
            Assert.Equal(false, (bool)tools[0]["annotations"]["destructiveHint"]);
            // Unannotated tool omits the field entirely (NullValueHandling.Ignore).
            Assert.Null(tools[1]["annotations"]);
        }

        [Fact]
        public void ToolsList_preserves_registration_order()
        {
            var server = NewServer();
            server.AddTool("a", "", null, ctx => ToolResults.Text("a"));
            server.AddTool("b", "", null, ctx => ToolResults.Text("b"));
            server.AddTool("c", "", null, ctx => ToolResults.Text("c"));

            var msgs = ServerHarness.Exchange(server, ServerHarness.ToolsList(1));
            JArray tools = (JArray)msgs[0]["result"]["tools"];
            Assert.Equal("a", (string)tools[0]["name"]);
            Assert.Equal("b", (string)tools[1]["name"]);
            Assert.Equal("c", (string)tools[2]["name"]);
        }

        // ---- Criterion 3: calling ----

        [Fact]
        public void ToolsCall_routes_to_handler_and_passes_arguments()
        {
            var server = NewServer();
            server.AddTool("echo", "Echoes text.", EchoSchema(),
                ctx => ToolResults.Text("you said: " + ctx.Arguments.Value<string>("text")));

            JObject args = new JObject();
            args["text"] = "hello";
            var msgs = ServerHarness.Exchange(server, ServerHarness.ToolsCall(3, "echo", args));

            JObject result = (JObject)msgs[0]["result"];
            Assert.False(result.Value<bool>("isError"));
            Assert.Equal("you said: hello", (string)result["content"][0]["text"]);
        }

        [Fact]
        public void ToolsCall_with_null_arguments_gives_handler_empty_object()
        {
            var server = NewServer();
            bool argsWereNull = true;
            server.AddTool("noargs", "", null, ctx =>
            {
                argsWereNull = ctx.Arguments == null;
                return ToolResults.Text("ok");
            });

            ServerHarness.Exchange(server, ServerHarness.ToolsCall(1, "noargs", null));
            Assert.False(argsWereNull); // normalized to {}
        }

        [Fact]
        public void ToolResults_Json_sets_structuredContent_and_text_mirror()
        {
            var server = NewServer();
            server.AddTool("data", "", null, ctx => ToolResults.Json(new Dictionary<string, object> {
                { "count", 3 }, { "ok", true }
            }));

            var msgs = ServerHarness.Exchange(server, ServerHarness.ToolsCall(1, "data", null));
            JObject result = (JObject)msgs[0]["result"];
            Assert.Equal(3, (int)result["structuredContent"]["count"]);
            Assert.True((bool)result["structuredContent"]["ok"]);
            Assert.NotNull(result["content"][0]["text"]); // text mirror present
        }

        // ---- Criterion 4: errors ----

        [Fact]
        public void Unknown_tool_is_invalid_params()
        {
            var server = NewServer();
            var msgs = ServerHarness.Exchange(server, ServerHarness.ToolsCall(4, "nope", null));
            Assert.Equal(-32602, (int)msgs[0]["error"]["code"]);
        }

        [Fact]
        public void Unknown_method_is_method_not_found()
        {
            var server = NewServer();
            var msgs = ServerHarness.Exchange(server, ServerHarness.Request(5, "resources/list", null));
            Assert.Equal(-32601, (int)msgs[0]["error"]["code"]);
        }

        [Fact]
        public void Handler_that_throws_yields_isError_and_server_survives()
        {
            var server = NewServer();
            server.AddTool("boom", "", null, ctx => { throw new InvalidOperationException("kaboom"); });

            // A second request after the throwing one must still be served (process stays alive).
            var msgs = ServerHarness.Exchange(server,
                ServerHarness.ToolsCall(1, "boom", null),
                ServerHarness.Request(2, "ping", null));

            Assert.Equal(2, msgs.Count);
            JObject callResult = (JObject)msgs[0]["result"];
            Assert.True(callResult.Value<bool>("isError"));
            Assert.Contains("kaboom", (string)callResult["content"][0]["text"]);
            // ping after the throw still answered:
            Assert.NotNull(msgs[1]["result"]);
        }

        [Fact]
        public void Ping_is_answered_with_empty_result()
        {
            var server = NewServer();
            var msgs = ServerHarness.Exchange(server, ServerHarness.Request(7, "ping", null));
            Assert.Equal(JTokenType.Object, msgs[0]["result"].Type);
            Assert.Empty(((JObject)msgs[0]["result"]).Properties());
        }

        [Fact]
        public void Duplicate_tool_name_throws()
        {
            var server = NewServer();
            server.AddTool("dup", "", null, ctx => ToolResults.Text("1"));
            Assert.Throws<ArgumentException>(() => server.AddTool("dup", "", null, ctx => ToolResults.Text("2")));
        }

        // ---- Criterion 5: framing / stdout hygiene ----

        [Fact]
        public void Each_response_is_exactly_one_line_on_stdout()
        {
            var server = NewServer();
            server.AddTool("echo", "", EchoSchema(),
                ctx => ToolResults.Text(ctx.Arguments.Value<string>("text")));

            JObject args = new JObject();
            args["text"] = "multi\nline\nvalue"; // newlines in payload must not break framing

            byte[] raw;
            var msgs = ServerHarness.Exchange(server, out raw,
                ServerHarness.Initialize(1, "2025-06-18"),
                ServerHarness.ToolsCall(2, "echo", args));

            string text = new UTF8Encoding(false, false).GetString(raw);
            // Exactly 2 responses => exactly 2 newline terminators, none embedded.
            int newlines = 0;
            foreach (char c in text) if (c == '\n') newlines++;
            Assert.Equal(2, newlines);
            Assert.Equal(2, msgs.Count);
            // and the value round-tripped despite its embedded newlines
            Assert.Equal("multi\nline\nvalue", (string)msgs[1]["result"]["content"][0]["text"]);
        }

        [Fact]
        public void OnShutdown_hooks_run_on_eof()
        {
            var server = NewServer();
            bool cleaned = false;
            server.OnShutdown(() => cleaned = true);

            ServerHarness.Exchange(server, ServerHarness.Request(1, "ping", null));
            Assert.True(cleaned); // EOF after the scripted input triggered shutdown
        }

        [Fact]
        public void Cannot_add_tool_after_run()
        {
            var server = NewServer();
            ServerHarness.Exchange(server, ServerHarness.Request(1, "ping", null));
            Assert.Throws<InvalidOperationException>(
                () => server.AddTool("late", "", null, ctx => ToolResults.Text("x")));
        }
    }
}
