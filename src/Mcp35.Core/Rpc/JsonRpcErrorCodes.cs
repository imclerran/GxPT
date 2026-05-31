namespace Mcp35.Core.Rpc
{
    /// <summary>
    /// Standard JSON-RPC 2.0 error codes. Server/MCP-defined codes live outside the
    /// reserved -32768..-32000 band. Note: a <em>tool</em> failure is NOT a protocol error;
    /// it rides in CallToolResult.isError, never as a JsonRpcError.
    /// </summary>
    public static class JsonRpcErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        /// <summary>Inclusive lower bound of the reserved pre-defined error band.</summary>
        public const int ReservedMin = -32768;
        /// <summary>Inclusive upper bound of the reserved pre-defined error band.</summary>
        public const int ReservedMax = -32000;

        public static bool IsReserved(int code)
        {
            return code >= ReservedMin && code <= ReservedMax;
        }
    }
}
