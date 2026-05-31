using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace FilesMcpServer
{
    /// <summary>
    /// Startup config, read once from the environment (servers-spec §1). The Files server's only
    /// input is its sandbox root, <c>GXPT_WORKDIR</c> (default: the process current directory).
    /// </summary>
    internal sealed class FilesConfig
    {
        public readonly string WorkDir;

        private FilesConfig(string workDir)
        {
            WorkDir = workDir;
        }

        public static FilesConfig FromEnvironment(ILogSink log)
        {
            string workDir = Environment.GetEnvironmentVariable("GXPT_WORKDIR");
            if (string.IsNullOrEmpty(workDir))
                workDir = Directory.GetCurrentDirectory();

            if (log != null) log.Log("files", "workdir=" + workDir);
            return new FilesConfig(workDir);
        }
    }
}
