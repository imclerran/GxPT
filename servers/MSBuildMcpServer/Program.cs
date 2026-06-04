using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace MSBuildMcpServer
{
    /// <summary>
    /// First-party MSBuild MCP server: discovers the MSBuild versions installed on the system
    /// (XP-era .NET Framework 2.0/3.5/4.0 through Visual Studio 2017+/MSBuild 17) and surfaces
    /// each version as its own build tool. The standard five lines around a dynamically-built tool
    /// set (servers-spec §1); the tool list is still static for the life of the process (discovery
    /// runs once at startup, listChanged=false).
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "msbuild";
            info.Version = "1.0";

            McpServer server = new McpServer(info, log);
            MsBuildTools.Register(server, MsBuildConfig.FromEnvironment(log));
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
