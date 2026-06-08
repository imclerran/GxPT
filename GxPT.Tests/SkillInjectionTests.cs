using System;
using System.IO;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillInjectionTests : IDisposable
    {
        private readonly string _root;

        public SkillInjectionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillinj_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void BundledRoot_AppendsSkillsDir()
        {
            Assert.Equal(Path.Combine("base", "skills"), SkillInjection.BundledRoot("base"));
            Assert.Null(SkillInjection.BundledRoot(null));
        }

        [Fact]
        public void ProjectRoot_IsUnderDotGxpt_OrNull()
        {
            string expected = Path.Combine(Path.Combine("work", ".gxpt"), "skills");
            Assert.Equal(expected, SkillInjection.ProjectRoot("work"));
            Assert.Null(SkillInjection.ProjectRoot(null));
        }

        [Fact]
        public void BuildManifestMessage_EmptyCatalog_ReturnsNull()
        {
            SkillCatalog empty = SkillCatalog.Build(_root, null);   // _root has no skill folders
            Assert.Null(SkillInjection.BuildManifestMessage(empty));
            Assert.Null(SkillInjection.BuildManifestMessage(null));
        }

        [Fact]
        public void BuildManifestMessage_NonEmpty_FramesAndListsSkills()
        {
            string dir = Path.Combine(_root, "release-notes");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\nname: Release Notes\ndescription: Draft notes.\n---\nbody\n", new UTF8Encoding(false));

            SkillCatalog cat = SkillCatalog.Build(_root, null);
            string msg = SkillInjection.BuildManifestMessage(cat);

            Assert.NotNull(msg);
            Assert.Contains("# Skills", msg);
            Assert.Contains("open_skill", msg);
            Assert.Contains("release-notes", msg);
            Assert.Contains("Draft notes.", msg);
        }

        [Fact]
        public void BuildCatalog_DiscoversProjectSkillsUnderDotGxpt()
        {
            string skillDir = Path.Combine(Path.Combine(Path.Combine(_root, ".gxpt"), "skills"), "proj-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                "---\ndescription: A project skill.\n---\nbody\n", new UTF8Encoding(false));

            SkillCatalog cat = SkillInjection.BuildCatalog("no-such-exe-dir", _root);

            Skill s;
            Assert.True(cat.TryGet("proj-skill", out s));
            Assert.Equal(SkillSource.Project, s.Source);
        }
    }
}
