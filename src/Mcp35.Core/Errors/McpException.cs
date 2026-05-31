using System;
using Mcp35.Core.Rpc;

namespace Mcp35.Core.Errors
{
    /// <summary>
    /// A JSON-RPC error response surfaced as an exception. A <em>tool</em> failure is NOT this —
    /// it is returned normally as CallToolResult.isError so the model can see it.
    /// </summary>
    public class McpException : Exception
    {
        public JsonRpcError Error;

        public McpException(string message)
            : base(message)
        {
        }

        public McpException(JsonRpcError error)
            : base(error == null ? "MCP error" : error.ToString())
        {
            Error = error;
        }

        public McpException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>A request did not receive its response within the allotted timeout.</summary>
    public class McpTimeoutException : McpException
    {
        public McpTimeoutException(string message)
            : base(message)
        {
        }
    }

    /// <summary>The underlying channel/process failed (closed, crashed, or never started).</summary>
    public class McpTransportException : McpException
    {
        public McpTransportException(string message)
            : base(message)
        {
        }

        public McpTransportException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
