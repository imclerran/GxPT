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
        // Prompt-cache breakpoint (request-time only; never persisted). OpenRouterClient emits this
        // message's content as a content-part array carrying cache_control {type: ephemeral} when the
        // model's provider supports explicit caching. Set only on request-scoped message objects (the
        // orchestrator clones history messages via WithCacheControl) so flags can never accumulate in
        // persisted history across turns - Anthropic rejects requests with more than 4 breakpoints.
        public bool CacheControl;

        public ChatMessage() { }
        public ChatMessage(string role, string content)
        {
            Role = role; Content = content ?? string.Empty; Attachments = null;
        }

        public ChatMessage(string role, string content, List<AttachedFile> attachments)
        {
            Role = role; Content = content ?? string.Empty; Attachments = attachments;
        }

        // A shallow copy with the cache breakpoint set: flags a persisted history message at request
        // time without mutating it (the copy lives only in the outgoing request list).
        internal ChatMessage WithCacheControl()
        {
            var c = new ChatMessage(Role, Content, Attachments);
            c.ToolCalls = ToolCalls;
            c.ToolCallId = ToolCallId;
            c.CacheControl = true;
            return c;
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
        // The provider endpoint that served this request (e.g. "Anthropic", "Amazon Bedrock").
        // Drives sticky provider routing: prompt caches live per provider, so the next request of
        // the conversation prefers (provider.order) whoever holds the warm cache.
        public string provider { get; set; }
        public List<Choice> choices { get; set; }
        // Token accounting; arrives on the final SSE chunk when the request asked for it
        // (usage: {include: true}). Null on every other chunk.
        public UsageInfo usage { get; set; }

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

        internal sealed class UsageInfo
        {
            public int? prompt_tokens { get; set; }
            public int? completion_tokens { get; set; }
            public PromptTokensDetails prompt_tokens_details { get; set; }
        }

        // cached_tokens is the prompt-cache read count: the prompt-prefix tokens the provider served
        // from cache (billed at the provider's discounted cache-read rate). Zero across repeated
        // identical-prefix requests means a silent cache invalidator is at work.
        // cache_write_tokens is the cache write count: tokens written to a new cache entry (billed
        // at the provider's write premium). Large repeated writes are the signature of an
        // invalidator re-billing the prefix; a write also proves this endpoint caches and now holds
        // the conversation's warm entry (drives sticky provider routing from the first request).
        internal sealed class PromptTokensDetails
        {
            public int? cached_tokens { get; set; }
            public int? cache_write_tokens { get; set; }
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
