using System;
using Mcp35.Core.Diagnostics;

namespace Mcp35.Server
{
    /// <summary>
    /// Routes all diagnostics to <c>stderr</c>. Critical: a server's <c>stdout</c> carries the
    /// JSON-RPC stream and nothing else (§5), so logging must never touch it.
    /// </summary>
    public sealed class StdErrLogSink : ILogSink
    {
        public void Log(string category, string message)
        {
            try
            {
                Console.Error.WriteLine("[" + (category ?? "mcp") + "] " + message);
            }
            catch
            {
                // Never let logging failures propagate into the dispatch loop.
            }
        }
    }
}
