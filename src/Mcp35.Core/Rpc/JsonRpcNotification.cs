using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Rpc
{
    /// <summary>
    /// A JSON-RPC notification: a request with no <c>id</c> field at all. Modelled as its own
    /// type (rather than a nullable id) so "is this a notification?" is a structural fact.
    /// </summary>
    public sealed class JsonRpcNotification
    {
        [JsonProperty("jsonrpc")]
        public string JsonRpc = "2.0";

        [JsonProperty("method")]
        public string Method;

        [JsonProperty("params", NullValueHandling = NullValueHandling.Ignore)]
        public JToken Params;
    }
}
