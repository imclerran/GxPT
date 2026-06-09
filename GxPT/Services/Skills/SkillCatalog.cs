using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GxPT
{
    // Discovers skills under one or more roots and exposes them by slug. A root holds one folder per
    // skill (<root>/<slug>/SKILL.md). Bundled skills are scanned first, then project skills, which
    // shadow a bundled skill of the same slug (design S2). Build takes explicit root paths so it stays
    // pure and net48-testable; resolving the real roots (<exe>/skills and <workdir>/.gxpt/skills) is the
    // caller's job in a later phase. XP / .NET 3.5 friendly.
    internal sealed class SkillCatalog
    {
        private readonly List<Skill> _skills;                 // sorted by slug (ordinal)
        private readonly Dictionary<string, Skill> _bySlug;   // case-insensitive

        private SkillCatalog(List<Skill> skills, Dictionary<string, Skill> bySlug)
        {
            _skills = skills;
            _bySlug = bySlug;
        }

        // All discovered skills, slug-sorted (read-only).
        public IList<Skill> Skills
        {
            get { return _skills.AsReadOnly(); }
        }

        public bool TryGet(string slug, out Skill skill)
        {
            skill = null;
            if (string.IsNullOrEmpty(slug)) return false;
            return _bySlug.TryGetValue(slug, out skill);
        }

        // Convenience: a catalog with no user-global root (bundled + project only).
        public static SkillCatalog Build(string bundledRoot, string projectRoot)
        {
            return Build(bundledRoot, null, projectRoot);
        }

        // Scans bundled, then user, then project; a more specific source shadows a less specific one of
        // the same slug (project > user > bundled, design S2). Any root may be null/missing - it is
        // simply skipped.
        public static SkillCatalog Build(string bundledRoot, string userRoot, string projectRoot)
        {
            Dictionary<string, Skill> bySlug =
                new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);

            ScanRoot(bundledRoot, SkillSource.Bundled, bySlug);
            ScanRoot(userRoot, SkillSource.User, bySlug);         // user overrides bundled
            ScanRoot(projectRoot, SkillSource.Project, bySlug);   // project overrides user + bundled

            List<Skill> list = new List<Skill>(bySlug.Values);
            list.Sort(delegate(Skill a, Skill b) { return string.CompareOrdinal(a.Slug, b.Slug); });
            return new SkillCatalog(list, bySlug);
        }

        private static void ScanRoot(string root, SkillSource source, Dictionary<string, Skill> bySlug)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { return; }

            for (int i = 0; i < dirs.Length; i++)
            {
                Skill skill = TryLoad(dirs[i], source);
                if (skill != null) bySlug[skill.Slug] = skill;   // last writer wins -> project shadows bundled
            }
        }

        // Loads a skill from a folder if it holds a SKILL.md whose frontmatter declares a non-empty
        // description (the manifest line every skill needs). Returns null for anything malformed, so a
        // bad folder is skipped rather than breaking discovery.
        internal static Skill TryLoad(string dir, SkillSource source)
        {
            if (string.IsNullOrEmpty(dir)) return null;

            string folderName = Path.GetFileName(
                dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string slug = SkillSlug.Make(folderName);
            if (string.IsNullOrEmpty(slug)) return null;

            string file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file)) return null;

            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch { return null; }

            SkillFrontmatter fm = SkillFrontmatter.Parse(text);
            if (fm == null || string.IsNullOrEmpty(fm.Description)) return null;

            string name = (fm.Name != null && fm.Name.Length > 0) ? fm.Name : slug;
            return new Skill(slug, name, fm.Description, dir, file, source);
        }

        // The manifest body the model sees: one "- <slug> - <description>" line per skill, slug-ordered.
        // The surrounding system-message framing + enable filtering is a later phase; this is the list.
        public string BuildManifest()
        {
            return BuildManifest(_skills);
        }

        public static string BuildManifest(IEnumerable<Skill> skills)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Skill s in skills)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("- ").Append(s.Slug).Append(" \u2014 ").Append(s.Description);
            }
            return sb.ToString();
        }
    }
}
