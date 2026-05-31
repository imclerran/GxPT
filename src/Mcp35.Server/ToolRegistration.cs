using Mcp35.Core.Diagnostics;
using Mcp35.Core.Protocol;
using Newtonsoft.Json.Linq;

namespace Mcp35.Server
{
    /// <summary>A tool implementation: receives the call context, returns a result (failures are values).</summary>
    public delegate CallToolResult ToolHandler(ToolCallContext ctx);

    /// <summary>Everything a handler needs for one <c>tools/call</c> invocation.</summary>
    public sealed class ToolCallContext
    {
        private readonly string _toolName;
        private readonly JObject _arguments;
        private readonly ILogSink _log;

        public ToolCallContext(string toolName, JObject arguments, ILogSink log)
        {
            _toolName = toolName;
            // MCP allows arguments to be omitted; normalize null to an empty object so handlers
            // never have to null-check ctx.Arguments.
            _arguments = arguments ?? new JObject();
            _log = log ?? NullLogSink.Instance;
        }

        public string ToolName { get { return _toolName; } }
        public JObject Arguments { get { return _arguments; } }
        public ILogSink Log { get { return _log; } }
    }

    /// <summary>A registered tool: its public MCP descriptor plus the handler to invoke.</summary>
    internal sealed class RegisteredTool
    {
        public readonly Tool Descriptor;
        public readonly ToolHandler Handler;

        public RegisteredTool(Tool descriptor, ToolHandler handler)
        {
            Descriptor = descriptor;
            Handler = handler;
        }
    }
}
