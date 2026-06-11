using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // Orchestrator-level coverage of the skills/hidden-tool dispatch wiring (the seam between the registry
    // filters and ExecuteCall): hidden tools are stripped from the manifest AND refused on call; open_skill
    // is dispatched locally without an MCP round-trip; an all-hidden turn injects no manifest framing.
    public sealed class OrchestratorSkillDispatchTests : IDisposable
    {
        private readonly string _root;
        private readonly string _bundled;

        public OrchestratorSkillDispatchTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_orchskill_" + Guid.NewGuid().ToString("N"));
            _bundled = Path.Combine(_root, "bundled");
            Directory.CreateDirectory(_bundled);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        private static McpChatOrchestrator New(ScriptedStreamer s, McpToolRegistry reg)
        {
            return new McpChatOrchestrator(s, reg, null, "test-model", null);
        }

        // The manifest now rides in the ephemeral context tail (a trailing user message; see the
        // prompt-caching zones in McpChatOrchestrator), so manifest assertions scan every message
        // of the request rather than just the system head.
        private static string JoinAll(IList<ChatMessage> msgs)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < msgs.Count; i++)
                if (msgs[i] != null && msgs[i].Content != null) sb.Append(msgs[i].Content).Append('\n');
            return sb.ToString();
        }

        [Fact]
        public void Hidden_tool_is_filtered_from_manifest_and_refused_on_call()
        {
            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("skills", out ft, new ToolDef("create_skill"), new ToolDef("validate_skill"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);
            ft.OnCall = delegate(string n, JObject a) { return RegistryFakeTransport.TextResult("should not run"); };

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "skills__create_skill", "{}")); // a hidden tool
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg);
            orch.HiddenToolNames = new List<string> { "skills__create_skill" };
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "go", ui);

            // The manifest lists the visible tool but not the hidden one.
            string firstReq = JoinAll(streamer.SeenMessages[0]);
            Assert.Contains("skills__validate_skill", firstReq);
            Assert.DoesNotContain("skills__create_skill", firstReq);

            // Calling the hidden tool is refused as Unknown, isError, and never reaches the transport.
            Assert.True(ui.ToolErrors[0]);
            Assert.Contains("Unknown tool", ui.ToolResults[0]);
            Assert.Empty(ft.CalledTools);
        }

        [Fact]
        public void All_tools_hidden_injects_no_manifest_framing()
        {
            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("skills", out ft, new ToolDef("create_skill"));
            var reg = new McpToolRegistry(null);
            reg.AddConnection(conn);

            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.Text("hi"));

            var orch = New(streamer, reg);
            orch.HiddenToolNames = new List<string> { "skills__create_skill" }; // the only tool -> all hidden
            orch.RunTurn(new List<ChatMessage>(), "hello", new RecordingUi());

            // No leftover framing over an empty list.
            Assert.DoesNotContain("Available tools:", JoinAll(streamer.SeenMessages[0]));
        }

        [Fact]
        public void Open_skill_is_dispatched_locally_without_an_mcp_round_trip()
        {
            string dir = Path.Combine(_bundled, "greeting");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\nname: greeting\ndescription: be a pirate\n---\n\nARRR MATEY\n", new UTF8Encoding(false));
            SkillCatalog cat = SkillCatalog.Build(_bundled, null);

            // Empty registry: the only exposed tools are the host meta-tools (open_skill/read_skill_file).
            var reg = new McpToolRegistry(null);
            var streamer = new ScriptedStreamer();
            streamer.Turns.Add(Chunks.OneToolCall("c1", "open_skill", "{\"names\":[\"greeting\"]}"));
            streamer.Turns.Add(Chunks.Text("done"));

            var orch = New(streamer, reg);
            orch.SkillTools = new SkillTools(cat.Skills, cat);
            var ui = new RecordingUi();
            orch.RunTurn(new List<ChatMessage>(), "use greeting", ui);

            // The body is returned as the (non-error) tool result, dispatched in-process.
            Assert.False(ui.ToolErrors[0]);
            Assert.Contains("ARRR MATEY", ui.ToolResults[0]);
        }
    }
}
