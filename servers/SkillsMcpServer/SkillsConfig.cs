using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace SkillsMcpServer
{
    /// <summary>
    /// Startup config from the environment (servers-spec sec.1), read once. The writable skill roots:
    /// project = GXPT_WORKDIR/.gxpt/skills; user = GXPT_SKILLS_USER_ROOT (user-global, %AppData%). WorkDir
    /// and the bundled root (GXPT_SKILLS_BUNDLED_ROOT) are used by run_skill_script.
    /// </summary>
    internal sealed class SkillsConfig
    {
        public readonly string WorkDir;       // conversation workspace (cwd for scripts), or null
        public readonly string ProjectRoot;   // <workdir>/.gxpt/skills, or null when no workspace
        public readonly string UserRoot;      // %AppData%/GxPT/skills (user-global), or null if unset
        public readonly string BundledRoot;   // <exe>/skills, or null (for run_skill_script)
        public readonly string Shell;         // cmd.exe (ComSpec / GXPT_CMD_SHELL) for run_skill_script

        private SkillsConfig(string workDir, string projectRoot, string userRoot, string bundledRoot, string shell)
        {
            WorkDir = workDir;
            ProjectRoot = projectRoot;
            UserRoot = userRoot;
            BundledRoot = bundledRoot;
            Shell = shell;
        }

        // Test-only construction (the production path is FromEnvironment). Internal, so it is reachable
        // only from the linked-source test assembly.
        internal static SkillsConfig ForTesting(string workDir, string projectRoot, string userRoot,
            string bundledRoot, string shell)
        {
            return new SkillsConfig(workDir, projectRoot, userRoot, bundledRoot, shell);
        }

        public static SkillsConfig FromEnvironment(ILogSink log)
        {
            string workDir = Environment.GetEnvironmentVariable("GXPT_WORKDIR");
            if (string.IsNullOrEmpty(workDir)) workDir = null;

            string projectRoot = string.IsNullOrEmpty(workDir)
                ? null
                : Path.Combine(Path.Combine(workDir, ".gxpt"), "skills");

            string userRoot = Environment.GetEnvironmentVariable("GXPT_SKILLS_USER_ROOT");
            if (string.IsNullOrEmpty(userRoot)) userRoot = null;

            string bundledRoot = Environment.GetEnvironmentVariable("GXPT_SKILLS_BUNDLED_ROOT");
            if (string.IsNullOrEmpty(bundledRoot)) bundledRoot = null;

            string shell = Environment.GetEnvironmentVariable("GXPT_CMD_SHELL");
            if (string.IsNullOrEmpty(shell)) shell = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(shell)) shell = "cmd.exe";

            if (log != null)
                log.Log("skills", "project=" + (projectRoot != null ? projectRoot : "(none)")
                    + " user=" + (userRoot != null ? userRoot : "(none)")
                    + " bundled=" + (bundledRoot != null ? bundledRoot : "(none)"));

            return new SkillsConfig(workDir, projectRoot, userRoot, bundledRoot, shell);
        }
    }
}
