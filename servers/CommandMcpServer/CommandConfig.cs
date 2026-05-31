using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace CommandMcpServer
{
    /// <summary>
    /// Startup config from the environment (servers-spec §1), read once. Commands run from
    /// GXPT_WORKDIR via the shell named by GXPT_CMD_SHELL (default cmd.exe from %ComSpec%).
    /// </summary>
    internal sealed class CommandConfig
    {
        public readonly string WorkDir;
        public readonly string Shell;

        private CommandConfig(string workDir, string shell)
        {
            WorkDir = workDir;
            Shell = shell;
        }

        public static CommandConfig FromEnvironment(ILogSink log)
        {
            string workDir = Environment.GetEnvironmentVariable("GXPT_WORKDIR");
            if (string.IsNullOrEmpty(workDir)) workDir = Directory.GetCurrentDirectory();

            string shell = Environment.GetEnvironmentVariable("GXPT_CMD_SHELL");
            if (string.IsNullOrEmpty(shell)) shell = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(shell)) shell = "cmd.exe";

            if (log != null) log.Log("command", "workdir=" + workDir + " shell=" + shell);
            return new CommandConfig(workDir, shell);
        }
    }
}
