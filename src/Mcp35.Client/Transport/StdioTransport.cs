using System;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;

namespace Mcp35.Client.Transport
{
    /// <summary>
    /// An <see cref="IRpcTransport"/> over a child process: composes a <see cref="StdioChannel"/>
    /// (process + framing) with a Core <see cref="JsonRpcPeer"/> (id-correlation + dispatch). The
    /// transport owns process lifecycle; the peer owns RPC semantics. See mcp35-client-spec.md §2.
    /// </summary>
    public sealed class StdioTransport : IRpcTransport
    {
        private readonly StdioChannel _channel;
        private readonly JsonRpcPeer _peer;

        public StdioTransport(StdioLaunch launch, ILogSink log)
        {
            ILogSink sink = log ?? NullLogSink.Instance;
            _channel = new StdioChannel(launch, sink);
            _peer = new JsonRpcPeer(_channel, sink);
        }

        public event EventHandler<JsonRpcInboundEventArgs> Inbound
        {
            add { _peer.Inbound += value; }
            remove { _peer.Inbound -= value; }
        }

        public void Start()
        {
            // JsonRpcPeer.Start() calls channel.Start() (spawns the process).
            _peer.Start();
        }

        public bool IsConnected
        {
            get { return _peer.IsConnected && _channel.IsAlive; }
        }

        public JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs)
        {
            return _peer.SendRequest(method, @params, timeoutMs);
        }

        public void SendNotification(string method, JToken @params)
        {
            _peer.SendNotification(method, @params);
        }

        public void Dispose()
        {
            // Dispose the peer first (faults pending calls so no caller hangs), then the channel
            // performs graceful stdin-EOF shutdown. JsonRpcPeer.Dispose disposes its channel too,
            // so guard against a double dispose in StdioChannel (idempotent via _stopping).
            _peer.Dispose();
        }
    }
}
