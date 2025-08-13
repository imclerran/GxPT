using System;
using System.Collections.Generic;

namespace GxPT
{
    internal sealed class ChatMessage
    {
        public string Role; // "user" | "assistant" | "system"
        public string Content;

        public ChatMessage() { }
        public ChatMessage(string role, string content)
        {
            Role = role; Content = content ?? string.Empty;
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
        }

        internal sealed class Delta
        {
            public string role { get; set; }
            public string content { get; set; }
        }
    }
}
