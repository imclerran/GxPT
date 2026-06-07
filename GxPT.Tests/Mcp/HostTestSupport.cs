using System;
using System.Collections.Generic;
using System.Threading;
using GxPT;
using Mcp35.Client;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;

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

    // A transport whose initialize handshake blocks until a gate is released, signaling when it has
    // entered Open(). Lets a test park a connection mid-connect and prove Dispose doesn't wait on it.
    internal sealed class GatedTransport : IRpcTransport
    {
        private readonly RegistryFakeTransport _inner;
        private readonly ManualResetEvent _openGate;
        private readonly ManualResetEvent _opening;
        private bool _connected = true;

        public GatedTransport(IEnumerable<ToolDef> tools, ManualResetEvent openGate, ManualResetEvent opening)
        {
            _inner = new RegistryFakeTransport(tools);
            _openGate = openGate;
            _opening = opening;
        }

        public event EventHandler<JsonRpcInboundEventArgs> Inbound { add { } remove { } }
        public void Start() { }
        public bool IsConnected { get { return _connected; } }
        public void Dispose() { _connected = false; }
        public void Shutdown(bool forceful) { _connected = false; }
        public void SendNotification(string method, JToken @params) { }

        public JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs)
        {
            if (method == "initialize")
            {
                _opening.Set();      // tell the test we're now parked inside the blocking handshake
                _openGate.WaitOne(); // ...until the test releases us
            }
            return _inner.SendRequest(method, @params, timeoutMs);
        }
    }

    // An IServerConnector whose connections block in Open() until OpenGate is set.
    internal sealed class GatedServerConnector : IServerConnector
    {
        public readonly ManualResetEvent OpenGate = new ManualResetEvent(false);
        public readonly ManualResetEvent Opening = new ManualResetEvent(false);
        public readonly List<McpServerConnection> Created = new List<McpServerConnection>();

        public McpServerConnection Create(McpServerSpec spec, string workdir)
        {
            var ft = new GatedTransport(new[] { new ToolDef(spec.Name + "_tool") }, OpenGate, Opening);
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
