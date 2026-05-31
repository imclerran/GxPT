using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Rpc
{
    /// <summary>The <c>error</c> member of a JSON-RPC response.</summary>
    public sealed class JsonRpcError
    {
        [JsonProperty("code")]
        public int Code;

        [JsonProperty("message")]
        public string Message;

        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public JToken Data;

        public JsonRpcError() { }

        public JsonRpcError(int code, string message)
        {
            Code = code;
            Message = message;
        }

        public override string ToString()
        {
            return "JSON-RPC error " + Code + ": " + Message;
        }
    }
}
