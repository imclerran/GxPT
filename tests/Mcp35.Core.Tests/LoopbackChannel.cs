using System;
using System.Collections.Generic;
using System.Threading;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Tests
{
    /// <summary>
    /// An in-memory <see cref="IDuplexMessageChannel"/> for testing <see cref="JsonRpcPeer"/>:
    /// records everything sent, can auto-respond to requests via <see cref="Responder"/>, and lets
    /// a test inject inbound messages / faults directly.
    /// </summary>
    internal sealed class LoopbackChannel : IDuplexMessageChannel
    {
        private readonly object _lock = new object();
        private readonly List<string> _sent = new List<string>();

        /// <summary>Optional: given a parsed outgoing message, return a response json (or null for none).</summary>
        public Func<JObject, string> Responder;

        /// <summary>Set the first time <see cref="Send"/> is called (handy for "request is in flight" waits).</summary>
        public readonly ManualResetEvent SendSignaled = new ManualResetEvent(false);

        public event Action<string> MessageReceived;
        public event Action<Exception> Faulted;

        public void Start() { }

        public void Send(string json)
        {
            lock (_lock) { _sent.Add(json); }
            SendSignaled.Set();

            if (Responder != null)
            {
                string resp = Responder(JObject.Parse(json));
                if (resp != null)
                {
                    // Deliver on a worker thread to mimic the real reader thread.
                    ThreadPool.QueueUserWorkItem(delegate { Deliver(resp); });
                }
            }
        }

        /// <summary>Synchronously raise MessageReceived (inbound from the "server").</summary>
        public void Deliver(string json)
        {
            Action<string> h = MessageReceived;
            if (h != null) h(json);
        }

        public void Fault(Exception ex)
        {
            Action<Exception> h = Faulted;
            if (h != null) h(ex);
        }

        public void Dispose() { }

        public string[] SentSnapshot()
        {
            lock (_lock) { return _sent.ToArray(); }
        }

        /// <summary>A Responder that echoes a request's method back in its result; ignores notifications.</summary>
        public static string EchoResult(JObject msg)
        {
            JToken id = msg["id"];
            if (id == null) return null; // notification — no response

            JObject result = new JObject();
            result["echo"] = (string)msg["method"];

            JObject resp = new JObject();
            resp["jsonrpc"] = "2.0";
            resp["id"] = id.DeepClone();
            resp["result"] = result;
            return resp.ToString();
        }
    }
}
