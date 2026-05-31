using System;
using System.IO;
using Mcp35.Client;
using Mcp35.Client.Transport;
using Mcp35.Core.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Mcp35.Client.Tests
{
    /// <summary>
    /// The first Core + Server + Client integration test: launches the real EchoMcpServer exe as a
    /// child process and runs initialize + tools/list + tools/call over StdioTransport, then
    /// verifies graceful shutdown on Dispose (stdin EOF → exit). Phase-3 criterion 5.
    /// </summary>
    public class StdioEndToEndTests
    {
        private static string EchoServerExe()
        {
            // The csproj CopyEchoServer target stages the server under <output>/echo-server/.
            string dir = Path.Combine(AppContext.BaseDirectory, "echo-server");
            string exe = Path.Combine(dir, "EchoMcpServer.exe");
            string dll = Path.Combine(dir, "EchoMcpServer.dll");

            if (File.Exists(exe)) return exe;
            // On the CI runner the apphost exe should exist; fall back to the dll via dotnet if not.
            if (File.Exists(dll)) return dll;
            throw new FileNotFoundException("EchoMcpServer not found in " + dir +
                " (looked for EchoMcpServer.exe/.dll).");
        }

        private static StdioLaunch BuildLaunch()
        {
            string target = EchoServerExe();
            StdioLaunch launch = new StdioLaunch();
            launch.ShutdownGraceMs = 3000;

            if (target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                launch.Command = "dotnet";
                launch.Arguments = "\"" + target + "\"";
            }
            else
            {
                launch.Command = target;
                launch.Arguments = "";
            }
            return launch;
        }

        private static McpServerConnection Connect()
        {
            StdioTransport transport = new StdioTransport(BuildLaunch(), null);
            Implementation client = new Implementation();
            client.Name = "e2e-test";
            client.Version = "1.0";
            return new McpServerConnection("echo", transport, client, null);
        }

        [Fact]
        public void Full_handshake_list_and_call_over_real_process()
        {
            using (McpServerConnection conn = Connect())
            {
                conn.Open(McpServerConnection.DefaultInitializeTimeoutMs);
                Assert.Equal(ConnectionState.Ready, conn.State);
                Assert.True(conn.SupportsTools);
                Assert.Equal("echo", conn.ServerInfo.ServerInfo.Name);

                var tools = conn.ListTools(false);
                Assert.Contains(tools, delegate (Tool t) { return t.Name == "echo"; });
                Assert.Contains(tools, delegate (Tool t) { return t.Name == "boom"; });

                JObject args = new JObject();
                args["text"] = "hello over stdio";
                CallToolResult result = conn.CallTool("echo", args, 10000);

                Assert.False(result.IsError);
                Assert.Equal("hello over stdio", result.Content[0].Text);
            }
            // Dispose (end of using) closed stdin → the server exited gracefully; no hang/throw.
        }

        [Fact]
        public void Handler_exception_in_real_server_comes_back_as_isError()
        {
            using (McpServerConnection conn = Connect())
            {
                conn.Open(McpServerConnection.DefaultInitializeTimeoutMs);
                CallToolResult result = conn.CallTool("boom", new JObject(), 10000);
                Assert.True(result.IsError); // server contained the throw, stayed alive
            }
        }

        [Fact]
        public void Unknown_tool_on_real_server_throws_mcp_exception()
        {
            using (McpServerConnection conn = Connect())
            {
                conn.Open(McpServerConnection.DefaultInitializeTimeoutMs);
                Assert.Throws<Mcp35.Core.Errors.McpException>(
                    delegate { conn.CallTool("does_not_exist", new JObject(), 10000); });
            }
        }
    }
}
