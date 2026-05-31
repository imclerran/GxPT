using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace WebSearchMcpServer
{
    /// <summary>
    /// First-party web server: search + page extraction via the Tavily API over curl. The standard
    /// five lines around the web tool set (servers-spec §1). Server name "web" → tools surface to
    /// the model as web__search and web__extract.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            StdErrLogSink log = new StdErrLogSink();

            Implementation info = new Implementation();
            info.Name = "web";
            info.Version = "1.0";

            McpServer server = new McpServer(info, log);
            WebSearchTools.Register(server, WebSearchConfig.FromEnvironment(log));
            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
