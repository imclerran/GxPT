using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace SkillsMcpServer
{
    /// <summary>
    /// First-party Skills MCP server: authors skill files (and, later, runs a skill's bundled .bat). The
    /// same five-line shape as every first-party server (servers-spec sec.1), around the skills tool set.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "skills";
            info.Version = "1.0";

            McpServer server = new McpServer(info, log);
            SkillsTools.Register(server, SkillsConfig.FromEnvironment(log));
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
