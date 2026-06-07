using System;
using System.Collections.Generic;
using System.Threading;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Errors;
using Mcp35.Core.Json;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Mcp35.Core.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Client.Transport
{
    /// <summary>
    /// Streamable-HTTP transport (MCP) implemented directly over Core's <see cref="CurlRunner"/> +
    /// <see cref="SseParser"/>. Unlike stdio, HTTP is one POST = one correlated response, so there
    /// is no JsonRpcPeer: each <see cref="SendRequest"/> is its own curl invocation that blocks the
    /// calling worker thread and gets its own reply. Session id + negotiated protocol version are
    /// captured at handshake and replayed on every later request. See mcp35-http-spec.md.
    /// </summary>
    public sealed class HttpTransport : IRpcTransport
    {
        private readonly string _url;
        private readonly IDictionary<string, string> _baseHeaders; // e.g. Authorization (secrets)
        private readonly ICurlRunner _curl;
        private readonly ILogSink _log;

        private long _idCounter;
        private string _sessionId;          // from Mcp-Session-Id, replayed after initialize
        private string _protocolVersion;    // negotiated, sent as MCP-Protocol-Version after initialize
        private bool _connected;
        private bool _disposed;

        public event EventHandler<JsonRpcInboundEventArgs> Inbound;

        public HttpTransport(string url, IDictionary<string, string> headers, ICurlRunner curl, ILogSink log)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("url is required", "url");
            if (curl == null) throw new ArgumentNullException("curl");
            _url = url;
            _baseHeaders = headers ?? new Dictionary<string, string>();
            _curl = curl;
            _log = log ?? NullLogSink.Instance;
        }

        /// <summary>No-op: HTTP is connectionless until the first POST. The session is a protocol state.</summary>
        public void Start() { }

        public bool IsConnected
        {
            get { return _connected && !_disposed; }
        }

        public JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs)
        {
            if (_disposed) throw new McpTransportException("Transport disposed.");

            RequestId id = RequestId.FromLong(Interlocked.Increment(ref _idCounter));

            JsonRpcRequest req = new JsonRpcRequest();
            req.Id = id;
            req.Method = method;
            req.Params = @params;

            CurlRequest cr = new CurlRequest();
            cr.Url = _url;
            cr.Method = "POST";
            cr.BodyJson = McpJson.Serialize(req);
            cr.Headers = BuildHeaders(true);
            cr.TimeoutMs = timeoutMs;

            CurlResult res;
            try
            {
                res = _curl.Run(cr);
            }
            catch (Exception ex)
            {
                throw new McpTransportException("HTTP request '" + method + "' failed: " + ex.Message, ex);
            }

            // Auth failures fault the connection (host may surface "check your PAT").
            if (res.HttpStatus == 401 || res.HttpStatus == 403)
            {
                _connected = false;
                throw new McpTransportException("HTTP " + res.HttpStatus + " (authorization failed) for '" + method + "'.");
            }
            // A post-init 404 most likely means the session expired. We fault, never auto-reinit.
            if (res.HttpStatus == 404 && _sessionId != null)
            {
                _connected = false;
                throw new McpTransportException("HTTP 404 (session expired?) for '" + method + "'.");
            }

            // Capture the session id from the initialize response (only set once).
            if (_sessionId == null)
            {
                string sid = res.GetHeader("Mcp-Session-Id");
                if (!string.IsNullOrEmpty(sid)) _sessionId = sid;
            }

            JsonRpcResponse response = ExtractResponse(res, id, method);

            // initialize success establishes the session/connection (version stamped by caller below).
            if (method == McpMethods.Initialize && !response.IsError)
            {
                _connected = true;
                CaptureNegotiatedVersion(response);
            }
            return response;
        }

        public void SendNotification(string method, JToken @params)
        {
            if (_disposed) return;

            JsonRpcNotification n = new JsonRpcNotification();
            n.Method = method;
            n.Params = @params;

            CurlRequest cr = new CurlRequest();
            cr.Url = _url;
            cr.Method = "POST";
            cr.BodyJson = McpJson.Serialize(n);
            cr.Headers = BuildHeaders(true);
            cr.TimeoutMs = 30000;

            try
            {
                CurlResult res = _curl.Run(cr);
                // Notifications expect 202 Accepted with no body; any 2xx is fine, non-2xx is logged.
                if (res.HttpStatus != 0 && (res.HttpStatus < 200 || res.HttpStatus >= 300))
                    _log.Log("mcp-http", "notification '" + method + "' got HTTP " + res.HttpStatus + " (ignored)");
            }
            catch (Exception ex)
            {
                // A dropped notification must not fault the session.
                _log.Log("mcp-http", "notification '" + method + "' failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            Shutdown(false);
        }

        public void Shutdown(bool forceful)
        {
            if (_disposed) return;
            _disposed = true;

            // Best-effort session teardown: DELETE with the session header. Failure is swallowed.
            // Forceful (app/host shutdown) skips it — a blocking network round trip to a remote
            // server that may be slow or unreachable must not delay close; the server GCs the
            // session on its own timeout.
            if (!forceful && _sessionId != null)
            {
                try
                {
                    CurlRequest cr = new CurlRequest();
                    cr.Url = _url;
                    cr.Method = "DELETE";
                    cr.Headers = BuildHeaders(false);
                    cr.TimeoutMs = 10000;
                    _curl.Run(cr);
                }
                catch (Exception ex)
                {
                    _log.Log("mcp-http", "session DELETE failed (ignored): " + ex.Message);
                }
            }
            _connected = false;
        }

        // ---- response extraction ----

        /// <summary>
        /// Read the reply: a single application/json message, or a text/event-stream whose events
        /// each carry one JSON-RPC message. Inbound notifications/requests on the SSE are surfaced;
        /// the message whose id matches ours is returned.
        /// </summary>
        private JsonRpcResponse ExtractResponse(CurlResult res, RequestId expectedId, string method)
        {
            string contentType = res.GetHeader("Content-Type");
            bool isSse = contentType != null &&
                contentType.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;

            if (string.IsNullOrEmpty(res.Body))
                throw new McpTransportException("empty HTTP response for '" + method + "'.");

            if (!isSse)
            {
                JObject single = ParseObject(res.Body, method);
                return MatchOrFault(single, expectedId, method);
            }

            // SSE: feed lines through the parser; each event's Data is a JSON-RPC message.
            SseParser parser = new SseParser();
            JsonRpcResponse matched = null;

            string[] lines = res.Body.Replace("\r\n", "\n").Split('\n');
            List<SseEvent> events = new List<SseEvent>();
            for (int i = 0; i < lines.Length; i++)
                foreach (SseEvent ev in parser.PushLine(lines[i])) events.Add(ev);
            SseEvent tail = parser.Flush();
            if (tail != null) events.Add(tail);

            for (int i = 0; i < events.Count; i++)
            {
                string data = events[i].Data;
                if (string.IsNullOrEmpty(data)) continue;

                JObject obj;
                try { obj = (JObject)McpJson.Parse(data); }
                catch (Exception ex) { _log.Log("mcp-http", "bad SSE message (ignored): " + ex.Message); continue; }

                if (obj["method"] != null)
                {
                    // server -> client notification or request seen on this POST's stream
                    RaiseInbound(obj);
                    continue;
                }
                // a response: is it ours?
                JsonRpcResponse r = JsonRpcResponse.Parse(obj);
                if (r.Id.Equals(expectedId)) matched = r;
            }

            if (matched == null)
                throw new McpTransportException("no matching response for '" + method + "' in SSE stream.");
            return matched;
        }

        private JsonRpcResponse MatchOrFault(JObject obj, RequestId expectedId, string method)
        {
            if (obj["method"] != null)
            {
                // The server answered a request with a notification/request — surface and fault.
                RaiseInbound(obj);
                throw new McpTransportException("expected a response to '" + method + "', got a request/notification.");
            }
            JsonRpcResponse r = JsonRpcResponse.Parse(obj);
            if (!r.Id.Equals(expectedId))
                throw new McpTransportException("response id mismatch for '" + method + "'.");
            return r;
        }

        // ---- helpers ----

        private IDictionary<string, string> BuildHeaders(bool isPost)
        {
            Dictionary<string, string> h = new Dictionary<string, string>(StringComparer.Ordinal);
            // Caller-supplied headers first (Authorization, etc.).
            foreach (KeyValuePair<string, string> kv in _baseHeaders) h[kv.Key] = kv.Value;

            h["Accept"] = "application/json, text/event-stream";
            if (isPost) h["Content-Type"] = "application/json";
            if (_protocolVersion != null) h["MCP-Protocol-Version"] = _protocolVersion;
            if (_sessionId != null) h["Mcp-Session-Id"] = _sessionId;
            return h;
        }

        private void CaptureNegotiatedVersion(JsonRpcResponse initResult)
        {
            try
            {
                if (initResult.Result != null && initResult.Result.Type == JTokenType.Object)
                {
                    JToken v = initResult.Result["protocolVersion"];
                    if (v != null && v.Type == JTokenType.String) _protocolVersion = (string)v;
                }
            }
            catch (Exception ex)
            {
                _log.Log("mcp-http", "could not read negotiated protocolVersion: " + ex.Message);
            }
        }

        private JObject ParseObject(string body, string method)
        {
            try
            {
                return (JObject)McpJson.Parse(body);
            }
            catch (Exception ex)
            {
                throw new McpTransportException("unparseable HTTP response for '" + method + "': " + ex.Message, ex);
            }
        }

        private void RaiseInbound(JObject obj)
        {
            EventHandler<JsonRpcInboundEventArgs> handler = Inbound;
            if (handler == null) return;

            JsonRpcInboundEventArgs args = new JsonRpcInboundEventArgs();
            args.Method = (string)obj["method"];
            args.Params = obj["params"];
            args.Raw = obj;
            args.IsRequest = obj.Property("id") != null;
            if (args.IsRequest)
            {
                JToken idTok = obj["id"];
                if (idTok != null && idTok.Type == JTokenType.Integer) args.Id = RequestId.FromLong(idTok.Value<long>());
                else if (idTok != null && idTok.Type == JTokenType.String) args.Id = RequestId.FromString(idTok.Value<string>());
            }
            try { handler(this, args); }
            catch (Exception ex) { _log.Log("mcp-http", "inbound handler threw: " + ex.Message); }
        }
    }
}
