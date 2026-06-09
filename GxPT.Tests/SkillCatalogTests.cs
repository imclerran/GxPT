using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillCatalogTests : IDisposable
    {
        private readonly string _root;
        private readonly string _bundled;
        private readonly string _project;

        public SkillCatalogTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skills_" + Guid.NewGuid().ToString("N"));
            _bundled = Path.Combine(_root, "bundled");
            _project = Path.Combine(_root, "project");
            Directory.CreateDirectory(_bundled);
            Directory.CreateDirectory(_project);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        // Writes <root>/<folder>/SKILL.md with the given frontmatter. A null description omits the line.
        private static void WriteSkill(string root, string folder, string name, string description, string body)
        {
            string dir = Path.Combine(root, folder);
            Directory.CreateDirectory(dir);
            StringBuilder sb = new StringBuilder();
            sb.Append("---\n");
            if (name != null) sb.Append("name: ").Append(name).Append('\n');
            if (description != null) sb.Append("description: ").Append(description).Append('\n');
            sb.Append("---\n\n").Append(body == null ? "" : body).Append('\n');
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), sb.ToString(), new UTF8Encoding(false));
        }

        [Fact]
        public void Build_DiscoversBundledSkill()
        {
            WriteSkill(_bundled, "release-notes", "Release Notes", "Draft notes.", "body");

            SkillCatalog cat = SkillCatalog.Build(_bundled, _project);

            Assert.Single(cat.Skills);
            Skill s;
            Assert.True(cat.TryGet("release-notes", out s));
            Assert.Equal("Release Notes", s.Name);
            Assert.Equal("Draft notes.", s.Description);
            Assert.Equal(SkillSource.Bundled, s.Source);
            Assert.EndsWith(Path.Combine("release-notes", "SKILL.md"), s.SkillFilePath);
        }

        [Fact]
        public void Build_NormalizesFolderNameToSlug()
        {
            WriteSkill(_bundled, "Release Notes", null, "Draft notes.", "body");

            SkillCatalog cat = SkillCatalog.Build(_bundled, _project);

            Skill s;
            Assert.True(cat.TryGet("release-notes", out s));
            // No frontmatter name -> Name falls back to the slug.
            Assert.Equal("release-notes", s.Name);
        }

        [Fact]
        public void Build_SkipsFolderWithoutSkillMd()
        {
            Directory.CreateDirectory(Path.Combine(_bundled, "not-a-skill"));

            SkillCatalog cat = SkillCatalog.Build(_bundled, _project);

            Assert.Empty(cat.Skills);
        }

        [Fact]
        public void Build_SkipsSkillWithoutDescription()
        {
            WriteSkill(_bundled, "no-desc", "No Desc", null, "body");

            SkillCatalog cat = SkillCatalog.Build(_bundled, _project);

            Assert.Empty(cat.Skills);
        }

        [Fact]
        public void Build_ProjectShadowsBundledOfSameSlug()
        {
            WriteSkill(_bundled, "shared", "Bundled One", "From bundled.", "b");
            WriteSkill(_project, "shared", "Project One", "From project.", "p");

            SkillCatalog cat = SkillCatalog.Build(_bundled, _project);

            Assert.Single(cat.Skills);
            Skill s;
            Assert.True(cat.TryGet("shared", out s));
            Assert.Equal(SkillSource.Project, s.Source);
            Assert.Equal("From project.", s.Description);
        }

        [Fact]
        public void Build_DiscoversUserSkill()
        {
            string user = Path.Combine(_root, "user");
            WriteSkill(user, "my-skill", "My Skill", "A user-global skill.", "u");

            SkillCatalog cat = SkillCatalog.Build(_bundled, user, _project);

            Skill s;
            Assert.True(cat.TryGet("my-skill", out s));
            Assert.Equal(SkillSource.User, s.Source);
            Assert.Equal("A user-global skill.", s.Description);
        }

        [Fact]
        public void Build_UserShadowsBundled_ProjectShadowsUser()
        {
            string user = Path.Combine(_root, "user");
            WriteSkill(_bundled, "shared", null, "From bundled.", "b");
            WriteSkill(user, "shared", null, "From user.", "u");

            // user shadows bundled
            Skill s;
            Assert.True(SkillCatalog.Build(_bundled, user, null).TryGet("shared", out s));
            Assert.Equal(SkillSource.User, s.Source);
            Assert.Equal("From user.", s.Description);

            // project shadows user (and bundled)
            WriteSkill(_project, "shared", null, "From project.", "p");
            Assert.True(SkillCatalog.Build(_bundled, user, _project).TryGet("shared", out s));
            Assert.Equal(SkillSource.Project, s.Source);
            Assert.Equal("From project.", s.Description);
        }

        [Fact]
        public void Build_MergesBundledAndProject_SortedBySlug()
        {
            WriteSkill(_bundled, "zebra", null, "Z.", "b");
            WriteSkill(_bundled, "alpha", null, "A.", "b");
            WriteSkill(_project, "mango", null, "M.", "p");

            SkillCatalog cat = SkillCatalog.Build(_bundled, _project);

            IList<Skill> skills = cat.Skills;
            Assert.Equal(3, skills.Count);
            Assert.Equal("alpha", skills[0].Slug);
            Assert.Equal("mango", skills[1].Slug);
            Assert.Equal("zebra", skills[2].Slug);
        }

        [Fact]
        public void Build_NullOrMissingRoots_AreSkipped()
        {
            WriteSkill(_bundled, "only-bundled", null, "Just one.", "b");

            SkillCatalog cat = SkillCatalog.Build(_bundled, null);
            Assert.Single(cat.Skills);

            SkillCatalog empty = SkillCatalog.Build(
                Path.Combine(_root, "does-not-exist"), null);
            Assert.Empty(empty.Skills);
        }

        [Fact]
        public void TryGet_UnknownSlug_ReturnsFalse()
        {
            SkillCatalog cat = SkillCatalog.Build(_bundled, _project);

            Skill s;
            Assert.False(cat.TryGet("nope", out s));
            Assert.Null(s);
            Assert.False(cat.TryGet(null, out s));
        }

        [Fact]
        public void BuildManifest_OneLinePerSkill_SlugOrdered()
        {
            WriteSkill(_bundled, "beta", null, "Second.", "b");
            WriteSkill(_bundled, "alpha", null, "First.", "b");

            SkillCatalog cat = SkillCatalog.Build(_bundled, _project);

            string manifest = cat.BuildManifest();
            string expected = "- alpha \u2014 First.\n- beta \u2014 Second.";
            Assert.Equal(expected, manifest);
        }
    }
}
