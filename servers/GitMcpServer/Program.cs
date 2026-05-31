using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace GitMcpServer
{
    /// <summary>
    /// First-party Git MCP server: status/diff/log/commit/push against GXPT_WORKDIR. The standard
    /// five lines around the Git tool set (servers-spec §1).
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "git";
            info.Version = "1.0";

            McpServer server = new McpServer(info, log);
            GitTools.Register(server, GitConfig.FromEnvironment(log));
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
