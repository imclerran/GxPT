using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class ToolCallAssemblerTests
    {
        // ---- chunk builders ----

        private static ChatCompletionChunk TextChunk(string content, string finish)
        {
            var c = new ChatCompletionChunk();
            c.choices = new List<ChatCompletionChunk.Choice>();
            var ch = new ChatCompletionChunk.Choice();
            ch.delta = new ChatCompletionChunk.Delta { content = content };
            ch.finish_reason = finish;
            c.choices.Add(ch);
            return c;
        }

        private static ChatCompletionChunk ToolChunk(int index, string id, string name, string argsFragment, string finish)
        {
            var c = new ChatCompletionChunk();
            c.choices = new List<ChatCompletionChunk.Choice>();
            var ch = new ChatCompletionChunk.Choice();
            ch.delta = new ChatCompletionChunk.Delta();
            ch.delta.tool_calls = new List<ToolCallDelta>();
            ch.delta.tool_calls.Add(new ToolCallDelta
            {
                index = index,
                id = id,
                type = id != null ? "function" : null,
                function = new FunctionDelta { name = name, arguments = argsFragment }
            });
            ch.finish_reason = finish;
            c.choices.Add(ch);
            return c;
        }

        private static ToolCallAssembler Feed(params ChatCompletionChunk[] chunks)
        {
            var asm = new ToolCallAssembler(null);
            foreach (var c in chunks) asm.OnChunk(c);
            asm.Finish();
            return asm;
        }

        // ---- tests ----

        [Fact]
        public void Reassembles_single_fragmented_tool_call()
        {
            var asm = Feed(
                ToolChunk(0, "call_1", "files__read", "{\"pa", null),
                ToolChunk(0, null, null, "th\":\"a.txt\"}", null),
                ToolChunk(0, null, null, null, "tool_calls"));

            Assert.True(asm.ProducedToolCalls);
            Assert.Single(asm.Calls);
            Assert.Equal("call_1", asm.Calls[0].Id);
            Assert.Equal("files__read", asm.Calls[0].Name);
            Assert.Equal("{\"path\":\"a.txt\"}", asm.Calls[0].ArgumentsJson);
            Assert.False(asm.Truncated);
        }

        [Fact]
        public void Reassembles_multiple_tool_calls_by_index()
        {
            var asm = Feed(
                ToolChunk(0, "call_a", "files__read", "{}", null),
                ToolChunk(1, "call_b", "git__status", "{\"x\":1}", null),
                TextChunk(null, "tool_calls"));

            Assert.True(asm.ProducedToolCalls);
            Assert.Equal(2, asm.Calls.Count);
            Assert.Equal("call_a", asm.Calls[0].Id);
            Assert.Equal("files__read", asm.Calls[0].Name);
            Assert.Equal("call_b", asm.Calls[1].Id);
            Assert.Equal("git__status", asm.Calls[1].Name);
        }

        [Fact]
        public void Handles_whole_tool_call_in_one_chunk()
        {
            var asm = Feed(ToolChunk(0, "call_x", "web__search", "{\"q\":\"hi\"}", "tool_calls"));

            Assert.True(asm.ProducedToolCalls);
            Assert.Single(asm.Calls);
            Assert.Equal("{\"q\":\"hi\"}", asm.Calls[0].ArgumentsJson);
        }

        [Fact]
        public void Empty_arguments_default_to_object_literal()
        {
            // name arrives but no arguments fragments at all → "{}"
            var asm = Feed(
                ToolChunk(0, "call_n", "files__list", null, null),
                TextChunk(null, "tool_calls"));

            Assert.True(asm.ProducedToolCalls);
            Assert.Equal("{}", asm.Calls[0].ArgumentsJson);
        }

        [Fact]
        public void Plain_text_answer_produces_no_tool_calls()
        {
            var deltas = new List<string>();
            var asm = new ToolCallAssembler(s => deltas.Add(s));
            asm.OnChunk(TextChunk("Hello, ", null));
            asm.OnChunk(TextChunk("world.", "stop"));
            asm.Finish();

            Assert.False(asm.ProducedToolCalls);
            Assert.Equal("Hello, world.", asm.Text);
            Assert.Equal(new[] { "Hello, ", "world." }, deltas.ToArray());
        }

        [Fact]
        public void Truncated_tool_call_is_flagged()
        {
            var asm = Feed(
                ToolChunk(0, "call_t", "command__run", "{\"cmd\":\"long", null),
                TextChunk(null, "length"));

            Assert.True(asm.ProducedToolCalls);
            Assert.True(asm.Truncated);
            Assert.Equal("length", asm.FinishReason);
        }

        [Fact]
        public void Finish_is_idempotent_and_safe_without_finish_reason()
        {
            var asm = new ToolCallAssembler(null);
            asm.OnChunk(ToolChunk(0, "call_1", "files__read", "{}", null)); // no finish_reason in stream
            asm.Finish();
            asm.Finish(); // second call must not change anything

            Assert.True(asm.ProducedToolCalls);
            Assert.Single(asm.Calls);
        }
    }
}
