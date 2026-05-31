using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Mcp35.Core.Json;

namespace Mcp35.Core.Rpc
{
    /// <summary>
    /// A parsed JSON-RPC response. Parsing is <b>presence-aware</b>: a valid <c>result</c>
    /// may itself be JSON <c>null</c>, which a plain DTO with NullValueHandling.Ignore would
    /// erase. We never rely on a C# null to decide result-vs-error — we branch on which key
    /// is present. See mcp35-core-spec.md section 3.
    /// </summary>
    public sealed class JsonRpcResponse
    {
        public RequestId Id;
        public bool IsError;
        public JToken Result;     // present (possibly a JSON-null JToken) when !IsError
        public JsonRpcError Error; // present when IsError

        public static JsonRpcResponse Parse(JObject o)
        {
            JsonRpcResponse r = new JsonRpcResponse();
            r.Id = ParseId(o["id"]);

            JToken err = o["error"];
            if (err != null && err.Type != JTokenType.Null)
            {
                r.IsError = true;
                r.Error = err.ToObject<JsonRpcError>(JsonSerializer.Create(McpJson.Settings));
                return r;
            }

            // Branch on KEY presence, not value: result:null is a genuine result.
            if (o.Property("result") != null)
            {
                r.IsError = false;
                r.Result = o["result"]; // may be a JTokenType.Null token
                return r;
            }

            // Neither result nor error present: malformed envelope.
            r.IsError = true;
            r.Error = new JsonRpcError(JsonRpcErrorCodes.InternalError,
                "Malformed JSON-RPC response: neither 'result' nor 'error' present.");
            return r;
        }

        public static JsonRpcResponse ParseJson(string json)
        {
            return Parse(McpJson.ParseObject(json));
        }

        private static RequestId ParseId(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return RequestId.Null;
            if (t.Type == JTokenType.Integer) return RequestId.FromLong(t.Value<long>());
            if (t.Type == JTokenType.String) return RequestId.FromString(t.Value<string>());
            // Unexpected id kind (e.g. float); fall back to its string form rather than throw.
            return RequestId.FromString(t.ToString());
        }
    }
}
