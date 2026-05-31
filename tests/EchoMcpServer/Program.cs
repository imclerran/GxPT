using Mcp35.Core.Protocol;
using Mcp35.Server;

namespace EchoMcpServer
{
    /// <summary>
    /// A minimal MCP server over stdio: one "echo" tool that returns its <c>text</c> argument,
    /// and one "boom" tool that throws (to exercise contained-exception handling). Used by
    /// Mcp35.Client.Tests' end-to-end StdioTransport test.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            Implementation info = new Implementation();
            info.Name = "echo";
            info.Version = "1.0.0";

            McpServer server = new McpServer(info, new StdErrLogSink());

            server.AddTool("echo", "Echoes the provided text back.",
                SchemaBuilder.Object().Str("text", true, "Text to echo").Build(),
                delegate(ToolCallContext ctx)
                {
                    string text = ctx.Arguments.Value<string>("text");
                    return ToolResults.Text(text == null ? "" : text);
                });

            server.AddTool("boom", "Always throws (for error-path testing).", null,
                delegate(ToolCallContext ctx)
                {
                    throw new System.InvalidOperationException("intentional failure");
                });

            server.Run(); // blocks until stdin EOF
            return 0;
        }
    }
}
