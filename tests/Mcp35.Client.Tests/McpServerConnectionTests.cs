using System;
using System.Collections.Generic;
using Mcp35.Client;
using Mcp35.Core.Errors;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Mcp35.Client.Tests
{
    public class McpServerConnectionTests
    {
        private static McpServerConnection NewConnection(FakeTransport t)
        {
            Implementation client = new Implementation();
            client.Name = "test-client";
            client.Version = "1.0";
            return new McpServerConnection("fake", t, client, null);
        }

        private static FakeTransport ReadyTransport(string version, bool withTools, params string[] tools)
        {
            var t = new FakeTransport();
            t.Handlers[McpMethods.Initialize] = delegate (JToken p)
            {
                return FakeTransport.Result(FakeTransport.InitializeResult(version, withTools));
            };
            t.Handlers[McpMethods.ToolsList] = delegate (JToken p)
            {
                return FakeTransport.Result(FakeTransport.ToolsListResult(null, tools));
            };
            return t;
        }

        // ---- Criterion 1: connection / handshake ----

        [Fact]
        public void Open_drives_initialize_then_initialized_and_reaches_ready()
        {
            var t = ReadyTransport("2025-06-18", true, "echo");
            var states = new List<ConnectionState>();

            using (var conn = NewConnection(t))
            {
                conn.StateChanged += delegate (object s, ConnectionStateEventArgs e) { states.Add(e.NewState); };
                conn.Open(5000);

                Assert.Equal(ConnectionState.Ready, conn.State);
                Assert.True(conn.SupportsTools);
                Assert.Equal("fake", conn.ServerInfo.ServerInfo.Name);

                // initialize was sent and initialized notification followed.
                Assert.Contains(McpMethods.Initialize, t.SentMethods);
                Assert.Contains(McpMethods.Initialized, t.Notifications);

                // state progression
                Assert.Equal(ConnectionState.Starting, states[0]);
                Assert.Equal(ConnectionState.Initializing, states[1]);
                Assert.Equal(ConnectionState.Ready, states[states.Count - 1]);
            }
        }

        [Fact]
        public void Server_without_tools_capability_has_no_tools()
        {
            var t = ReadyTransport("2025-06-18", false);
            using (var conn = NewConnection(t))
            {
                conn.Open(5000);
                Assert.False(conn.SupportsTools);
                Assert.Empty(conn.ListTools(false));
            }
        }

        // ---- Criterion 2: tools cache + paging ----

        [Fact]
        public void ListTools_follows_next_cursor_across_pages()
        {
            var t = new FakeTransport();
            t.Handlers[McpMethods.Initialize] = delegate (JToken p)
            {
                return FakeTransport.Result(FakeTransport.InitializeResult("2025-06-18", true));
            };
            int call = 0;
            t.Handlers[McpMethods.ToolsList] = delegate (JToken p)
            {
                call++;
                if (call == 1) return FakeTransport.Result(FakeTransport.ToolsListResult("page2", "a", "b"));
                return FakeTransport.Result(FakeTransport.ToolsListResult(null, "c"));
            };

            using (var conn = NewConnection(t))
            {
                conn.Open(5000); // prefetch does page 1 + 2 already
                IList<Tool> tools = conn.ListTools(false);
                Assert.Equal(3, tools.Count);
                Assert.Equal("a", tools[0].Name);
                Assert.Equal("c", tools[2].Name);
            }
        }

        [Fact]
        public void ListTools_is_cached_until_refresh()
        {
            var t = ReadyTransport("2025-06-18", true, "echo");
            using (var conn = NewConnection(t))
            {
                conn.Open(5000);
                int afterOpen = CountCalls(t, McpMethods.ToolsList);

                conn.ListTools(false); // served from cache — no new call
                Assert.Equal(afterOpen, CountCalls(t, McpMethods.ToolsList));

                conn.ListTools(true);  // forced refresh — one more call
                Assert.Equal(afterOpen + 1, CountCalls(t, McpMethods.ToolsList));
            }
        }

        [Fact]
        public void ToolsListChanged_invalidates_cache_and_raises_event()
        {
            var t = ReadyTransport("2025-06-18", true, "echo");
            using (var conn = NewConnection(t))
            {
                conn.Open(5000);
                bool raised = false;
                conn.ToolsChanged += delegate (object s, EventArgs e) { raised = true; };

                int before = CountCalls(t, McpMethods.ToolsList);
                t.RaiseInbound(McpMethods.ToolsListChanged);
                Assert.True(raised);

                conn.ListTools(false); // cache was invalidated → re-fetches
                Assert.Equal(before + 1, CountCalls(t, McpMethods.ToolsList));
            }
        }

        // ---- Criterion 3: CallTool routing ----

        [Fact]
        public void CallTool_passes_through_isError_result()
        {
            var t = ReadyTransport("2025-06-18", true, "boom");
            t.Handlers[McpMethods.ToolsCall] = delegate (JToken p)
            {
                JObject result = new JObject();
                JArray content = new JArray();
                JObject block = new JObject();
                block["type"] = "text";
                block["text"] = "it failed";
                content.Add(block);
                result["content"] = content;
                result["isError"] = true;
                return FakeTransport.Result(result);
            };

            using (var conn = NewConnection(t))
            {
                conn.Open(5000);
                CallToolResult r = conn.CallTool("boom", new JObject(), 5000);
                Assert.True(r.IsError);
                Assert.Equal("it failed", r.Content[0].Text);
            }
        }

        [Fact]
        public void CallTool_throws_on_jsonrpc_error_response()
        {
            var t = ReadyTransport("2025-06-18", true, "echo");
            t.Handlers[McpMethods.ToolsCall] = delegate (JToken p)
            {
                return FakeTransport.Error(JsonRpcErrorCodes.InvalidParams, "unknown tool");
            };

            using (var conn = NewConnection(t))
            {
                conn.Open(5000);
                McpException ex = Assert.Throws<McpException>(delegate { conn.CallTool("nope", new JObject(), 5000); });
                Assert.Equal(JsonRpcErrorCodes.InvalidParams, ex.Error.Code);
            }
        }

        [Fact]
        public void CallTool_before_ready_throws()
        {
            var t = ReadyTransport("2025-06-18", true, "echo");
            using (var conn = NewConnection(t))
            {
                // not opened
                Assert.Throws<InvalidOperationException>(delegate { conn.CallTool("echo", new JObject(), 5000); });
            }
        }

        // ---- Criterion 4: fault paths ----

        [Fact]
        public void Handshake_timeout_faults_the_connection()
        {
            var t = new FakeTransport();
            t.TimeoutEverything = true;

            using (var conn = NewConnection(t))
            {
                Assert.Throws<McpTransportException>(delegate { conn.Open(500); });
                Assert.Equal(ConnectionState.Faulted, conn.State);
            }
        }

        [Fact]
        public void Mid_call_transport_fault_propagates_and_faults()
        {
            var t = ReadyTransport("2025-06-18", true, "echo");
            using (var conn = NewConnection(t))
            {
                conn.Open(5000);
                t.ThrowOnSend = true;
                Assert.Throws<McpTransportException>(delegate { conn.CallTool("echo", new JObject(), 5000); });
            }
        }

        [Fact]
        public void Dispose_transitions_to_closed()
        {
            var t = ReadyTransport("2025-06-18", true, "echo");
            var conn = NewConnection(t);
            conn.Open(5000);
            conn.Dispose();
            Assert.Equal(ConnectionState.Closed, conn.State);
        }

        private static int CountCalls(FakeTransport t, string method)
        {
            int n = 0;
            foreach (string m in t.SentMethods) if (m == method) n++;
            return n;
        }
    }
}
