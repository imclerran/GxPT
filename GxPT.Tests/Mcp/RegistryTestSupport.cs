using System;
using System.Collections.Generic;
using Mcp35.Client;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;

namespace GxPT.Tests.Mcp
{
    // A canned tool definition for seeding a fake connection.
    internal sealed class ToolDef
    {
        public string Name;
        public string Description;
        public JObject Schema;

        public ToolDef(string name) : this(name, name + " description", null) { }
        public ToolDef(string name, string description, JObject schema)
        {
            Name = name;
            Description = description;
            Schema = schema;
        }
    }

    // An in-memory IRpcTransport that answers initialize + tools/list so a real McpServerConnection
    // reaches Ready and ListTools() returns canned tools. The current tool set is mutable so a
    // RefreshConnection (which re-sends tools/list) can observe a changed catalog.
    internal sealed class RegistryFakeTransport : IRpcTransport
    {
        public List<ToolDef> Tools = new List<ToolDef>();
        private bool _connected = true;

        public event EventHandler<JsonRpcInboundEventArgs> Inbound;

        public RegistryFakeTransport(IEnumerable<ToolDef> tools)
        {
            if (tools != null) Tools.AddRange(tools);
        }

        public void Start() { }
        public bool IsConnected { get { return _connected; } }
        public void Dispose() { _connected = false; }
        public void SendNotification(string method, JToken @params) { }

        public JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs)
        {
            if (method == "initialize") return Ok(InitializeResult());
            if (method == "tools/list") return Ok(ToolsListResult());
            return Ok(new JObject());
        }

        public void RaiseInbound(string method)
        {
            EventHandler<JsonRpcInboundEventArgs> h = Inbound;
            if (h == null) return;
            JsonRpcInboundEventArgs e = new JsonRpcInboundEventArgs();
            e.Method = method;
            e.IsRequest = false;
            h(this, e);
        }

        private static JsonRpcResponse Ok(JToken result)
        {
            JsonRpcResponse r = new JsonRpcResponse();
            r.Id = RequestId.FromLong(1);
            r.IsError = false;
            r.Result = result;
            return r;
        }

        private static JObject InitializeResult()
        {
            JObject caps = new JObject();
            caps["tools"] = new JObject();
            JObject info = new JObject();
            info["name"] = "fake";
            info["version"] = "1.0";
            JObject result = new JObject();
            result["protocolVersion"] = ProtocolVersions.Default;
            result["capabilities"] = caps;
            result["serverInfo"] = info;
            return result;
        }

        private JObject ToolsListResult()
        {
            JArray arr = new JArray();
            for (int i = 0; i < Tools.Count; i++)
            {
                ToolDef t = Tools[i];
                JObject o = new JObject();
                o["name"] = t.Name;
                o["description"] = t.Description;
                o["inputSchema"] = t.Schema != null ? (JToken)t.Schema : DefaultSchema();
                arr.Add(o);
            }
            JObject result = new JObject();
            result["tools"] = arr;
            return result;
        }

        private static JObject DefaultSchema()
        {
            JObject s = new JObject();
            s["type"] = "object";
            return s;
        }
    }

    internal static class FakeConn
    {
        // Build a connection already Open (Ready) with the given server name and tools.
        public static McpServerConnection Ready(string serverName, params ToolDef[] tools)
        {
            RegistryFakeTransport ignored;
            return Ready(serverName, out ignored, tools);
        }

        public static McpServerConnection Ready(string serverName, out RegistryFakeTransport transport, params ToolDef[] tools)
        {
            transport = new RegistryFakeTransport(tools);
            Implementation client = new Implementation();
            client.Name = "GxPT";
            client.Version = "test";
            McpServerConnection conn = new McpServerConnection(serverName, transport, client, null);
            conn.Open(2000);
            return conn;
        }
    }
}
