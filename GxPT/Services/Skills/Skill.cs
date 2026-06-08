using System;

namespace GxPT
{
    // Where a discovered skill came from. Project skills shadow bundled skills of the same slug
    // (design S2). The source is surfaced in the approval prompt for run_skill_script (design S15).
    internal enum SkillSource
    {
        Bundled,
        Project
    }

    // One discovered skill: its kebab-case handle (slug), the frontmatter name/description, and where
    // its files live on disk. The catalog holds a slug -> Skill map, so resolution never derives a path
    // from the slug at runtime - each Skill carries its own Directory (design S5). XP / .NET 3.5 friendly.
    internal sealed class Skill
    {
        // Kebab-case handle: appears in the manifest, taken by open_skill / read_skill_file /
        // run_skill_script, and typed in the slash command.
        public string Slug { get; private set; }

        // Frontmatter "name" (human label); falls back to the slug when the author omits it.
        public string Name { get; private set; }

        // Frontmatter "description": the single manifest line the model sees ("use this when ...").
        public string Description { get; private set; }

        // The skill's own folder (a read-only asset source at runtime, never a working dir - design S14).
        public string Directory { get; private set; }

        // Absolute path to the skill's SKILL.md.
        public string SkillFilePath { get; private set; }

        public SkillSource Source { get; private set; }

        public Skill(string slug, string name, string description,
                     string directory, string skillFilePath, SkillSource source)
        {
            Slug = slug;
            Name = name;
            Description = description;
            Directory = directory;
            SkillFilePath = skillFilePath;
            Source = source;
        }
    }
}
