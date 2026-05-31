using System;
using System.Collections.Generic;
using System.Threading;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Errors;
using Mcp35.Core.Json;
using Mcp35.Core.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Rpc
{
    /// <summary>
    /// The reusable correlation engine for a full-duplex transport (stdio). Owns the id counter,
    /// the pending-request table, and inbound dispatch (response vs notification vs request).
    /// HTTP is one-POST-one-response and does NOT use this. See mcp35-core-spec.md §4-5.
    /// </summary>
    public sealed class JsonRpcPeer : IRpcTransport
    {
        private sealed class PendingCall
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public JsonRpcResponse Response;
            public Exception Fault;
        }

        private readonly IDuplexMessageChannel _channel;
        private readonly ILogSink _log;
        private readonly object _gate = new object();
        private readonly Dictionary<RequestId, PendingCall> _pending = new Dictionary<RequestId, PendingCall>();

        private long _idCounter;
        private bool _started;
        private bool _faulted;
        private bool _disposed;
        private Exception _faultEx;

        public event EventHandler<JsonRpcInboundEventArgs> Inbound;

        public JsonRpcPeer(IDuplexMessageChannel channel, ILogSink log)
        {
            if (channel == null) throw new ArgumentNullException("channel");
            _channel = channel;
            _log = log ?? NullLogSink.Instance;
            _channel.MessageReceived += OnMessage;
            _channel.Faulted += OnChannelFaulted;
        }

        public bool IsConnected
        {
            get { lock (_gate) { return _started && !_faulted && !_disposed; } }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
            }
            _channel.Start();
        }

        public JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs)
        {
            EnsureUsable();

            RequestId id = RequestId.FromLong(Interlocked.Increment(ref _idCounter));
            PendingCall pc = new PendingCall();
            lock (_gate)
            {
                if (_disposed) throw new McpTransportException("Transport disposed.");
                if (_faulted) throw new McpTransportException("Transport faulted.", _faultEx);
                _pending[id] = pc;
            }

            JsonRpcRequest req = new JsonRpcRequest();
            req.Id = id;
            req.Method = method;
            req.Params = @params;

            try
            {
                _channel.Send(McpJson.Serialize(req));
            }
            catch (Exception ex)
            {
                lock (_gate) { _pending.Remove(id); }
                throw new McpTransportException("Failed to send request '" + method + "': " + ex.Message, ex);
            }

            bool signaled = pc.Done.WaitOne(timeoutMs, false);
            if (!signaled)
            {
                lock (_gate) { _pending.Remove(id); }
                throw new McpTimeoutException("Timed out after " + timeoutMs + " ms awaiting response to '" + method + "'.");
            }

            if (pc.Fault != null)
                throw new McpTransportException("Transport faulted while awaiting response to '" + method + "'.", pc.Fault);

            return pc.Response;
        }

        public void SendNotification(string method, JToken @params)
        {
            EnsureUsable();
            JsonRpcNotification n = new JsonRpcNotification();
            n.Method = method;
            n.Params = @params;
            _channel.Send(McpJson.Serialize(n));
        }

        // ---- inbound dispatch (reader thread) ----

        private void OnMessage(string json)
        {
            JToken tok;
            try
            {
                tok = McpJson.Parse(json);
            }
            catch (Exception ex)
            {
                _log.Log("mcp", "Failed to parse inbound message: " + ex.Message);
                return;
            }

            if (tok.Type == JTokenType.Array)
            {
                // We never emit batches; tolerate inbound arrays (legacy) by processing each element.
                foreach (JToken el in (JArray)tok) Dispatch(el as JObject);
                return;
            }
            Dispatch(tok as JObject);
        }

        private void Dispatch(JObject o)
        {
            if (o == null) return;

            bool hasMethod = o["method"] != null;

            // Response: no method, carries result or error. (result may be a JSON-null token.)
            if (!hasMethod && (o.Property("result") != null || o["error"] != null))
            {
                CompletePending(JsonRpcResponse.Parse(o));
                return;
            }

            if (!hasMethod)
            {
                _log.Log("mcp", "Ignoring message with neither method nor result/error.");
                return;
            }

            string method = (string)o["method"];
            JToken prms = o["params"];

            // No id => notification.
            if (o.Property("id") == null)
            {
                RaiseInbound(method, prms, false, RequestId.Null, o);
                return;
            }

            // Has id => server->client request.
            RequestId id = ParseId(o["id"]);
            if (method == McpMethods.Ping)
            {
                ReplyResult(id, new JObject()); // keep-alive: {}
                return;
            }

            // Anything else is unsupported in phase 1: decline, but surface for observability.
            ReplyError(id, JsonRpcErrorCodes.MethodNotFound, "method not found: " + method);
            RaiseInbound(method, prms, true, id, o);
        }

        private void CompletePending(JsonRpcResponse resp)
        {
            PendingCall pc;
            lock (_gate)
            {
                if (!_pending.TryGetValue(resp.Id, out pc))
                {
                    _log.Log("mcp", "Response for unknown id " + resp.Id + " (ignored).");
                    return;
                }
                _pending.Remove(resp.Id);
            }
            pc.Response = resp;
            pc.Done.Set();
        }

        private void OnChannelFaulted(Exception ex)
        {
            List<PendingCall> toFault;
            lock (_gate)
            {
                _faulted = true;
                _faultEx = ex;
                toFault = new List<PendingCall>(_pending.Values);
                _pending.Clear();
            }
            for (int i = 0; i < toFault.Count; i++)
            {
                toFault[i].Fault = ex;
                toFault[i].Done.Set();
            }
            _log.Log("mcp", "Transport faulted: " + (ex == null ? "(unknown)" : ex.Message));
        }

        private void RaiseInbound(string method, JToken prms, bool isRequest, RequestId id, JObject raw)
        {
            EventHandler<JsonRpcInboundEventArgs> h = Inbound;
            if (h == null) return;

            JsonRpcInboundEventArgs args = new JsonRpcInboundEventArgs();
            args.Method = method;
            args.Params = prms;
            args.IsRequest = isRequest;
            args.Id = id;
            args.Raw = raw;
            try
            {
                h(this, args);
            }
            catch (Exception ex)
            {
                _log.Log("mcp", "Inbound handler threw: " + ex.Message);
            }
        }

        private void ReplyResult(RequestId id, JToken result)
        {
            JObject o = new JObject();
            o["jsonrpc"] = "2.0";
            o["id"] = IdToToken(id);
            o["result"] = result ?? JValue.CreateNull();
            SafeSend(o.ToString(Formatting.None));
        }

        private void ReplyError(RequestId id, int code, string message)
        {
            JObject err = new JObject();
            err["code"] = code;
            err["message"] = message;

            JObject o = new JObject();
            o["jsonrpc"] = "2.0";
            o["id"] = IdToToken(id);
            o["error"] = err;
            SafeSend(o.ToString(Formatting.None));
        }

        private void SafeSend(string json)
        {
            try
            {
                _channel.Send(json);
            }
            catch (Exception ex)
            {
                _log.Log("mcp", "Failed to send reply: " + ex.Message);
            }
        }

        private void EnsureUsable()
        {
            lock (_gate)
            {
                if (_disposed) throw new McpTransportException("Transport disposed.");
                if (_faulted) throw new McpTransportException("Transport faulted.", _faultEx);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            try { _channel.Dispose(); }
            catch (Exception ex) { _log.Log("mcp", "Channel dispose threw: " + ex.Message); }

            // Fault any callers still blocked in SendRequest so none hang.
            OnChannelFaulted(_faultEx ?? new ObjectDisposedException("JsonRpcPeer"));
        }

        private static JToken IdToToken(RequestId id)
        {
            if (id.IsNull) return JValue.CreateNull();
            if (id.IsString) return new JValue(id.AsString);
            return new JValue(id.AsLong);
        }

        private static RequestId ParseId(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return RequestId.Null;
            if (t.Type == JTokenType.Integer) return RequestId.FromLong(t.Value<long>());
            if (t.Type == JTokenType.String) return RequestId.FromString(t.Value<string>());
            return RequestId.FromString(t.ToString());
        }
    }
}
