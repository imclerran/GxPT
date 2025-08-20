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
        public string Role; // "user" | "assistant" | "system"
        public string Content;
        // Internal-only: attachments are kept in-memory for UI; not serialized in ConversationStore
        public List<AttachedFile> Attachments;

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
