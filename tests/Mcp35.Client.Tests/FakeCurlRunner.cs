using System;
using System.Collections.Generic;
using Mcp35.Core.Transport;
using Newtonsoft.Json.Linq;

namespace Mcp35.Client.Tests
{
    /// <summary>
    /// An in-memory <see cref="ICurlRunner"/> for driving <see cref="Mcp35.Client.Transport.HttpTransport"/>
    /// without a network. A test queues responses (status/headers/body) and the fake records each
    /// request (method, body, headers) so assertions can check what was sent.
    /// </summary>
    internal sealed class FakeCurlRunner : ICurlRunner
    {
        public sealed class Sent
        {
            public string Method;
            public string BodyJson;
            public IDictionary<string, string> Headers;
        }

        public readonly List<Sent> Requests = new List<Sent>();

        /// <summary>Per-call responder: given the outbound request, produce the canned CurlResult.</summary>
        public Func<Sent, CurlResult> Responder;

        public bool ThrowNext;

        public CurlResult Run(CurlRequest req)
        {
            Sent s = new Sent();
            s.Method = req.Method;
            s.BodyJson = req.BodyJson;
            s.Headers = req.Headers;
            Requests.Add(s);

            if (ThrowNext)
            {
                ThrowNext = false;
                throw new Exception("simulated curl failure");
            }

            if (Responder != null) return Responder(s);
            return Json(200, "{}", null);
        }

        public void RunStreaming(CurlRequest req, Action<string> onLine, Action onDone, Action<string> onError)
        {
            throw new NotImplementedException("HttpTransport uses buffered Run only.");
        }

        // ---- response builders ----

        public static CurlResult Json(int status, string body, string sessionId)
        {
            CurlResult r = new CurlResult();
            r.HttpStatus = status;
            r.Body = body;
            r.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            r.Headers["Content-Type"] = "application/json";
            if (sessionId != null) r.Headers["Mcp-Session-Id"] = sessionId;
            return r;
        }

        public static CurlResult Sse(int status, string body, string sessionId)
        {
            CurlResult r = new CurlResult();
            r.HttpStatus = status;
            r.Body = body;
            r.Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            r.Headers["Content-Type"] = "text/event-stream";
            if (sessionId != null) r.Headers["Mcp-Session-Id"] = sessionId;
            return r;
        }

        // ---- JSON-RPC message builders (the fake echoes the request's id) ----

        public static string InitializeResponse(long id, string protocolVersion, bool withTools)
        {
            JObject caps = new JObject();
            if (withTools) caps["tools"] = new JObject();
            JObject serverInfo = new JObject();
            serverInfo["name"] = "github";
            serverInfo["version"] = "1.0";

            JObject result = new JObject();
            result["protocolVersion"] = protocolVersion;
            result["capabilities"] = caps;
            result["serverInfo"] = serverInfo;
            return Response(id, result);
        }

        public static string Response(long id, JToken result)
        {
            JObject o = new JObject();
            o["jsonrpc"] = "2.0";
            o["id"] = id;
            o["result"] = result;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string ErrorResponse(long id, int code, string message)
        {
            JObject err = new JObject();
            err["code"] = code;
            err["message"] = message;
            JObject o = new JObject();
            o["jsonrpc"] = "2.0";
            o["id"] = id;
            o["error"] = err;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Read the JSON-RPC id from a recorded request body.</summary>
        public static long IdOf(Sent s)
        {
            JObject o = JObject.Parse(s.BodyJson);
            return (long)o["id"];
        }
    }
}
