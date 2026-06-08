using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GxPT
{
    // Host side of the skills feature (phase 2): resolves the bundled (<exe>/skills) and project
    // (<workdir>/.gxpt/skills) roots, builds the SkillCatalog, and frames the always-on skills manifest
    // for injection as an ephemeral system message (McpChatOrchestrator.SkillsManifestSystemMessageProvider),
    // ordered right after the memory block and before the MCP names manifest (design sec.5). All skills
    // framing lives here, so when no skills are present nothing is injected and the feature leaves no
    // trace in context.
    internal static class SkillInjection
    {
        public const string SkillsDirName = "skills";

        // Bundled skills ship beside GxPT.exe (deployed by the AfterBuild copy like mcp-servers).
        public static string BundledRoot(string exeDir)
        {
            if (string.IsNullOrEmpty(exeDir)) return null;
            return Path.Combine(exeDir, SkillsDirName);
        }

        // Project skills live under the conversation's working folder, reusing the memory system's .gxpt.
        public static string ProjectRoot(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;
            return Path.Combine(Path.Combine(workingDir, MemoryInjection.MemoryDirName), SkillsDirName);
        }

        public static SkillCatalog BuildCatalog(string exeDir, string workingDir)
        {
            return SkillCatalog.Build(BundledRoot(exeDir), ProjectRoot(workingDir));
        }

        // The phase-2 ephemeral block: framing + the slug/description manifest. Returns null when the
        // catalog is empty, so a skill-less conversation injects nothing (no phantom capability).
        public static string BuildManifestMessage(SkillCatalog catalog)
        {
            if (catalog == null) return null;
            IList<Skill> skills = catalog.Skills;
            if (skills == null || skills.Count == 0) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("# Skills\n\n");
            sb.Append("Skills are reusable procedures available for this conversation, listed below as ");
            sb.Append("`- <slug> - <description>`. When a task matches one, load its full instructions by ");
            sb.Append("calling open_skill({\"names\":[\"<slug>\"]}) and then follow them. open_skill is ");
            sb.Append("directly callable - you do NOT need to reveal it first - and you may open several ");
            sb.Append("skills at once. Do not mention skills unless they are relevant to the request.\n\n");
            sb.Append("Available skills:\n");
            sb.Append(catalog.BuildManifest());
            return sb.ToString();
        }
    }
}
