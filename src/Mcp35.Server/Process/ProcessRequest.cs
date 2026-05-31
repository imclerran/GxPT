using System.Collections.Generic;

namespace Mcp35.Server.Process
{
    /// <summary>
    /// One external-process invocation. Argument escaping/quoting is the <b>caller's</b>
    /// responsibility — <see cref="ProcessRunner"/> does not build command lines (§6).
    /// </summary>
    public sealed class ProcessRequest
    {
        /// <summary>Resolved executable path (injected or found on PATH by the caller).</summary>
        public string FileName;

        /// <summary>Pre-escaped command-line arguments.</summary>
        public string Arguments;

        public string WorkingDirectory;

        /// <summary>Extra environment variables to set for the child (added to the inherited block).</summary>
        public IDictionary<string, string> Environment;

        /// <summary>Optional text to write to the child's stdin (then stdin is closed).</summary>
        public string StdinText;

        /// <summary>Kill the process if it runs longer than this. 0 or less = no timeout.</summary>
        public int TimeoutMs;
    }
}
