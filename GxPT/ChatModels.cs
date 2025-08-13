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
        public string id;
        public string @object;
        public long created;
        public string model;
        public List<Choice> choices;

        internal sealed class Choice
        {
            public Delta delta;
        }

        internal sealed class Delta
        {
            public string role;
            public string content;
        }
    }
}
