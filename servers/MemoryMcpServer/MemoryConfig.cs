using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace MemoryMcpServer
{
    /// <summary>
    /// Startup config, read once from the environment (servers-spec sec.1). Memory's inputs are its
    /// store root - <c>GXPT_WORKDIR</c>/.gxpt (default: the process current dir) - and the soft
    /// line cap for the index, <c>GXPT_MEMORY_MAX_LINES</c> (default 40).
    /// </summary>
    internal sealed class MemoryConfig
    {
        public const int DefaultMaxLines = 40;

        public readonly string MemoryRoot;   // <workdir>/.gxpt
        public readonly int MaxLines;

        private MemoryConfig(string memoryRoot, int maxLines)
        {
            MemoryRoot = memoryRoot;
            MaxLines = maxLines;
        }

        public static MemoryConfig FromEnvironment(ILogSink log)
        {
            string workDir = Environment.GetEnvironmentVariable("GXPT_WORKDIR");
            if (string.IsNullOrEmpty(workDir))
                workDir = Directory.GetCurrentDirectory();
            string root = Path.Combine(workDir, ".gxpt");

            int maxLines = DefaultMaxLines;
            string capRaw = Environment.GetEnvironmentVariable("GXPT_MEMORY_MAX_LINES");
            if (!string.IsNullOrEmpty(capRaw))
            {
                int parsed;
                if (int.TryParse(capRaw.Trim(), out parsed) && parsed > 0)
                    maxLines = parsed;
            }

            if (log != null) log.Log("memory", "root=" + root + " maxLines=" + maxLines);
            return new MemoryConfig(root, maxLines);
        }
    }
}
