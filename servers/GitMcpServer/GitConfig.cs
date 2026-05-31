using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace GitMcpServer
{
    /// <summary>
    /// Startup config from the environment (servers-spec §1), read once. Git runs against
    /// GXPT_WORKDIR using GXPT_GIT_PATH (default "git", resolved on PATH).
    /// </summary>
    internal sealed class GitConfig
    {
        public readonly string WorkDir;
        public readonly string GitPath;

        private GitConfig(string workDir, string gitPath)
        {
            WorkDir = workDir;
            GitPath = gitPath;
        }

        public static GitConfig FromEnvironment(ILogSink log)
        {
            string workDir = Environment.GetEnvironmentVariable("GXPT_WORKDIR");
            if (string.IsNullOrEmpty(workDir)) workDir = Directory.GetCurrentDirectory();

            string gitPath = Environment.GetEnvironmentVariable("GXPT_GIT_PATH");
            if (string.IsNullOrEmpty(gitPath)) gitPath = "git";

            if (log != null) log.Log("git", "workdir=" + workDir + " git=" + gitPath);
            return new GitConfig(workDir, gitPath);
        }
    }
}
