using System.Collections.Generic;
using Mcp35.Core.Json;
using Mcp35.Core.Protocol;
using Newtonsoft.Json.Linq;

namespace Mcp35.Server
{
    /// <summary>
    /// Helpers that keep tool handlers terse. A tool <em>failure</em> is a value
    /// (<see cref="Error"/> → isError = true), never an exception.
    /// </summary>
    public static class ToolResults
    {
        /// <summary>A single text content block, success.</summary>
        public static CallToolResult Text(string text)
        {
            CallToolResult r = new CallToolResult();
            r.Content = new List<ContentBlock>();
            r.Content.Add(ContentBlock.FromText(text ?? string.Empty));
            r.IsError = false;
            return r;
        }

        /// <summary>
        /// Structured output: sets <c>structuredContent</c> and also serializes a text mirror so
        /// hosts/models that only read text still see the data.
        /// </summary>
        public static CallToolResult Json(object structured)
        {
            JObject obj = structured == null ? new JObject() : JObject.FromObject(structured, McpJsonSerializer());

            CallToolResult r = new CallToolResult();
            r.Content = new List<ContentBlock>();
            r.Content.Add(ContentBlock.FromText(obj.ToString(Newtonsoft.Json.Formatting.None)));
            r.StructuredContent = obj;
            r.IsError = false;
            return r;
        }

        /// <summary>A tool-level failure the host relays to the model (isError = true).</summary>
        public static CallToolResult Error(string message)
        {
            CallToolResult r = new CallToolResult();
            r.Content = new List<ContentBlock>();
            r.Content.Add(ContentBlock.FromText(message ?? "error"));
            r.IsError = true;
            return r;
        }

        private static Newtonsoft.Json.JsonSerializer McpJsonSerializer()
        {
            return Newtonsoft.Json.JsonSerializer.Create(McpJson.Settings);
        }
    }
}
