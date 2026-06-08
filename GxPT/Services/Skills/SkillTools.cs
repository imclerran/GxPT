using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The skills execution-surface meta-tool (phase 3): open_skill. Host-synthesized and handled inside
    // the orchestrator without an MCP round-trip - the skills analogue of reveal_tools (design S1/S7).
    // open_skill loads a skill's full SKILL.md body on demand, plus the resolved on-disk skill directory
    // and a listing of its bundled assets, so the model can follow the instructions (and, in later
    // phases, read_skill_file / run_skill_script those assets by handle). Reads are fresh each call, so
    // edits to a SKILL.md take effect immediately.
    internal sealed class SkillTools
    {
        public const string OpenSkillName = "open_skill";

        // Keep a single skill body from blowing up the context if a SKILL.md is huge.
        private const int MaxBodyChars = 32 * 1024;
        private const int MaxAssetsListed = 200;

        private readonly SkillCatalog _catalog;

        public SkillTools(SkillCatalog catalog)
        {
            _catalog = catalog;
        }

        public bool HasSkills
        {
            get { return _catalog != null && _catalog.Skills.Count > 0; }
        }

        public bool IsOpenSkill(string functionName)
        {
            return functionName == OpenSkillName;
        }

        // The OpenAI-style function definition for open_skill: { names: string[] } (required), mirroring
        // reveal_tools' shape so the model treats the two meta-tools the same way.
        public JObject OpenSkillDef()
        {
            JObject items = new JObject();
            items["type"] = "string";
            JObject namesProp = new JObject();
            namesProp["type"] = "array";
            namesProp["items"] = items;
            JObject props = new JObject();
            props["names"] = namesProp;
            JObject schema = new JObject();
            schema["type"] = "object";
            schema["properties"] = props;
            schema["required"] = new JArray("names");

            JObject fn = new JObject();
            fn["name"] = OpenSkillName;
            fn["description"] = "Load one or more skills' full instructions by slug so you can follow "
                + "them. Pass the slugs from the skills list.";
            fn["parameters"] = schema;

            JObject def = new JObject();
            def["type"] = "function";
            def["function"] = fn;
            return def;
        }

        // Resolve each slug to its rendered skill block (body + directory + asset listing). Unknown slugs
        // become a short note rather than an error, so a partly-wrong batch still returns the rest.
        public string Open(string[] names)
        {
            if (names == null || names.Length == 0) return "No skill names provided.";
            if (_catalog == null) return "No skills are available.";

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < names.Length; i++)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                string slug = names[i];
                Skill skill;
                if (slug == null || !_catalog.TryGet(slug, out skill))
                {
                    sb.Append("Unknown skill: ").Append(slug != null ? slug : "(null)");
                    continue;
                }
                sb.Append(RenderSkill(skill));
            }
            return sb.ToString();
        }

        private static string RenderSkill(Skill skill)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# Skill: ").Append(skill.Slug).Append('\n');
            sb.Append("Skill files are at: ").Append(skill.Directory).Append('\n');

            string assets = ListAssets(skill.Directory);
            if (!string.IsNullOrEmpty(assets))
            {
                sb.Append("Bundled files (relative to the skill directory):\n");
                sb.Append(assets).Append('\n');
            }

            sb.Append('\n');
            string body = ReadBody(skill);
            sb.Append(!string.IsNullOrEmpty(body) ? body : "(this skill has no instructions)");
            return sb.ToString();
        }

        private static string ReadBody(Skill skill)
        {
            try
            {
                string text = File.ReadAllText(skill.SkillFilePath, Encoding.UTF8);
                SkillFrontmatter fm = SkillFrontmatter.Parse(text);
                string body = fm.Body;
                if (body != null && body.Length > MaxBodyChars)
                    body = body.Substring(0, MaxBodyChars) + "\n[skill truncated]";
                return body;
            }
            catch
            {
                return null;
            }
        }

        // Lists files under the skill dir (relative, forward-slashed), excluding SKILL.md, capped.
        private static string ListAssets(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
                string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                List<string> rel = new List<string>();
                for (int i = 0; i < files.Length && rel.Count < MaxAssetsListed; i++)
                {
                    string full = files[i];
                    if (string.Equals(Path.GetFileName(full), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string r = full.Substring(dir.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');
                    rel.Add(r);
                }
                if (rel.Count == 0) return null;
                rel.Sort(StringComparer.Ordinal);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < rel.Count; i++)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("- ").Append(rel[i]);
                }
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
