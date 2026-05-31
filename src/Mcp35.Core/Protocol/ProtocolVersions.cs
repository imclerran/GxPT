namespace Mcp35.Core.Protocol
{
    /// <summary>
    /// MCP protocol version strings and the negotiation policy knobs. We advertise a single
    /// latest stable version and accept whatever the server negotiates down to (above the HTTP
    /// floor). Revision differences are additive JSON absorbed by tolerant parsing — there is
    /// no per-version parsing code. See mcp35-core-spec.md section 9.
    /// </summary>
    public static class ProtocolVersions
    {
        public const string V2024_11_05 = "2024-11-05"; // old dual-endpoint HTTP+SSE (HTTP unsupported)
        public const string V2025_03_26 = "2025-03-26"; // Streamable HTTP — HTTP floor
        public const string V2025_06_18 = "2025-06-18"; // structured output; MCP-Protocol-Version header
        public const string V2025_11_25 = "2025-11-25"; // latest stable at time of writing

        /// <summary>Advertised in initialize (the single knob; bump when a newer revision is validated).</summary>
        public const string Default = V2025_11_25;

        /// <summary>Minimum acceptable version for an HTTP server (older HTTP used a transport we don't implement).</summary>
        public const string HttpFloor = V2025_03_26;
    }
}
