using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace FilesMcpServer
{
    /// <summary>
    /// First-party Files MCP server: read/list/write/delete confined to GXPT_WORKDIR. The same
    /// five lines as every first-party server (servers-spec §1), around the Files tool set.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "files";
            info.Version = "1.0";

            McpServer server = new McpServer(info, log);
            FilesTools.Register(server, FilesConfig.FromEnvironment(log));
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
