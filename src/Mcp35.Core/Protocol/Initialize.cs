using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Protocol
{
    /// <summary>Identifies the client or server implementation in the handshake.</summary>
    public sealed class Implementation
    {
        [JsonProperty("name")]
        public string Name;

        [JsonProperty("version")]
        public string Version;
    }

    /// <summary>
    /// Params for the <c>initialize</c> request. Capabilities are kept as a JObject in phase 1
    /// (we only need to detect capabilities.tools); promote to typed sub-DTOs later if needed.
    /// </summary>
    public sealed class InitializeParams
    {
        [JsonProperty("protocolVersion")]
        public string ProtocolVersion;

        [JsonProperty("capabilities")]
        public JObject Capabilities;

        [JsonProperty("clientInfo")]
        public Implementation ClientInfo;
    }

    /// <summary>Result of the <c>initialize</c> request.</summary>
    public sealed class InitializeResult
    {
        [JsonProperty("protocolVersion")]
        public string ProtocolVersion;

        [JsonProperty("capabilities")]
        public JObject Capabilities; // ServerCapabilities; we read capabilities.tools

        [JsonProperty("serverInfo")]
        public Implementation ServerInfo;

        [JsonProperty("instructions", NullValueHandling = NullValueHandling.Ignore)]
        public string Instructions;
    }
}
