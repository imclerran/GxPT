using System;
using System.IO;
using System.Text;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillToolsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _bundled;

        public SkillToolsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skilltools_" + Guid.NewGuid().ToString("N"));
            _bundled = Path.Combine(_root, "bundled");
            Directory.CreateDirectory(_bundled);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        private void WriteSkill(string folder, string description, string body)
        {
            string dir = Path.Combine(_bundled, folder);
            Directory.CreateDirectory(dir);
            string text = "---\nname: " + folder + "\ndescription: " + description + "\n---\n\n" + body + "\n";
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), text, new UTF8Encoding(false));
        }

        private void WriteAsset(string folder, string relpath, string content)
        {
            string full = Path.Combine(Path.Combine(_bundled, folder), relpath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }

        private SkillTools Tools()
        {
            return new SkillTools(SkillCatalog.Build(_bundled, null).Skills);
        }

        [Fact]
        public void OpenSkillDef_HasExpectedShape()
        {
            WriteSkill("release-notes", "Draft notes.", "body");

            JObject def = Tools().OpenSkillDef();

            Assert.Equal("function", (string)def["type"]);
            JObject fn = (JObject)def["function"];
            Assert.Equal(SkillTools.OpenSkillName, (string)fn["name"]);
            JObject pars = (JObject)fn["parameters"];
            Assert.Equal("object", (string)pars["type"]);
            JObject names = (JObject)pars["properties"]["names"];
            Assert.Equal("array", (string)names["type"]);
            Assert.Equal("string", (string)names["items"]["type"]);
            Assert.Contains("names", ((JArray)pars["required"]).ToObject<string[]>());
        }

        [Fact]
        public void IsOpenSkill_MatchesOnlyTheName()
        {
            SkillTools t = Tools();
            Assert.True(t.IsOpenSkill("open_skill"));
            Assert.False(t.IsOpenSkill("reveal_tools"));
            Assert.False(t.IsOpenSkill(null));
        }

        [Fact]
        public void HasSkills_ReflectsCatalog()
        {
            Assert.False(new SkillTools(SkillCatalog.Build(_bundled, null).Skills).HasSkills);
            WriteSkill("a", "A.", "body");
            Assert.True(new SkillTools(SkillCatalog.Build(_bundled, null).Skills).HasSkills);
        }

        [Fact]
        public void Open_KnownSkill_ReturnsBodyDirAndAssets()
        {
            WriteSkill("release-notes", "Draft notes.", "STEP ONE\nSTEP TWO");
            WriteAsset("release-notes", Path.Combine("scripts", "gen.bat"), "@echo off");

            string result = Tools().Open(new string[] { "release-notes" });

            Assert.Contains("# Skill: release-notes", result);
            Assert.Contains("STEP ONE", result);
            Assert.Contains("Skill files are at:", result);
            Assert.Contains("scripts/gen.bat", result);   // forward-slashed, SKILL.md excluded
            Assert.DoesNotContain("SKILL.md", result);
        }

        [Fact]
        public void Open_UnknownSkill_ReturnsNote_AndKeepsOthers()
        {
            WriteSkill("known", "Known.", "the body");

            string result = Tools().Open(new string[] { "nope", "known" });

            Assert.Contains("Unknown skill: nope", result);
            Assert.Contains("# Skill: known", result);
            Assert.Contains("the body", result);
        }

        [Fact]
        public void Open_NullOrEmpty_ReturnsNotice()
        {
            SkillTools t = Tools();
            Assert.Equal("No skill names provided.", t.Open(null));
            Assert.Equal("No skill names provided.", t.Open(new string[0]));
        }

        // ---- read_skill_file ----

        [Fact]
        public void ReadSkillFileDef_HasExpectedShape()
        {
            WriteSkill("release-notes", "Draft notes.", "body");

            JObject def = Tools().ReadSkillFileDef();
            JObject fn = (JObject)def["function"];
            Assert.Equal(SkillTools.ReadSkillFileName, (string)fn["name"]);
            JObject pars = (JObject)fn["parameters"];
            Assert.Equal("string", (string)pars["properties"]["slug"]["type"]);
            Assert.Equal("string", (string)pars["properties"]["relpath"]["type"]);
            string[] req = ((JArray)pars["required"]).ToObject<string[]>();
            Assert.Contains("slug", req);
            Assert.Contains("relpath", req);
        }

        [Fact]
        public void IsReadSkillFile_MatchesOnlyTheName()
        {
            SkillTools t = Tools();
            Assert.True(t.IsReadSkillFile("read_skill_file"));
            Assert.False(t.IsReadSkillFile("open_skill"));
            Assert.False(t.IsReadSkillFile(null));
        }

        [Fact]
        public void ReadFile_KnownSkillNestedAsset_ReturnsContents()
        {
            WriteSkill("release-notes", "Draft notes.", "body");
            WriteAsset("release-notes", Path.Combine("scripts", "gen.bat"), "@echo off\necho hi");

            string r = Tools().ReadFile("release-notes", "scripts/gen.bat");

            Assert.Contains("@echo off", r);
            Assert.Contains("echo hi", r);
        }

        [Fact]
        public void ReadFile_UnknownSkill_ReturnsNote()
        {
            WriteSkill("release-notes", "Draft notes.", "body");
            Assert.StartsWith("Unknown skill:", Tools().ReadFile("nope", "a.txt"));
        }

        [Fact]
        public void ReadFile_MissingFile_ReturnsNotFound()
        {
            WriteSkill("release-notes", "Draft notes.", "body");
            Assert.StartsWith("File not found:", Tools().ReadFile("release-notes", "nope.txt"));
        }

        [Fact]
        public void ReadFile_EscapeOrAbsolute_IsRejected()
        {
            WriteSkill("release-notes", "Draft notes.", "body");
            WriteAsset("release-notes", "ok.txt", "fine");
            // A sibling file outside the skill folder that '..' would reach.
            File.WriteAllText(Path.Combine(_bundled, "secret.txt"), "nope", new UTF8Encoding(false));

            SkillTools t = Tools();
            Assert.StartsWith("Invalid path:", t.ReadFile("release-notes", "../secret.txt"));
            Assert.StartsWith("Invalid path:", t.ReadFile("release-notes", Path.Combine(_bundled, "secret.txt")));
            Assert.StartsWith("Invalid path:", t.ReadFile("release-notes", "scripts/../../secret.txt"));
            Assert.Contains("fine", t.ReadFile("release-notes", "ok.txt")); // the in-bounds file still works
        }
    }
}
