using System;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;

namespace Mcp35.Client.Transport
{
    /// <summary>
    /// Streamable-HTTP transport for remote servers (GitHub). Specified and implemented in
    /// phase 8 (directly over Core's CurlRunner + SseParser — one POST = one correlated response,
    /// no JsonRpcPeer). Present here only so <see cref="Mcp35.Client.McpServerConnection"/> can be
    /// written transport-agnostically; every member throws until phase 8.
    /// </summary>
    public sealed class HttpTransport : IRpcTransport
    {
        public HttpTransport(string endpointUrl, string curlPath, string caBundlePath, ILogSink log)
        {
            // Retained for the phase-8 implementation; intentionally not wired yet.
        }

        private static Exception NotYet()
        {
            return new NotImplementedException("HttpTransport is implemented in phase 8.");
        }

        public event EventHandler<JsonRpcInboundEventArgs> Inbound
        {
            add { throw NotYet(); }
            remove { throw NotYet(); }
        }

        public void Start() { throw NotYet(); }
        public bool IsConnected { get { return false; } }
        public JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs) { throw NotYet(); }
        public void SendNotification(string method, JToken @params) { throw NotYet(); }
        public void Dispose() { }
    }
}
