using System;
using System.Collections.Generic;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Mcp35.Server.Process;
using Newtonsoft.Json.Linq;

namespace SkillsMcpServer
{
    /// <summary>
    /// The skill authoring tools (design S16). Tier 1 (create flow): create_skill, write_skill_file,
    /// update_skill. Tier 2 (maintenance): edit_skill_file, list_skill_files, delete_skill_file,
    /// delete_skill, validate_skill. The host gates writes by (scope, slug); the server validates structure
    /// (slug, frontmatter) and owns encoding/atomic writes, so the model never produces an unloadable
    /// SKILL.md. run_skill_script (execution) registers here too once added.
    /// </summary>
    internal static class SkillsServerTools
    {
        private const int DefaultTimeoutMs = 60000;
        private const int MaxTimeoutMs = 600000;
        private const int OutputCap = 100000; // chars per stream

        public static void Register(McpServer server, SkillsConfig config)
        {
            // Two-mode: with a workspace (GXPT_WORKDIR set) the server offers the full surface and defaults
            // writes to project scope; without one (the workdir-independent instance) it advertises only the
            // authoring tools - run_skill_script and project-scope writes can't work without a workspace -
            // and defaults writes to user (global) scope.
            bool hasWorkdir = !string.IsNullOrEmpty(config.WorkDir);
            string defaultScope = hasWorkdir ? "project" : "user";
            string scopeDesc = hasWorkdir
                ? "'project' (default, this workspace's .gxpt/skills) or 'user' (this machine's global skills)."
                : "'user' (default; this conversation has no project folder). 'project' is unavailable here.";

            SkillWriter writer = new SkillWriter(config.ProjectRoot, config.UserRoot, defaultScope);

            if (hasWorkdir)
            {
                SkillScriptRunner scripts = new SkillScriptRunner(config);
                server.AddTool("run_skill_script",
                    "Run a skill's bundled batch script (.bat/.cmd) by (slug, relpath), passing literal "
                    + "arguments. The script runs in the conversation's working directory (the project folder); "
                    + "its own folder is available via %~dp0 and %GXPT_SKILL_DIR% for reading bundled assets. Arguments are passed "
                    + "verbatim - they are not a shell command, so do not chain commands or use shell operators.",
                    SchemaBuilder.Object()
                        .Str("slug", true, "The skill that owns the script.")
                        .Str("relpath", true, "Path to the script relative to the skill folder, e.g. scripts/gen.bat.")
                        .Arr("args", "string", false, "Arguments to pass to the script, each as a separate literal token.")
                        .Int("timeout_ms", false, "Kill the script after this many milliseconds (default 60000).")
                        .Build(),
                    ToolAnnotations.Destructive(),
                    delegate(ToolCallContext ctx) { return RunScript(scripts, ctx); });
            }

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
                    .Str("scope", false, scopeDesc)
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
                    .Str("scope", false, scopeDesc)
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
                    .Str("scope", false, scopeDesc)
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

            server.AddTool("edit_skill_file",
                "Make a targeted edit to a file in an existing skill by replacing an exact text span (like "
                + "files__edit). old_string must match exactly and be unique unless replace_all is set. "
                + "Works on SKILL.md too, where the edit applies to the instructions BODY only - use "
                + "update_skill to change the name or description.",
                SchemaBuilder.Object()
                    .Str("slug", true, "The skill's slug (it must already exist).")
                    .Str("relpath", true, "Path relative to the skill folder, e.g. SKILL.md or scripts/gen.bat.")
                    .Str("old_string", true, "Exact text to find (must be unique unless replace_all is set).")
                    .Str("new_string", true, "Replacement text.")
                    .Bool("replace_all", false, "Replace every occurrence instead of requiring a unique match.")
                    .Str("scope", false, scopeDesc)
                    .Build(),
                ToolAnnotations.Write(),
                delegate(ToolCallContext ctx)
                {
                    try
                    {
                        return ToolResults.Text(writer.EditFile(
                            Str(ctx, "scope"), Str(ctx, "slug"), Str(ctx, "relpath"),
                            Str(ctx, "old_string"), Str(ctx, "new_string"), Bool(ctx, "replace_all")));
                    }
                    catch (SkillWriteException ex) { return ToolResults.Error(ex.Message); }
                });

            server.AddTool("list_skill_files",
                "List every file in an existing skill's folder, by path relative to the folder. Works for any "
                + "skill regardless of whether it is enabled.",
                SchemaBuilder.Object()
                    .Str("slug", true, "The skill's slug (it must already exist).")
                    .Str("scope", false, scopeDesc)
                    .Build(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx)
                {
                    try { return ToolResults.Text(writer.ListFiles(Str(ctx, "scope"), Str(ctx, "slug"))); }
                    catch (SkillWriteException ex) { return ToolResults.Error(ex.Message); }
                });

            server.AddTool("delete_skill_file",
                "Delete one supporting file or script from a skill, by a path relative to the skill folder. "
                + "Cannot delete SKILL.md - use delete_skill to remove the whole skill.",
                SchemaBuilder.Object()
                    .Str("slug", true, "The skill's slug (it must already exist).")
                    .Str("relpath", true, "Path relative to the skill folder, e.g. scripts/gen.bat (not SKILL.md).")
                    .Str("scope", false, scopeDesc)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx)
                {
                    try { return ToolResults.Text(writer.DeleteFile(Str(ctx, "scope"), Str(ctx, "slug"), Str(ctx, "relpath"))); }
                    catch (SkillWriteException ex) { return ToolResults.Error(ex.Message); }
                });

            server.AddTool("delete_skill",
                "Delete an entire skill: removes its folder and everything in it. This cannot be undone.",
                SchemaBuilder.Object()
                    .Str("slug", true, "The skill's slug (it must already exist).")
                    .Str("scope", false, scopeDesc)
                    .Build(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx)
                {
                    try { return ToolResults.Text(writer.DeleteSkill(Str(ctx, "scope"), Str(ctx, "slug"))); }
                    catch (SkillWriteException ex) { return ToolResults.Error(ex.Message); }
                });

            server.AddTool("validate_skill",
                "Check whether a skill's SKILL.md would load (its frontmatter must declare a non-empty "
                + "description). Reports the parsed name and description, or what is wrong. Read-only.",
                SchemaBuilder.Object()
                    .Str("slug", true, "The skill's slug (it must already exist).")
                    .Str("scope", false, scopeDesc)
                    .Build(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx)
                {
                    try { return ToolResults.Text(writer.ValidateSkill(Str(ctx, "scope"), Str(ctx, "slug"))); }
                    catch (SkillWriteException ex) { return ToolResults.Error(ex.Message); }
                });
        }

        private static CallToolResult RunScript(SkillScriptRunner scripts, ToolCallContext ctx)
        {
            string slug = Str(ctx, "slug");
            string relpath = Str(ctx, "relpath");
            if (string.IsNullOrEmpty(slug)) return ToolResults.Error("slug is required");
            if (string.IsNullOrEmpty(relpath)) return ToolResults.Error("relpath is required");

            List<string> args = StrList(ctx, "args");
            int timeout = IntArg(ctx, "timeout_ms", DefaultTimeoutMs, 1, MaxTimeoutMs);

            SkillScriptTarget target;
            try { target = scripts.Resolve(slug, relpath); }
            catch (SkillScriptException ex) { return ToolResults.Error(ex.Message); }

            ProcessResult result;
            try { result = scripts.RunResolved(target, args, timeout); }
            catch (SkillScriptException ex) { return ToolResults.Error(ex.Message); } // bad argument token
            catch (Exception ex) { return ToolResults.Error("failed to run script: " + ex.Message); }

            bool outTrunc, errTrunc;
            JObject outp = new JObject();
            outp["exitCode"] = result.ExitCode;
            outp["stdout"] = Cap(result.StdOut, out outTrunc);
            outp["stderr"] = Cap(result.StdErr, out errTrunc);
            outp["timedOut"] = result.TimedOut;
            if (outTrunc || errTrunc) outp["truncated"] = true;
            return ToolResults.Json(outp);
        }

        // The string elements of an array arg (skips nulls); empty list when absent.
        private static List<string> StrList(ToolCallContext ctx, string key)
        {
            List<string> list = new List<string>();
            JToken t = ctx.Arguments[key];
            JArray arr = t as JArray;
            if (arr != null)
            {
                foreach (JToken e in arr)
                {
                    if (e == null || e.Type == JTokenType.Null) continue;
                    list.Add(e.Type == JTokenType.String ? (string)e : e.ToString());
                }
            }
            return list;
        }

        private static int IntArg(ToolCallContext ctx, string key, int fallback, int min, int max)
        {
            JToken t = ctx.Arguments[key];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int n;
            try { n = t.Value<int>(); }
            catch { return fallback; }
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        private static string Cap(string s, out bool truncated)
        {
            truncated = false;
            if (s == null) return string.Empty;
            if (s.Length <= OutputCap) return s;
            truncated = true;
            return s.Substring(0, OutputCap);
        }

        // A bool arg, defaulting to false when absent/null.
        private static bool Bool(ToolCallContext ctx, string key)
        {
            JToken t = ctx.Arguments[key];
            if (t == null || t.Type == JTokenType.Null) return false;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            bool b;
            return bool.TryParse(t.ToString(), out b) && b;
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
