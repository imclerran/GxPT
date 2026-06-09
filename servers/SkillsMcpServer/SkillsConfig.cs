using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace SkillsMcpServer
{
    /// <summary>
    /// Startup config from the environment (servers-spec sec.1), read once. The writable skill roots:
    /// project = GXPT_WORKDIR/.gxpt/skills; user = GXPT_SKILLS_USER_ROOT (not wired yet). WorkDir and the
    /// bundled root (GXPT_SKILLS_BUNDLED_ROOT) are kept for run_skill_script, added later.
    /// </summary>
    internal sealed class SkillsConfig
    {
        public readonly string WorkDir;       // conversation workspace (cwd for scripts), or null
        public readonly string ProjectRoot;   // <workdir>/.gxpt/skills, or null when no workspace
        public readonly string UserRoot;      // %AppData%/GxPT/skills, or null until wired
        public readonly string BundledRoot;   // <exe>/skills, or null (for run_skill_script later)

        private SkillsConfig(string workDir, string projectRoot, string userRoot, string bundledRoot)
        {
            WorkDir = workDir;
            ProjectRoot = projectRoot;
            UserRoot = userRoot;
            BundledRoot = bundledRoot;
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

            if (log != null)
                log.Log("skills", "project=" + (projectRoot != null ? projectRoot : "(none)")
                    + " user=" + (userRoot != null ? userRoot : "(none)"));

            return new SkillsConfig(workDir, projectRoot, userRoot, bundledRoot);
        }
    }
}
