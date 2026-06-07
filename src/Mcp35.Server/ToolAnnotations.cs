using Newtonsoft.Json.Linq;

namespace Mcp35.Server
{
    /// <summary>
    /// Builds the small annotations object an MCP tool advertises about its capability — the
    /// <c>readOnlyHint</c> / <c>destructiveHint</c> fields the host reads to classify a tool into an
    /// approval tier (and that a "read-only" subagent filter selects on). Built-in servers should
    /// declare one per tool via <see cref="McpServer.AddTool(string,string,JObject,JObject,ToolHandler)"/>.
    ///
    /// Fail-safe convention (mirrors the host classifier): an ABSENT <c>readOnlyHint</c> is treated as
    /// NOT read-only, so a forgotten annotation never silently widens access. Hints are emitted
    /// explicitly here so the declared capability round-trips losslessly to the host.
    /// </summary>
    public static class ToolAnnotations
    {
        /// <summary>Inspects/reads only; never mutates state. Auto-allowed by the host.</summary>
        public static JObject ReadOnly()
        {
            JObject a = new JObject();
            a["readOnlyHint"] = true;
            a["destructiveHint"] = false;
            return a;
        }

        /// <summary>Mutates state but does not destroy or discard existing data (e.g. create/append).</summary>
        public static JObject Write()
        {
            JObject a = new JObject();
            a["readOnlyHint"] = false;
            a["destructiveHint"] = false;
            return a;
        }

        /// <summary>May overwrite, delete, or otherwise irreversibly discard data.</summary>
        public static JObject Destructive()
        {
            JObject a = new JObject();
            a["readOnlyHint"] = false;
            a["destructiveHint"] = true;
            return a;
        }
    }
}
