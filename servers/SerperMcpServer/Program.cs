using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace SerperMcpServer
{
    /// <summary>
    /// First-party Serper MCP server: one web-search tool over curl. The standard five lines
    /// around the Serper tool set (servers-spec §1).
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "serper";
            info.Version = "1.0";

            McpServer server = new McpServer(info, log);
            SerperTools.Register(server, SerperConfig.FromEnvironment(log));
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
