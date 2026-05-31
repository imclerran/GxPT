using System;
using System.Collections.Generic;
using System.Threading;
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
    }
}
