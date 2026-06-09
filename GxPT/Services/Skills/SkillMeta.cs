using System;
using System.Collections.Generic;

namespace GxPT
{
    // The skill-authoring MCP tools (from SkillsMcpServer) are "owned" by a single meta-skill: the
    // bundled skill-writing skill. They are omitted from the model's context (manifest + reveal + call)
    // whenever that skill is NOT enabled for the conversation - a deliberate, hardcoded link (NOT a
    // general per-skill tool-gating feature). run_skill_script (execution) is intentionally not here.
    internal static class SkillMeta
    {
        // The meta-skill that owns the authoring tools (its slug / folder name).
        public const string SkillWriterSlug = "skill-writer";

        // Server-qualified authoring tool names gated on the meta-skill.
        public static readonly string[] AuthoringTools = new string[]
        {
            McpConfig.SkillsName + "__create_skill",
            McpConfig.SkillsName + "__write_skill_file",
            McpConfig.SkillsName + "__update_skill"
        };

        // The tool names to hide this turn: the authoring tools, UNLESS the meta-skill is among the
        // enabled skills (then nothing is hidden). Empty when the meta-skill is enabled.
        public static ICollection<string> HiddenTools(IEnumerable<Skill> enabledSkills)
        {
            if (enabledSkills != null)
            {
                foreach (Skill s in enabledSkills)
                {
                    if (s != null && string.Equals(s.Slug, SkillWriterSlug, StringComparison.OrdinalIgnoreCase))
                        return new string[0]; // meta-skill enabled -> authoring tools visible
                }
            }
            return AuthoringTools;
        }
    }
}
