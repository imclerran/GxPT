using System;
using System.Collections.Generic;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;

namespace Mcp35.Client.Tests
{
    /// <summary>
    /// An in-memory <see cref="IRpcTransport"/> (no process) for driving
    /// <see cref="Mcp35.Client.McpServerConnection"/> in unit tests. Lets a test script per-method
    /// responses, record sent messages, simulate faults/timeouts, and push inbound notifications.
    /// </summary>
    internal sealed class FakeTransport : IRpcTransport
    {
        public readonly List<string> SentMethods = new List<string>();
        public readonly List<string> Notifications = new List<string>();

        /// <summary>method =&gt; produce a response for a request to that method.</summary>
        public readonly Dictionary<string, Func<JToken, JsonRpcResponse>> Handlers =
            new Dictionary<string, Func<JToken, JsonRpcResponse>>(StringComparer.Ordinal);

        public bool ThrowOnSend;            // simulate a mid-call transport fault
        public bool TimeoutEverything;      // simulate no response (throw timeout)
        public bool Started;
        private bool _connected = true;

        public event EventHandler<JsonRpcInboundEventArgs> Inbound;

        public void Start() { Started = true; }
        public bool IsConnected { get { return _connected; } }

        public JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs)
        {
            SentMethods.Add(method);

            if (ThrowOnSend)
            {
                _connected = false;
                throw new Mcp35.Core.Errors.McpTransportException("simulated transport fault");
            }
            if (TimeoutEverything)
                throw new Mcp35.Core.Errors.McpTimeoutException("simulated timeout");

            Func<JToken, JsonRpcResponse> h;
            if (Handlers.TryGetValue(method, out h)) return h(@params);

            // Default: empty object result.
            return Result(new JObject());
        }

        public void SendNotification(string method, JToken @params)
        {
            Notifications.Add(method);
        }

        public void Dispose() { _connected = false; }

        public void RaiseInbound(string method)
        {
            EventHandler<JsonRpcInboundEventArgs> h = Inbound;
            if (h == null) return;
            JsonRpcInboundEventArgs e = new JsonRpcInboundEventArgs();
            e.Method = method;
            e.IsRequest = false;
            h(this, e);
        }

        // ---- response builders ----

        public static JsonRpcResponse Result(JToken result)
        {
            JsonRpcResponse r = new JsonRpcResponse();
            r.Id = RequestId.FromLong(1);
            r.IsError = false;
            r.Result = result;
            return r;
        }

        public static JsonRpcResponse Error(int code, string message)
        {
            JsonRpcResponse r = new JsonRpcResponse();
            r.Id = RequestId.FromLong(1);
            r.IsError = true;
            r.Error = new JsonRpcError(code, message);
            return r;
        }

        public static JObject InitializeResult(string version, bool withTools)
        {
            JObject caps = new JObject();
            if (withTools) caps["tools"] = new JObject();

            JObject serverInfo = new JObject();
            serverInfo["name"] = "fake";
            serverInfo["version"] = "1.0";

            JObject result = new JObject();
            result["protocolVersion"] = version;
            result["capabilities"] = caps;
            result["serverInfo"] = serverInfo;
            return result;
        }

        public static JObject ToolsListResult(string nextCursor, params string[] names)
        {
            JArray tools = new JArray();
            foreach (string n in names)
            {
                JObject t = new JObject();
                t["name"] = n;
                JObject schema = new JObject();
                schema["type"] = "object";
                t["inputSchema"] = schema;
                tools.Add(t);
            }
            JObject result = new JObject();
            result["tools"] = tools;
            if (nextCursor != null) result["nextCursor"] = nextCursor;
            return result;
        }
    }
}
