using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace MSBuildMcpServer
{
    /// <summary>
    /// Startup config from the environment (servers-spec §1), read once. Builds run from
    /// GXPT_WORKDIR (the conversation's project folder); MSBuild executables are discovered on the
    /// system, not configured, so there is no extra env var beyond the working directory.
    /// </summary>
    internal sealed class MsBuildConfig
    {
        public readonly string WorkDir;

        private MsBuildConfig(string workDir)
        {
            WorkDir = workDir;
        }

        public static MsBuildConfig FromEnvironment(ILogSink log)
        {
            string workDir = Environment.GetEnvironmentVariable("GXPT_WORKDIR");
            if (string.IsNullOrEmpty(workDir)) workDir = Directory.GetCurrentDirectory();

            if (log != null) log.Log("msbuild", "workdir=" + workDir);
            return new MsBuildConfig(workDir);
        }
    }
}
