using System;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Rpc
{
    /// <summary>
    /// A framed, full-duplex message channel: one framed JSON string in each direction.
    /// <see cref="JsonRpcPeer"/> layers request/response correlation on top of it.
    /// stdio (Client) implements this over a child process's stdin/stdout.
    /// </summary>
    public interface IDuplexMessageChannel : IDisposable
    {
        void Start();

        /// <summary>Send one framed message (the channel adds whatever framing it uses).</summary>
        void Send(string json);

        /// <summary>One framed message arrived (raised on the channel's reader thread).</summary>
        event Action<string> MessageReceived;

        /// <summary>The channel failed (process exited, stream closed, read error).</summary>
        event Action<Exception> Faulted;
    }

    /// <summary>An inbound server-&gt;client notification or request surfaced to the host.</summary>
    public sealed class JsonRpcInboundEventArgs : EventArgs
    {
        public string Method;
        public JToken Params;

        /// <summary>True when the message carried an id (a server-&gt;client request, not a notification).</summary>
        public bool IsRequest;

        /// <summary>Valid when <see cref="IsRequest"/> is true.</summary>
        public RequestId Id;

        /// <summary>The full raw message object, for handlers that need fields beyond method/params.</summary>
        public JObject Raw;
    }

    /// <summary>
    /// The transport contract that <c>Mcp35.Client</c>'s StdioTransport / HttpTransport implement
    /// and a server connection consumes. Synchronous and blocking — callers invoke it from a
    /// worker thread (just like OpenRouterClient.CreateCompletionStream). See mcp35-core-spec.md §4.
    /// </summary>
    public interface IRpcTransport : IDisposable
    {
        void Start();
        bool IsConnected { get; }

        /// <summary>
        /// Tear down the transport. When <paramref name="forceful"/> is true, favor speed over
        /// politeness for application/host shutdown: kill child processes immediately and skip
        /// best-effort niceties (the graceful stdin-EOF wait, the HTTP session DELETE). When
        /// false, behaves exactly like <see cref="IDisposable.Dispose"/>.
        /// </summary>
        void Shutdown(bool forceful);

        /// <summary>Blocking request/response; the transport allocates the id and correlates.</summary>
        JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs);

        /// <summary>Fire-and-forget client-&gt;server notification.</summary>
        void SendNotification(string method, JToken @params);

        /// <summary>Server-&gt;client notifications (and, later, requests), raised on the reader thread.</summary>
        event EventHandler<JsonRpcInboundEventArgs> Inbound;
    }
}
