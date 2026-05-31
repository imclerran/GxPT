using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Rpc
{
    /// <summary>
    /// A JSON-RPC request. <see cref="Id"/> is always written; a request without an id is a
    /// <see cref="JsonRpcNotification"/> (a separate type, so presence is structural).
    /// </summary>
    public sealed class JsonRpcRequest
    {
        [JsonProperty("jsonrpc")]
        public string JsonRpc = "2.0";

        [JsonProperty("id")]
        public RequestId Id;

        [JsonProperty("method")]
        public string Method;

        [JsonProperty("params", NullValueHandling = NullValueHandling.Ignore)]
        public JToken Params;
    }
}
