using System;
using System.Collections.Generic;

namespace GxPT
{
    // Per-turn visibility of the SkillsMcpServer tools. Two rules, applied to the model's context
    // (manifest + reveal + call):
    //   * Authoring tools are "owned" by a single meta-skill, the bundled skill-writer: hidden unless
    //     that skill is enabled (a deliberate hardcoded link, NOT a general per-skill tool gate).
    //   * ALL skills tools (authoring + run_skill_script) are hidden when NO skill is enabled for the
    //     conversation - the server may still be running (it is shared across the workdir), but its tools
    //     have no business in a turn where skills aren't in play.
    internal static class SkillToolGate
    {
        // The meta-skill that owns the authoring tools (its slug / folder name).
        public const string SkillWriterSlug = "skill-writer";

        // The execution tool - available to any skill that ships a script (not owned by the meta-skill),
        // but still hidden when no skill at all is enabled.
        public static readonly string RunScriptTool = McpConfig.SkillsName + "__run_skill_script";

        // Server-qualified authoring/maintenance tool names gated on the meta-skill (tier 1 + tier 2).
        // The whole authoring surface belongs to the meta-skill - including the ReadOnly list/validate
        // tools, which are only meaningful while writing skills.
        public static readonly string[] AuthoringTools = new string[]
        {
            McpConfig.SkillsName + "__create_skill",
            McpConfig.SkillsName + "__write_skill_file",
            McpConfig.SkillsName + "__update_skill",
            McpConfig.SkillsName + "__edit_skill_file",
            McpConfig.SkillsName + "__list_skill_files",
            McpConfig.SkillsName + "__delete_skill_file",
            McpConfig.SkillsName + "__delete_skill",
            McpConfig.SkillsName + "__validate_skill"
        };

        // The tool names to hide this turn: authoring tools unless skill-writer is enabled, plus
        // run_skill_script when NO skill is enabled at all (then every skills tool is hidden).
        public static ICollection<string> HiddenTools(IEnumerable<Skill> enabledSkills)
        {
            bool anyEnabled = false, writerEnabled = false;
            if (enabledSkills != null)
            {
                foreach (Skill s in enabledSkills)
                {
                    if (s == null) continue;
                    anyEnabled = true;
                    if (string.Equals(s.Slug, SkillWriterSlug, StringComparison.OrdinalIgnoreCase))
                        writerEnabled = true;
                }
            }

            List<string> hidden = new List<string>();
            if (!writerEnabled) hidden.AddRange(AuthoringTools);
            if (!anyEnabled) hidden.Add(RunScriptTool);
            return hidden;
        }
    }
}
