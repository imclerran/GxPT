using Mcp35.Core.Protocol;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace SkillsMcpServer
{
    /// <summary>
    /// The skill authoring tools (design S16, tier 1): create_skill, write_skill_file, update_skill. All
    /// Write-tier (the host gates them by (scope, slug)). The server validates structure (slug, frontmatter)
    /// and owns encoding/atomic writes, so the model never produces an unloadable SKILL.md. run_skill_script
    /// (execution) and tier-2 tools register here too once added.
    /// </summary>
    internal static class SkillsTools
    {
        public static void Register(McpServer server, SkillsConfig config)
        {
            SkillWriter writer = new SkillWriter(config.ProjectRoot, config.UserRoot);

            server.AddTool("create_skill",
                "Create a NEW skill: writes its SKILL.md from the given fields (the server assembles valid "
                + "frontmatter). Refuses if the skill already exists - use update_skill to change one. The "
                + "skill becomes available on the user's next message.",
                SchemaBuilder.Object()
                    .Str("slug", true, "Kebab-case handle (lowercase words joined by single hyphens, e.g. "
                        + "release-notes); normalized automatically. This is the skill's folder name and handle.")
                    .Str("name", true, "Human-readable name (Title Case).")
                    .Str("description", true, "Single line shown in the skills list - phrase it as 'use this when ...'.")
                    .Str("body", true, "The skill's instructions (markdown). Tell the model how to do the task.")
                    .Str("scope", false, "Where to create it: 'project' (default, this workspace's .gxpt/skills) or 'user'.")
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx)
                {
                    try
                    {
                        return ToolResults.Text(writer.CreateSkill(
                            Str(ctx, "scope"), Str(ctx, "slug"), Str(ctx, "name"),
                            Str(ctx, "description"), Str(ctx, "body")));
                    }
                    catch (SkillWriteException ex) { return ToolResults.Error(ex.Message); }
                });

            server.AddTool("write_skill_file",
                "Write a supporting file or script into an existing skill's folder, by a path relative to the "
                + "skill folder (subdirs allowed). For SKILL.md use create_skill / update_skill instead. "
                + "Scripts should be .bat/.cmd; they are written with CRLF line endings.",
                SchemaBuilder.Object()
                    .Str("slug", true, "The skill's slug (it must already exist).")
                    .Str("relpath", true, "Path relative to the skill folder, e.g. scripts/gen.bat or examples/foo.md.")
                    .Str("content", true, "The file's text content.")
                    .Str("scope", false, "'project' (default) or 'user'.")
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx)
                {
                    try
                    {
                        return ToolResults.Text(writer.WriteFile(
                            Str(ctx, "scope"), Str(ctx, "slug"), Str(ctx, "relpath"), Str(ctx, "content")));
                    }
                    catch (SkillWriteException ex) { return ToolResults.Error(ex.Message); }
                });

            server.AddTool("update_skill",
                "Edit an existing skill's SKILL.md. Pass only the fields to change; omitted fields are left "
                + "unchanged. The server re-assembles valid frontmatter.",
                SchemaBuilder.Object()
                    .Str("slug", true, "The skill's slug (it must already exist).")
                    .Str("name", false, "New name, or omit to keep.")
                    .Str("description", false, "New description, or omit to keep.")
                    .Str("body", false, "New instructions, or omit to keep.")
                    .Str("scope", false, "'project' (default) or 'user'.")
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx)
                {
                    try
                    {
                        return ToolResults.Text(writer.UpdateSkill(
                            Str(ctx, "scope"), Str(ctx, "slug"), Str(ctx, "name"),
                            Str(ctx, "description"), Str(ctx, "body")));
                    }
                    catch (SkillWriteException ex) { return ToolResults.Error(ex.Message); }
                });
        }

        // A string arg, or null when absent/null (so optional fields stay "unchanged" in update_skill).
        private static string Str(ToolCallContext ctx, string key)
        {
            JToken t = ctx.Arguments[key];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.String) return (string)t;
            return t.ToString();
        }
    }
}
