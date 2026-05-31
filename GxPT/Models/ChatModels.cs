using System;
using System.Collections.Generic;

namespace GxPT
{
    public sealed class AttachedFile
    {
        public string FileName { get; set; }
        public string Content { get; set; }

        public AttachedFile() { }
        public AttachedFile(string fileName, string content)
        {
            FileName = fileName ?? string.Empty;
            Content = content ?? string.Empty;
        }
    }

    internal sealed class ChatMessage
    {
        public string Role; // "user" | "assistant" | "system" | "tool"
        public string Content;
        // Internal-only: attachments are kept in-memory for UI; not serialized in ConversationStore
        public List<AttachedFile> Attachments;
        // Tool-call loop (phase 4): set on assistant messages that request tool calls.
        public List<ToolCall> ToolCalls;
        // Tool-call loop (phase 4): set on "tool"-role messages; the id of the call being answered.
        public string ToolCallId;

        public ChatMessage() { }
        public ChatMessage(string role, string content)
        {
            Role = role; Content = content ?? string.Empty; Attachments = null;
        }

        public ChatMessage(string role, string content, List<AttachedFile> attachments)
        {
            Role = role; Content = content ?? string.Empty; Attachments = attachments;
        }
    }

    // A single tool call requested by the assistant (phase 4 tool-call loop).
    internal sealed class ToolCall
    {
        public string Id;             // provider-assigned call id (echoed back on the tool result)
        public string Name;           // qualified function name (e.g. "files__read")
        public string ArgumentsJson;  // raw JSON string of arguments, as the model emitted it

        public ToolCall() { }
        public ToolCall(string id, string name, string argumentsJson)
        {
            Id = id; Name = name; ArgumentsJson = argumentsJson;
        }
    }

    internal sealed class ChatCompletionChunk
    {
        // For streaming parse
        public string id { get; set; }
        public string @object { get; set; }
        public long created { get; set; }
        public string model { get; set; }
        public List<Choice> choices { get; set; }

        internal sealed class Choice
        {
            public Delta delta { get; set; }
            public string finish_reason { get; set; }
        }

        internal sealed class Delta
        {
            public string role { get; set; }
            public string content { get; set; }
            public List<ToolCallDelta> tool_calls { get; set; }
        }
    }

    // A streamed fragment of a tool call; fragments are correlated by index (phase 4, §5).
    internal sealed class ToolCallDelta
    {
        public int index { get; set; }
        public string id { get; set; }
        public string type { get; set; }
        public FunctionDelta function { get; set; }
    }

    internal sealed class FunctionDelta
    {
        public string name { get; set; }
        public string arguments { get; set; }
    }
}
