using System.Collections.Generic;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace MemoryMcpServer
{
    /// <summary>
    /// The Memory server's tools. read_memory is ReadOnly; remember/update_memory/forget/consolidate
    /// are Write (host-gated by the approval tiers). The append-vs-replace distinction between
    /// remember (additive) and update_memory (replaces) is spelled out in the descriptions so the
    /// model can never clobber when it means to add. All tools are purely mechanical - no LLM calls;
    /// the model composes the content and these just write it.
    /// </summary>
    internal static class MemoryTools
    {
        public static void Register(McpServer server, MemoryConfig config)
        {
            MemoryStore store = new MemoryStore(config.MemoryRoot, config.MaxLines, null);

            server.AddTool("remember",
                "Record a NEW persistent memory for this workspace. ADDITIVE: appends a 'name: summary' "
                + "line to the always-loaded memory index and never overwrites an existing memory (use "
                + "update_memory to change one - a duplicate name is rejected). Use it for durable, "
                + "reusable facts worth recalling in future conversations (conventions, architecture, "
                + "decisions, gotchas). Optionally include 'detail' for a longer note stored separately "
                + "and read on demand.",
                SchemaBuilder.Object()
                    .Str("name", true, "Short handle in kebab-case: lowercase words joined by single "
                        + "hyphens (e.g. auth-flow), at most 5 words. It is normalized to kebab-case "
                        + "automatically. Must not match an existing memory.")
                    .Str("summary", true, "A single concise line describing the memory, shown in the "
                        + "always-loaded index. Keep it short.")
                    .Str("detail", false, "Optional longer note stored in <name>.md and retrieved with "
                        + "read_memory.")
                    .Build(),
                delegate(ToolCallContext ctx) { return Remember(store, ctx); });

            server.AddTool("read_memory",
                "Read the full detail note (<name>.md) for a memory whose name appears in the index. "
                + "Use this to recall the specifics behind an index summary.",
                SchemaBuilder.Object()
                    .Str("name", true, "The memory's name (as shown in the index).")
                    .Build(),
                delegate(ToolCallContext ctx) { return Read(store, ctx); });

            server.AddTool("update_memory",
                "REPLACE fields of an existing memory. This does NOT append: the provided 'summary' "
                + "overwrites that entry's index line, and the provided 'detail' FULLY OVERWRITES "
                + "<name>.md - you must pass the complete new contents, not a fragment. Omitted fields "
                + "are left unchanged; passing an empty 'detail' removes the detail file.",
                SchemaBuilder.Object()
                    .Str("name", true, "The memory's name (as shown in the index).")
                    .Str("summary", false, "New single-line summary (replaces the existing one).")
                    .Str("detail", false, "New full detail contents (replaces <name>.md entirely; empty "
                        + "removes it).")
                    .Build(),
                delegate(ToolCallContext ctx) { return Update(store, ctx); });

            server.AddTool("forget",
                "Permanently remove a memory and its detail file from the workspace store.",
                SchemaBuilder.Object()
                    .Str("name", true, "The memory's name (as shown in the index).")
                    .Build(),
                delegate(ToolCallContext ctx) { return Forget(store, ctx); });

            server.AddTool("consolidate",
                "Collapse several related memories into one, atomically (all-or-nothing): writes the new "
                + "entry and its detail, then removes the originals. Use this to compact the index when it "
                + "grows large. Compose the merged content yourself: 'detail' should hold each absorbed "
                + "memory as its own section (keyed by its original name), and 'summary' is a single "
                + "rolled-up line. The originals' specifics must be preserved in 'detail'.",
                SchemaBuilder.Object()
                    .Arr("names", "string", true, "Names of the existing memories to merge and remove.")
                    .Str("new_name", true, "Name for the consolidated memory in kebab-case (<=5 words; "
                        + "may reuse one of the source names).")
                    .Str("summary", true, "A single rolled-up line for the consolidated entry's index line.")
                    .Str("detail", true, "The merged note: one section per absorbed memory, keyed by its "
                        + "original name.")
                    .Build(),
                delegate(ToolCallContext ctx) { return Consolidate(store, ctx); });
        }

        // ---- handlers ----

        private static CallToolResult Remember(MemoryStore store, ToolCallContext ctx)
        {
            try
            {
                return ToolResults.Text(store.Remember(
                    Str(ctx, "name"), Str(ctx, "summary"), Str(ctx, "detail")));
            }
            catch (MemoryException ex) { return ToolResults.Error(ex.Message); }
        }

        private static CallToolResult Read(MemoryStore store, ToolCallContext ctx)
        {
            try { return ToolResults.Text(store.Read(Str(ctx, "name"))); }
            catch (MemoryException ex) { return ToolResults.Error(ex.Message); }
        }

        private static CallToolResult Update(MemoryStore store, ToolCallContext ctx)
        {
            bool hasSummary = Has(ctx, "summary");
            bool hasDetail = Has(ctx, "detail");
            if (!hasSummary && !hasDetail)
                return ToolResults.Error("provide 'summary' and/or 'detail' to update");
            try
            {
                return ToolResults.Text(store.Update(
                    Str(ctx, "name"), Str(ctx, "summary"), Str(ctx, "detail"), hasSummary, hasDetail));
            }
            catch (MemoryException ex) { return ToolResults.Error(ex.Message); }
        }

        private static CallToolResult Forget(MemoryStore store, ToolCallContext ctx)
        {
            try { return ToolResults.Text(store.Forget(Str(ctx, "name"))); }
            catch (MemoryException ex) { return ToolResults.Error(ex.Message); }
        }

        private static CallToolResult Consolidate(MemoryStore store, ToolCallContext ctx)
        {
            try
            {
                return ToolResults.Text(store.Consolidate(
                    StrList(ctx, "names"), Str(ctx, "new_name"), Str(ctx, "summary"), Str(ctx, "detail")));
            }
            catch (MemoryException ex) { return ToolResults.Error(ex.Message); }
        }

        // ---- argument helpers ----

        private static string Str(ToolCallContext ctx, string key)
        {
            JToken t = ctx.Arguments[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return (string)t;
            return t.ToString();
        }

        // True when the property was present in the call (even if explicitly null/empty), so we can
        // distinguish "leave unchanged" from "clear it".
        private static bool Has(ToolCallContext ctx, string key)
        {
            return ctx.Arguments.Property(key) != null;
        }

        private static List<string> StrList(ToolCallContext ctx, string key)
        {
            List<string> result = new List<string>();
            JArray arr = ctx.Arguments[key] as JArray;
            if (arr != null)
            {
                foreach (JToken t in arr)
                {
                    if (t == null || t.Type == JTokenType.Null) continue;
                    result.Add(t.Type == JTokenType.String ? (string)t : t.ToString());
                }
            }
            return result;
        }
    }
}
