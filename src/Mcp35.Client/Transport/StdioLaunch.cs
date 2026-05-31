using System.Collections.Generic;

namespace Mcp35.Client.Transport
{
    /// <summary>How to launch a stdio MCP server child process.</summary>
    public sealed class StdioLaunch
    {
        /// <summary>Resolved executable path (the host resolves built-ins / PATH).</summary>
        public string Command;

        public string Arguments;
        public string WorkingDirectory;

        /// <summary>Extra environment variables for the child (added to the inherited block).</summary>
        public IDictionary<string, string> Environment;

        /// <summary>Grace period after stdin EOF before the child is force-killed on Dispose.</summary>
        public int ShutdownGraceMs = 2000;
    }
}
