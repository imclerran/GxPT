using System.Collections.Generic;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class McpChatOrchestratorTests
    {
        private static McpToolRegistry RegistryWith(out RegistryFakeTransport ft, string server, params ToolDef[] tools)
        {
            var conn = FakeConn.Ready(server, out ft, tools);
            var reg = new McpToolRegistry(8, null);
            reg.AddConnection(conn);
            return reg;
        }

        private static McpChatOrchestrator New(ScriptedStreamer s, McpToolRegistry reg)
        {
            return new McpChatOrchestrator(s, reg, null, "test-model", null);
        }

        [Fact]
        public void Single_tool_call_then_result_then_final_answer()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("file contents"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("call_1", "files__read", "{\"path\":\"a.txt\"}"));
            streamer.Turns.Add(Chunks.Text("Here is the file."));

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(history, "read a.txt", ui);

            Assert.True(ui.Completed);
            Assert.Equal("Here is the file.", ui.Text.ToString());
            Assert.Equal(new[] { "files__read" }, ui.ToolCalls.ToArray());
            Assert.Contains("file contents", ui.ToolResults[0]);
            Assert.False(ui.ToolErrors[0]);
            Assert.Equal(2, streamer.Calls);

            // history: user, assistant(+tool_calls), tool(result), assistant(final)
            Assert.Equal(4, history.Count);
            Assert.Equal("user", history[0].Role);
            Assert.Equal("assistant", history[1].Role);
            Assert.NotNull(history[1].ToolCalls);
            Assert.Single(history[1].ToolCalls);
            Assert.Equal("tool", history[2].Role);
            Assert.Equal("call_1", history[2].ToolCallId);
            Assert.Contains("file contents", history[2].Content);
            Assert.Equal("assistant", history[3].Role);
            Assert.Equal("Here is the file.", history[3].Content);
        }

        [Fact]
        public void Passes_manifest_system_message_and_tools_to_streamer()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("hi"));

            New(streamer, reg).RunTurn(new List<ChatMessage>(), "hello", new RecordingUi());

            // first request: leading system message is the names manifest, history follows
            var msgs = streamer.SeenMessages[0];
            Assert.Equal("system", msgs[0].Role);
            Assert.Contains("reveal_tools", msgs[0].Content);   // manifest instructs reveal-before-call
            Assert.Contains("files__read", msgs[0].Content);     // and lists tool names
            Assert.Equal("user", msgs[1].Role);
            // exposed tools always lead with reveal_tools
            Assert.Equal("reveal_tools", (string)streamer.SeenTools[0][0]["function"]["name"]);
        }

        [Fact]
        public void Multiple_tool_calls_in_one_turn_run_serially()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"), new ToolDef("list"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("r:" + name); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(new[]
            {
                Chunks.ToolChunk(0, "c1", "files__read", "{}", null),
                Chunks.ToolChunk(1, "c2", "files__list", "{}", "tool_calls")
            });
            streamer.Turns.Add(Chunks.Text("done"));

            var history = new List<ChatMessage>();
            New(streamer, reg).RunTurn(history, "go", new RecordingUi());

            Assert.Equal(new[] { "read", "list" }, ft.CalledTools.ToArray());
            // user, assistant(2 calls), tool, tool, assistant(final)
            Assert.Equal(5, history.Count);
            Assert.Equal(2, history[1].ToolCalls.Count);
            Assert.Equal("tool", history[2].Role);
            Assert.Equal("tool", history[3].Role);
        }

        [Fact]
        public void Reveal_tools_then_follow_up_call_succeeds_locally()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("content"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("r1", "reveal_tools", "{\"names\":[\"files__read\"]}"));
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("final"));

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(history, "use the read tool", ui);

            Assert.Equal(3, streamer.Calls);
            Assert.Equal(new[] { "reveal_tools", "files__read" }, ui.ToolCalls.ToArray());
            // reveal_tools is local — only the real tool hit the transport
            Assert.Equal(new[] { "read" }, ft.CalledTools.ToArray());
            // the reveal result lists the requested def
            Assert.Contains("files__read", ui.ToolResults[0]);
            Assert.False(ui.ToolErrors[0]);
            Assert.Equal("final", ui.Text.ToString());
        }

        [Fact]
        public void Hitting_iteration_cap_wraps_up_with_a_model_message()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            for (int i = 0; i < 3; i++) streamer.Turns.Add(Chunks.OneToolCall("c" + i, "files__read", "{}"));
            // The tool-less wrap-up call (after the cap) gets this text.
            streamer.Fallback = delegate(int i) { return Chunks.Text("Summary; how should I proceed?"); };

            // cap 3, no ContinuationDecider => wrap up rather than dead-end.
            var orch = new McpChatOrchestrator(streamer, reg, null, "m", null, 3, 1000);
            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            orch.RunTurn(history, "loop forever", ui);

            Assert.True(ui.Completed);
            Assert.Equal(4, streamer.Calls);                       // 3 tool iterations + 1 tool-less wrap-up
            Assert.Null(streamer.SeenTools[3]);                    // wrap-up offers no tools
            // The wrap-up instruction must be a trailing user turn, not a system message: Anthropic
            // hoists in-array system messages out of position, leaving nothing for the model to answer.
            var wrapMsgs = streamer.SeenMessages[3];
            var lastSent = wrapMsgs[wrapMsgs.Count - 1];
            Assert.Equal("user", lastSent.Role);
            Assert.Contains("maximum number of tool calls", lastSent.Content);
            Assert.Equal("assistant", history[history.Count - 1].Role);
            Assert.Equal("Summary; how should I proceed?", history[history.Count - 1].Content);
            Assert.Contains("how should I proceed", ui.Text.ToString());
        }

        [Fact]
        public void Continuing_at_the_cap_grants_another_budget()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("x"); };

            var streamer = new ScriptedStreamer();
            streamer.Fallback = delegate(int i) { return Chunks.OneToolCall("c" + i, "files__read", "{}"); };

            var orch = new McpChatOrchestrator(streamer, reg, null, "m", null, 2, 1000);
            int asked = 0;
            orch.ContinuationDecider = delegate(int n) { asked++; return asked == 1; }; // continue once, then stop

            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.Equal(2, asked);            // asked at the first cap (granted) and the refreshed cap (stopped)
            Assert.Equal(5, streamer.Calls);   // (2 + 2) tool iterations + 1 wrap-up
            Assert.True(ui.Completed);
        }

        [Fact]
        public void Empty_response_is_retried_once_then_proceeds()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args) { return RegistryFakeTransport.TextResult("ok"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(new ChatCompletionChunk[0]);                     // empty: no text, no tool calls
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));  // retry yields a tool call
            streamer.Turns.Add(Chunks.Text("done"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.Equal(3, streamer.Calls);   // empty + retry(tool call) + final
            Assert.Equal(new[] { "files__read" }, ui.ToolCalls.ToArray());
            Assert.Equal("done", ui.Text.ToString());
            Assert.True(ui.Completed);
        }

        [Fact]
        public void Empty_response_twice_surfaces_a_notice()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(new ChatCompletionChunk[0]);
            streamer.Turns.Add(new ChatCompletionChunk[0]);

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(history, "go", ui);

            Assert.True(ui.Completed);
            Assert.Equal(2, streamer.Calls);   // initial + one retry, then surface
            Assert.Contains("empty response", history[history.Count - 1].Content.ToLowerInvariant());
            Assert.Contains("empty response", ui.Text.ToString().ToLowerInvariant());
        }

        [Fact]
        public void Tool_isError_result_is_fed_back_and_loop_continues()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.OnCall = delegate(string name, JObject args)
            {
                JObject r = RegistryFakeTransport.TextResult("boom");
                r["isError"] = true;
                return r;
            };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("recovered"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("boom", ui.ToolResults[0]);
            Assert.Equal("recovered", ui.Text.ToString());
            Assert.True(ui.Completed);
        }

        [Fact]
        public void Transport_fault_during_call_surfaces_server_unavailable()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.ThrowTransportOnCall = true;

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("after"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Equal("[Server unavailable.]", ui.ToolResults[0]);
            Assert.Equal("after", ui.Text.ToString());
        }

        [Fact]
        public void Json_rpc_error_during_call_surfaces_tool_error()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            ft.ErrorOnCall = true;

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("after"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.StartsWith("[Tool error:", ui.ToolResults[0]);
        }

        [Fact]
        public void Unknown_tool_is_reported_without_hitting_transport()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__nope", "{}"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Unknown tool", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);
        }

        [Fact]
        public void Malformed_arguments_surface_as_an_error()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{not valid json"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Invalid tool arguments", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);
        }

        [Fact]
        public void Denied_call_is_not_executed()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "files__read", "{}"));
            streamer.Turns.Add(Chunks.Text("ok"));

            var ui = new RecordingUi();
            var orch = new McpChatOrchestrator(streamer, reg, new DenyAllApprovalPolicy(), "m", null);
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            Assert.True(ui.ToolErrors[0]);
            Assert.Equal("[Call denied by user.]", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);
        }

        [Fact]
        public void RunTurn_overload_does_not_add_a_user_message()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("hi"));

            var history = new List<ChatMessage> { new ChatMessage("user", "already here") };
            New(streamer, reg).RunTurn(history, new RecordingUi());

            Assert.Equal(2, history.Count); // user(existing) + assistant(final); no duplicate user
            Assert.Equal("user", history[0].Role);
            Assert.Equal("already here", history[0].Content);
            Assert.Equal("assistant", history[1].Role);
        }

        [Fact]
        public void RequestMessageTransform_changes_sent_messages_not_history()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("ok"));

            var orch = New(streamer, reg);
            orch.RequestMessageTransform = delegate(IList<ChatMessage> h)
            {
                var outp = new List<ChatMessage>();
                foreach (var m in h) outp.Add(new ChatMessage(m.Role, m.Content + " [T]"));
                return outp;
            };

            var history = new List<ChatMessage>();
            orch.RunTurn(history, "hello", new RecordingUi());

            bool sentTransformed = false;
            foreach (var m in streamer.SeenMessages[0])
                if (m.Role == "user" && m.Content == "hello [T]") sentTransformed = true;
            Assert.True(sentTransformed);             // what's sent is transformed
            Assert.Equal("hello", history[0].Content); // persisted history is untouched
        }

        [Fact]
        public void Streaming_error_stops_the_turn()
        {
            RegistryFakeTransport ft;
            var reg = RegistryWith(out ft, "files", new ToolDef("read"));

            var streamer = new ScriptedStreamer();
            streamer.ErrorMessage = "network down";
            streamer.ErrorOnCall = 0;

            var history = new List<ChatMessage>();
            var ui = new RecordingUi();
            New(streamer, reg).RunTurn(history, "go", ui);

            Assert.Equal("network down", ui.Error);
            Assert.False(ui.Completed);
            Assert.Single(history); // only the user message was added
        }
    }

    internal sealed class DenyAllApprovalPolicy : IToolApprovalPolicy
    {
        public ApprovalDecision Check(string functionName, JObject args) { return ApprovalDecision.Deny; }
    }
}
