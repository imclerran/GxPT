using System.Collections.Generic;
using GxPT;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    // Golden-JSON assertions for the Newtonsoft-migrated request builder (D16), plus chunk-parsing
    // checks proving the streaming DTO maps correctly under Newtonsoft. (The curl/SSE transport
    // itself needs a network and is exercised manually.)
    public class OpenRouterClientTests
    {
        // ---- BuildRequestBody: the no-tools path is unchanged in shape ----

        [Fact]
        public void No_tools_request_has_system_then_messages_and_no_tools_key()
        {
            var messages = new List<ChatMessage> { new ChatMessage("user", "hi") };
            var body = OpenRouterClient.BuildRequestBody("openai/gpt-4o", messages, null, new ClientProperties { Stream = true });
            var o = JObject.Parse(body);

            Assert.Equal("openai/gpt-4o", (string)o["model"]);
            Assert.True((bool)o["stream"]);

            var msgs = (JArray)o["messages"];
            Assert.Equal(2, msgs.Count);
            Assert.Equal("system", (string)msgs[0]["role"]);
            Assert.Contains("Do not use emojis", (string)msgs[0]["content"]);
            Assert.Equal("user", (string)msgs[1]["role"]);
            Assert.Equal("hi", (string)msgs[1]["content"]);

            Assert.Null(o["tools"]);       // absent when none provided
            Assert.Null(o["provider"]);    // absent when no provider options
        }

        [Fact]
        public void Defaults_model_when_empty()
        {
            var body = OpenRouterClient.BuildRequestBody(null, new List<ChatMessage>(), null, new ClientProperties());
            Assert.Equal("openai/gpt-4o", (string)JObject.Parse(body)["model"]);
        }

        // ---- tools / tool_calls / tool messages ----

        [Fact]
        public void Tools_array_is_emitted_when_provided()
        {
            var tools = new List<JObject>
            {
                JObject.Parse("{\"type\":\"function\",\"function\":{\"name\":\"files__read\",\"description\":\"d\",\"parameters\":{\"type\":\"object\"}}}")
            };
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>(), tools, new ClientProperties());
            var t = (JArray)JObject.Parse(body)["tools"];
            Assert.Single(t);
            Assert.Equal("function", (string)t[0]["type"]);
            Assert.Equal("files__read", (string)t[0]["function"]["name"]);
        }

        [Fact]
        public void Assistant_tool_calls_are_serialized_with_null_content()
        {
            var asst = new ChatMessage("assistant", "");
            asst.ToolCalls = new List<ToolCall> { new ToolCall("call_1", "files__read", "{\"path\":\"a\"}") };
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage> { asst }, null, new ClientProperties());

            var msg = (JObject)((JArray)JObject.Parse(body)["messages"])[1];
            Assert.Equal("assistant", (string)msg["role"]);
            Assert.Equal(JTokenType.Null, msg["content"].Type);

            var calls = (JArray)msg["tool_calls"];
            Assert.Single(calls);
            Assert.Equal("call_1", (string)calls[0]["id"]);
            Assert.Equal("function", (string)calls[0]["type"]);
            Assert.Equal("files__read", (string)calls[0]["function"]["name"]);
            Assert.Equal("{\"path\":\"a\"}", (string)calls[0]["function"]["arguments"]);
        }

        [Fact]
        public void Empty_tool_call_arguments_become_object_literal()
        {
            var asst = new ChatMessage("assistant", "text");
            asst.ToolCalls = new List<ToolCall> { new ToolCall("c", "files__list", null) };
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage> { asst }, null, new ClientProperties());
            var calls = (JArray)((JArray)JObject.Parse(body)["messages"])[1]["tool_calls"];
            Assert.Equal("{}", (string)calls[0]["function"]["arguments"]);
        }

        [Fact]
        public void Tool_role_message_has_tool_call_id()
        {
            var tool = new ChatMessage("tool", "result text");
            tool.ToolCallId = "call_1";
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage> { tool }, null, new ClientProperties());

            var msg = (JObject)((JArray)JObject.Parse(body)["messages"])[1];
            Assert.Equal("tool", (string)msg["role"]);
            Assert.Equal("result text", (string)msg["content"]);
            Assert.Equal("call_1", (string)msg["tool_call_id"]);
        }

        // ---- provider options (unchanged behavior) ----

        [Fact]
        public void Provider_options_are_emitted()
        {
            var props = new ClientProperties
            {
                ProviderDataCollectionAllowed = false,
                ProviderOnly = new List<string> { "openai" },
                ProviderMaxPricePrompt = 1.5m
            };
            var body = OpenRouterClient.BuildRequestBody("m", new List<ChatMessage>(), null, props);
            var p = (JObject)JObject.Parse(body)["provider"];

            Assert.Equal("deny", (string)p["data_collection"]);
            Assert.Equal("openai", (string)((JArray)p["only"])[0]);
            Assert.Equal(1.5m, (decimal)p["max_price"]["prompt"]);
        }

        // ---- streaming chunk parsing under Newtonsoft ----

        [Fact]
        public void Parses_streamed_content_chunk()
        {
            var json = "{\"id\":\"x\",\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}";
            var chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(json);
            Assert.Equal("Hello", chunk.choices[0].delta.content);
            Assert.Null(chunk.choices[0].finish_reason);
        }

        [Fact]
        public void Parses_streamed_tool_call_chunk()
        {
            var json = "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"type\":\"function\",\"function\":{\"name\":\"files__read\",\"arguments\":\"{}\"}}]},\"finish_reason\":\"tool_calls\"}]}";
            var chunk = JsonConvert.DeserializeObject<ChatCompletionChunk>(json);
            var choice = chunk.choices[0];
            Assert.Equal("tool_calls", choice.finish_reason);
            var tc = choice.delta.tool_calls[0];
            Assert.Equal(0, tc.index);
            Assert.Equal("c1", tc.id);
            Assert.Equal("files__read", tc.function.name);
            Assert.Equal("{}", tc.function.arguments);
        }
    }
}
