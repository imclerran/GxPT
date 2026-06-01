using Mcp35.Core.Diagnostics;

namespace GxPT
{
    // Bridges Mcp35 diagnostics into GxPT's Logger so MCP connection/tool issues (a missing server
    // exe, GitHub auth failures, etc.) show up in the app log when logging is enabled.
    internal sealed class LoggerSink : ILogSink
    {
        public static readonly LoggerSink Instance = new LoggerSink();

        public void Log(string category, string message)
        {
            try { Logger.Log("mcp:" + (category ?? string.Empty), message ?? string.Empty); }
            catch { }
        }
    }
}
