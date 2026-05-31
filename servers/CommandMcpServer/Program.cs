using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace CommandMcpServer
{
    /// <summary>
    /// First-party Command MCP server: run an already-approved shell command line. The standard
    /// five lines around the single run tool (servers-spec §1, §5).
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "command";
            info.Version = "1.0";

            McpServer server = new McpServer(info, log);
            CommandTools.Register(server, CommandConfig.FromEnvironment(log));
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
