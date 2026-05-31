namespace Mcp35.Core.Protocol
{
    /// <summary>MCP method-name constants.</summary>
    public static class McpMethods
    {
        public const string Initialize = "initialize";
        public const string Initialized = "notifications/initialized";
        public const string ToolsList = "tools/list";
        public const string ToolsCall = "tools/call";
        public const string ToolsListChanged = "notifications/tools/list_changed";
        public const string Ping = "ping";
    }
}
