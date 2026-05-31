using System.Collections.Generic;
using GxPT;
using Mcp35.Client;
using Mcp35.Core.Protocol;

namespace GxPT.Tests.Mcp
{
    // An IServerConnector that returns RegistryFakeTransport-backed connections (Created, not opened)
    // so McpHost can open them to Ready without spawning processes. Records what it was asked to make.
    internal sealed class FakeServerConnector : IServerConnector
    {
        public readonly List<string> CreatedNames = new List<string>();
        public readonly List<string> Workdirs = new List<string>();
        public readonly List<McpServerConnection> Created = new List<McpServerConnection>();
        public readonly Dictionary<string, ToolDef[]> ToolsByServer = new Dictionary<string, ToolDef[]>();

        public McpServerConnection Create(McpServerSpec spec, string workdir)
        {
            CreatedNames.Add(spec.Name);
            Workdirs.Add(workdir);

            ToolDef[] tools;
            if (!ToolsByServer.TryGetValue(spec.Name, out tools))
                tools = new[] { new ToolDef(spec.Name + "_tool") };

            var ft = new RegistryFakeTransport(tools);
            var ci = new Implementation { Name = "GxPT", Version = "test" };
            var conn = new McpServerConnection(spec.Name, ft, ci, null);
            Created.Add(conn);
            return conn;
        }
    }

    internal static class Specs
    {
        public static McpServerSpec Eager(string name, bool enabled)
        {
            return new McpServerSpec
            {
                Name = name,
                Kind = McpTransportKind.Stdio,
                Enabled = enabled,
                WorkdirScoped = false,
                BuiltIn = true,
                Command = name + ".exe"
            };
        }

        public static McpServerSpec Scoped(string name, bool enabled)
        {
            return new McpServerSpec
            {
                Name = name,
                Kind = McpTransportKind.Stdio,
                Enabled = enabled,
                WorkdirScoped = true,
                BuiltIn = true,
                Command = name + ".exe"
            };
        }
    }
}
