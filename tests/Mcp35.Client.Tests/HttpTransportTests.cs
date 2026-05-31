using System;
using System.Collections.Generic;
using Mcp35.Client;
using Mcp35.Client.Transport;
using Mcp35.Core.Errors;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Mcp35.Client.Tests
{
    public class HttpTransportTests
    {
        private const string Url = "https://api.example.test/mcp/";

        private static HttpTransport NewTransport(FakeCurlRunner curl)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["Authorization"] = "Bearer test-token";
            return new HttpTransport(Url, headers, curl, null);
        }

        // ---- handshake / session ----

        [Fact]
        public void Initialize_returns_result_and_captures_session_id()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                return FakeCurlRunner.Json(200, FakeCurlRunner.InitializeResponse(FakeCurlRunner.IdOf(s), "2025-06-18", true), "sess-123");
            };

            using (HttpTransport t = NewTransport(curl))
            {
                t.Start(); // no-op
                JsonRpcResponse resp = t.SendRequest(McpMethods.Initialize, new JObject(), 5000);
                Assert.False(resp.IsError);
                Assert.True(t.IsConnected);
                Assert.Equal("2025-06-18", (string)resp.Result["protocolVersion"]);
            }
        }

        [Fact]
        public void Session_id_and_version_are_replayed_after_initialize()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                long id = FakeCurlRunner.IdOf(s);
                JObject body = JObject.Parse(s.BodyJson);
                if ((string)body["method"] == McpMethods.Initialize)
                    return FakeCurlRunner.Json(200, FakeCurlRunner.InitializeResponse(id, "2025-06-18", true), "sess-xyz");
                return FakeCurlRunner.Json(200, FakeCurlRunner.Response(id, new JObject()), null);
            };

            using (HttpTransport t = NewTransport(curl))
            {
                t.SendRequest(McpMethods.Initialize, new JObject(), 5000);
                t.SendRequest(McpMethods.ToolsList, null, 5000);

                // First request (initialize) carries no session/version header.
                FakeCurlRunner.Sent first = curl.Requests[0];
                Assert.False(first.Headers.ContainsKey("Mcp-Session-Id"));
                Assert.False(first.Headers.ContainsKey("MCP-Protocol-Version"));

                // Second request replays both.
                FakeCurlRunner.Sent second = curl.Requests[1];
                Assert.Equal("sess-xyz", second.Headers["Mcp-Session-Id"]);
                Assert.Equal("2025-06-18", second.Headers["MCP-Protocol-Version"]);
            }
        }

        [Fact]
        public void Every_request_sends_accept_and_auth_headers()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                return FakeCurlRunner.Json(200, FakeCurlRunner.InitializeResponse(FakeCurlRunner.IdOf(s), "2025-06-18", true), "s");
            };
            using (HttpTransport t = NewTransport(curl))
            {
                t.SendRequest(McpMethods.Initialize, new JObject(), 5000);
                FakeCurlRunner.Sent first = curl.Requests[0];
                Assert.Equal("application/json, text/event-stream", first.Headers["Accept"]);
                Assert.Equal("application/json", first.Headers["Content-Type"]);
                Assert.Equal("Bearer test-token", first.Headers["Authorization"]);
            }
        }

        // ---- Content-Type branching ----

        [Fact]
        public void Json_reply_yields_response()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                JObject result = new JObject();
                result["ok"] = true;
                return FakeCurlRunner.Json(200, FakeCurlRunner.Response(FakeCurlRunner.IdOf(s), result), "s");
            };
            using (HttpTransport t = NewTransport(curl))
            {
                JsonRpcResponse r = t.SendRequest("tools/list", null, 5000);
                Assert.False(r.IsError);
                Assert.True((bool)r.Result["ok"]);
            }
        }

        [Fact]
        public void Sse_reply_yields_matching_response_and_raises_inbound_notifications()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                long id = FakeCurlRunner.IdOf(s);
                // An intervening notification, then the real response — as two SSE events.
                JObject prog = new JObject();
                prog["jsonrpc"] = "2.0";
                prog["method"] = "notifications/progress";
                JObject result = new JObject();
                result["done"] = true;

                string sse =
                    "event: message\n" +
                    "data: " + prog.ToString(Newtonsoft.Json.Formatting.None) + "\n" +
                    "\n" +
                    "event: message\n" +
                    "data: " + FakeCurlRunner.Response(id, result) + "\n" +
                    "\n";
                return FakeCurlRunner.Sse(200, sse, "s");
            };

            using (HttpTransport t = NewTransport(curl))
            {
                List<string> inbound = new List<string>();
                t.Inbound += delegate (object sender, JsonRpcInboundEventArgs e) { inbound.Add(e.Method); };

                JsonRpcResponse r = t.SendRequest("tools/call", new JObject(), 5000);
                Assert.False(r.IsError);
                Assert.True((bool)r.Result["done"]);
                Assert.Contains("notifications/progress", inbound);
            }
        }

        // ---- correlation / errors ----

        [Fact]
        public void Response_id_mismatch_faults_the_call()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                // Reply with a deliberately wrong id.
                return FakeCurlRunner.Json(200, FakeCurlRunner.Response(99999, new JObject()), "s");
            };
            using (HttpTransport t = NewTransport(curl))
            {
                Assert.Throws<McpTransportException>(delegate { t.SendRequest("tools/list", null, 5000); });
            }
        }

        [Fact]
        public void Error_response_passes_through()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                return FakeCurlRunner.Json(200, FakeCurlRunner.ErrorResponse(FakeCurlRunner.IdOf(s), -32601, "no method"), "s");
            };
            using (HttpTransport t = NewTransport(curl))
            {
                JsonRpcResponse r = t.SendRequest("bogus", null, 5000);
                Assert.True(r.IsError);
                Assert.Equal(-32601, r.Error.Code);
            }
        }

        [Fact]
        public void Auth_failure_faults_connection()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s) { return FakeCurlRunner.Json(401, "{}", null); };
            using (HttpTransport t = NewTransport(curl))
            {
                Assert.Throws<McpTransportException>(delegate { t.SendRequest("tools/list", null, 5000); });
                Assert.False(t.IsConnected);
            }
        }

        [Fact]
        public void Post_init_404_faults_as_expired_session()
        {
            var curl = new FakeCurlRunner();
            int call = 0;
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                call++;
                if (call == 1)
                    return FakeCurlRunner.Json(200, FakeCurlRunner.InitializeResponse(FakeCurlRunner.IdOf(s), "2025-06-18", true), "sess");
                return FakeCurlRunner.Json(404, "{}", null);
            };
            using (HttpTransport t = NewTransport(curl))
            {
                t.SendRequest(McpMethods.Initialize, new JObject(), 5000);
                Assert.Throws<McpTransportException>(delegate { t.SendRequest("tools/list", null, 5000); });
                Assert.False(t.IsConnected);
            }
        }

        [Fact]
        public void Curl_failure_is_wrapped_as_transport_exception()
        {
            var curl = new FakeCurlRunner();
            curl.ThrowNext = true;
            using (HttpTransport t = NewTransport(curl))
            {
                Assert.Throws<McpTransportException>(delegate { t.SendRequest("tools/list", null, 5000); });
            }
        }

        // ---- notifications ----

        [Fact]
        public void Notification_posts_and_tolerates_202()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s) { return FakeCurlRunner.Json(202, "", null); };
            using (HttpTransport t = NewTransport(curl))
            {
                t.SendNotification(McpMethods.Initialized, null);
                Assert.Single(curl.Requests);
                JObject body = JObject.Parse(curl.Requests[0].BodyJson);
                Assert.Equal(McpMethods.Initialized, (string)body["method"]);
                Assert.Null(body["id"]); // notifications have no id
            }
        }

        [Fact]
        public void Notification_nonsuccess_is_not_fatal()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s) { return FakeCurlRunner.Json(500, "boom", null); };
            using (HttpTransport t = NewTransport(curl))
            {
                t.SendNotification("notifications/progress", null); // must not throw
            }
        }

        // ---- teardown ----

        [Fact]
        public void Dispose_sends_delete_with_session_header()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                return FakeCurlRunner.Json(200, FakeCurlRunner.InitializeResponse(FakeCurlRunner.IdOf(s), "2025-06-18", true), "sess-del");
            };
            HttpTransport t = NewTransport(curl);
            t.SendRequest(McpMethods.Initialize, new JObject(), 5000);
            t.Dispose();

            FakeCurlRunner.Sent last = curl.Requests[curl.Requests.Count - 1];
            Assert.Equal("DELETE", last.Method);
            Assert.Equal("sess-del", last.Headers["Mcp-Session-Id"]);
        }

        [Fact]
        public void Dispose_without_session_sends_no_delete()
        {
            var curl = new FakeCurlRunner();
            HttpTransport t = NewTransport(curl); // never initialized
            t.Dispose();
            Assert.Empty(curl.Requests); // nothing sent
        }

        // ---- via McpServerConnection: the HTTP floor gate ----

        [Fact]
        public void Connection_faults_when_negotiated_version_below_http_floor()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                // Server returns an old pre-floor version.
                return FakeCurlRunner.Json(200, FakeCurlRunner.InitializeResponse(FakeCurlRunner.IdOf(s), "2024-11-05", true), "s");
            };

            HttpTransport t = NewTransport(curl);
            Implementation client = new Implementation();
            client.Name = "test";
            client.Version = "1.0";
            using (McpServerConnection conn = new McpServerConnection("github", t, client, null))
            {
                conn.MinProtocolVersion = ProtocolVersions.HttpFloor; // host sets this for HTTP
                Assert.ThrowsAny<Exception>(delegate { conn.Open(5000); });
                Assert.Equal(ConnectionState.Faulted, conn.State);
            }
        }

        [Fact]
        public void Connection_opens_when_version_at_or_above_floor()
        {
            var curl = new FakeCurlRunner();
            curl.Responder = delegate (FakeCurlRunner.Sent s)
            {
                long id = FakeCurlRunner.IdOf(s);
                JObject body = JObject.Parse(s.BodyJson);
                if ((string)body["method"] == McpMethods.Initialize)
                    return FakeCurlRunner.Json(200, FakeCurlRunner.InitializeResponse(id, "2025-03-26", true), "s");
                // tools/list prefetch during Open
                JObject result = new JObject();
                result["tools"] = new JArray();
                return FakeCurlRunner.Json(200, FakeCurlRunner.Response(id, result), null);
            };

            HttpTransport t = NewTransport(curl);
            Implementation client = new Implementation();
            client.Name = "test";
            client.Version = "1.0";
            using (McpServerConnection conn = new McpServerConnection("github", t, client, null))
            {
                conn.MinProtocolVersion = ProtocolVersions.HttpFloor;
                conn.Open(5000);
                Assert.Equal(ConnectionState.Ready, conn.State);
            }
        }
    }
}
