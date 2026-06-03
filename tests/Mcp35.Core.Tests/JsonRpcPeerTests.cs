using System;
using System.Collections.Generic;
using System.Threading;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Errors;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Mcp35.Core.Tests
{
    public class JsonRpcPeerTests
    {
        [Fact]
        public void Request_returns_correlated_result()
        {
            var ch = new LoopbackChannel { Responder = LoopbackChannel.EchoResult };
            using (var peer = new JsonRpcPeer(ch, null))
            {
                peer.Start();
                JsonRpcResponse resp = peer.SendRequest("ping", null, 2000);

                Assert.False(resp.IsError);
                Assert.Equal("ping", (string)resp.Result["echo"]);
            }
        }

        [Fact]
        public void Concurrent_requests_each_get_their_own_response()
        {
            var ch = new LoopbackChannel { Responder = LoopbackChannel.EchoResult };
            using (var peer = new JsonRpcPeer(ch, null))
            {
                peer.Start();

                const int n = 12;
                var outcomes = new string[n];
                var threads = new List<Thread>();
                for (int i = 0; i < n; i++)
                {
                    int k = i;
                    var t = new Thread(delegate ()
                    {
                        JsonRpcResponse r = peer.SendRequest("m" + k, null, 5000);
                        outcomes[k] = (string)r.Result["echo"];
                    });
                    threads.Add(t);
                }
                foreach (var t in threads) t.Start();
                foreach (var t in threads) Assert.True(t.Join(10000));

                for (int i = 0; i < n; i++)
                    Assert.Equal("m" + i, outcomes[i]); // each call got exactly its own reply
            }
        }

        [Fact]
        public void Request_times_out_when_no_response()
        {
            var ch = new LoopbackChannel(); // no responder
            using (var peer = new JsonRpcPeer(ch, null))
            {
                peer.Start();
                Assert.Throws<McpTimeoutException>(delegate { peer.SendRequest("slow", null, 100); });
            }
        }

        [Fact]
        public void Notification_raises_inbound_and_sends_no_reply()
        {
            var ch = new LoopbackChannel();
            using (var peer = new JsonRpcPeer(ch, null))
            {
                peer.Start();
                JsonRpcInboundEventArgs got = null;
                peer.Inbound += delegate (object s, JsonRpcInboundEventArgs e) { got = e; };

                ch.Deliver("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");

                Assert.NotNull(got);
                Assert.False(got.IsRequest);
                Assert.Equal("notifications/tools/list_changed", got.Method);
                Assert.Empty(ch.SentSnapshot()); // notifications get no reply
            }
        }

        [Fact]
        public void Ping_request_is_auto_replied_with_empty_result()
        {
            var ch = new LoopbackChannel();
            using (var peer = new JsonRpcPeer(ch, null))
            {
                peer.Start();
                ch.Deliver("{\"jsonrpc\":\"2.0\",\"id\":99,\"method\":\"ping\"}");

                string[] sent = ch.SentSnapshot();
                Assert.Single(sent);
                JObject reply = JObject.Parse(sent[0]);
                Assert.Equal(99, (int)reply["id"]);
                Assert.Equal(JTokenType.Object, reply["result"].Type);
            }
        }

        [Fact]
        public void Unknown_server_request_is_declined_and_surfaced()
        {
            var ch = new LoopbackChannel();
            using (var peer = new JsonRpcPeer(ch, null))
            {
                peer.Start();
                JsonRpcInboundEventArgs got = null;
                peer.Inbound += delegate (object s, JsonRpcInboundEventArgs e) { got = e; };

                ch.Deliver("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"sampling/createMessage\"}");

                string[] sent = ch.SentSnapshot();
                Assert.Single(sent);
                JObject reply = JObject.Parse(sent[0]);
                Assert.Equal(-32601, (int)reply["error"]["code"]);

                Assert.NotNull(got);
                Assert.True(got.IsRequest);
                Assert.Equal(RequestId.FromLong(5), got.Id);
            }
        }

        [Fact]
        public void After_dispose_send_throws_and_not_connected()
        {
            var ch = new LoopbackChannel();
            var peer = new JsonRpcPeer(ch, null);
            peer.Start();
            peer.Dispose();

            Assert.False(peer.IsConnected);
            Assert.Throws<McpTransportException>(delegate { peer.SendRequest("x", null, 1000); });
        }

        [Fact]
        public void Fault_while_request_in_flight_unblocks_caller()
        {
            var ch = new LoopbackChannel(); // no responder => request would hang
            using (var peer = new JsonRpcPeer(ch, null))
            {
                peer.Start();

                Exception caught = null;
                var t = new Thread(delegate ()
                {
                    try { peer.SendRequest("hang", null, 5000); }
                    catch (Exception ex) { caught = ex; }
                });
                t.Start();

                Assert.True(ch.SendSignaled.WaitOne(2000)); // the request is on the wire
                ch.Fault(new Exception("boom"));

                Assert.True(t.Join(2000));
                Assert.IsType<McpTransportException>(caught);
            }
        }

        [Fact]
        public void Dispose_while_request_in_flight_unblocks_caller()
        {
            var ch = new LoopbackChannel(); // no responder => request would hang
            var peer = new JsonRpcPeer(ch, null);
            peer.Start();

            Exception caught = null;
            var t = new Thread(delegate ()
            {
                try { peer.SendRequest("hang", null, 5000); }
                catch (Exception ex) { caught = ex; }
            });
            t.Start();

            Assert.True(ch.SendSignaled.WaitOne(2000)); // the request is on the wire
            peer.Dispose();

            Assert.True(t.Join(2000)); // disposal must wake the parked caller, not let it hang
            Assert.IsType<McpTransportException>(caught);
        }

        [Fact]
        public void Dispose_does_not_report_a_transport_fault()
        {
            // A clean shutdown must not be logged as a fault. This used to surface as
            // "Transport faulted: Cannot access a disposed object. Object name: 'JsonRpcPeer'".
            var log = new RecordingLog();
            var ch = new LoopbackChannel();
            var peer = new JsonRpcPeer(ch, log);
            peer.Start();
            peer.Dispose();

            string all = string.Join("\n", log.Messages);
            Assert.DoesNotContain("Transport faulted", all);
            Assert.DoesNotContain("disposed object", all); // no synthesized ObjectDisposedException
        }

        [Fact]
        public void Real_channel_fault_is_reported_exactly_once()
        {
            // Genuine faults are still surfaced — and idempotently (a second/late fault is ignored).
            var log = new RecordingLog();
            var ch = new LoopbackChannel();
            using (var peer = new JsonRpcPeer(ch, log))
            {
                peer.Start();
                ch.Fault(new Exception("boom"));
                ch.Fault(new Exception("boom-again")); // ignored: already faulted
            }
            Assert.Equal(1, log.Messages.FindAll(delegate (string m) { return m.Contains("Transport faulted"); }).Count);
        }

        [Fact]
        public void Late_channel_callbacks_after_dispose_are_ignored()
        {
            // The reader thread can deliver a final message or fault after teardown; once disposed the
            // peer has unsubscribed, so these are no-ops — no throw, no spurious fault log.
            var log = new RecordingLog();
            var ch = new LoopbackChannel();
            var peer = new JsonRpcPeer(ch, log);
            peer.Start();
            peer.Dispose();

            ch.Fault(new Exception("late fault"));
            ch.Deliver("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");

            Assert.DoesNotContain("Transport faulted", string.Join("\n", log.Messages));
        }

        private sealed class RecordingLog : ILogSink
        {
            public readonly List<string> Messages = new List<string>();
            public void Log(string category, string message)
            {
                lock (Messages) { Messages.Add(category + ": " + message); }
            }
        }
    }
}
