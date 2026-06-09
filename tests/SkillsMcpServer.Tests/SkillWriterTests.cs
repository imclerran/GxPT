using System;
using System.IO;
using SkillsMcpServer;
using Xunit;

namespace SkillsMcpServer.Tests
{
    public sealed class SkillWriterTests : IDisposable
    {
        private readonly string _root;        // stand-in workspace root
        private readonly string _project;     // the project skills root the writer targets
        private readonly SkillWriter _writer;

        public SkillWriterTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillwriter_" + Guid.NewGuid().ToString("N"));
            _project = Path.Combine(_root, "skills");
            Directory.CreateDirectory(_project);
            _writer = new SkillWriter(_project, null);   // user-global root not wired
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        private string SkillFile(string slug) { return Path.Combine(Path.Combine(_project, slug), "SKILL.md"); }

        [Fact]
        public void CreateSkill_WritesValidSkillMd()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "Say ahoy.");

            string text = File.ReadAllText(SkillFile("greeting"));
            Assert.Contains("name: Greeting", text);
            Assert.Contains("description: Be a pirate.", text);
            Assert.Contains("Say ahoy.", text);
            // Round-trips through the parser as a loadable skill.
            SkillFrontmatter fm = SkillFrontmatter.Parse(text);
            Assert.Equal("Greeting", fm.Name);
            Assert.Equal("Be a pirate.", fm.Description);
            Assert.Equal("Say ahoy.", fm.Body);
        }

        [Fact]
        public void CreateSkill_NormalizesSlug()
        {
            _writer.CreateSkill(null, "Release Notes", "Release Notes", "Draft notes.", "do it");
            Assert.True(File.Exists(SkillFile("release-notes")));
        }

        [Fact]
        public void CreateSkill_RefusesExisting()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body"));
        }

        [Fact]
        public void CreateSkill_RequiresNameAndDescription()
        {
            Assert.Throws<SkillWriteException>(() => _writer.CreateSkill(null, "a", "", "desc", "body"));
            Assert.Throws<SkillWriteException>(() => _writer.CreateSkill(null, "a", "Name", "  ", "body"));
        }

        [Fact]
        public void WriteFile_WritesSupportingScript_AsCrlf()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            _writer.WriteFile(null, "greeting", "scripts/gen.bat", "@echo off\necho hi");

            string path = Path.Combine(Path.Combine(_project, "greeting"), Path.Combine("scripts", "gen.bat"));
            string text = File.ReadAllText(path);
            Assert.Contains("@echo off", text);
            Assert.Contains("\r\n", text);   // .bat normalized to CRLF
        }

        [Fact]
        public void WriteFile_RejectsSkillMd()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.WriteFile(null, "greeting", "SKILL.md", "whatever"));
        }

        [Fact]
        public void WriteFile_RejectsEscape()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Be a pirate.", "body");
            Assert.Throws<SkillWriteException>(() =>
                _writer.WriteFile(null, "greeting", "../escape.txt", "x"));
        }

        [Fact]
        public void WriteFile_RequiresExistingSkill()
        {
            Assert.Throws<SkillWriteException>(() =>
                _writer.WriteFile(null, "nope", "ref.md", "x"));
        }

        [Fact]
        public void UpdateSkill_PartialFields_KeepsOthers()
        {
            _writer.CreateSkill(null, "greeting", "Greeting", "Old desc.", "Old body.");
            _writer.UpdateSkill(null, "greeting", null, "New desc.", null);   // description only

            SkillFrontmatter fm = SkillFrontmatter.Parse(File.ReadAllText(SkillFile("greeting")));
            Assert.Equal("Greeting", fm.Name);        // unchanged
            Assert.Equal("New desc.", fm.Description); // changed
            Assert.Equal("Old body.", fm.Body);        // unchanged
        }

        [Fact]
        public void UpdateSkill_NonexistentSkill_Throws()
        {
            Assert.Throws<SkillWriteException>(() => _writer.UpdateSkill(null, "nope", "N", "D", "B"));
        }

        [Fact]
        public void Scope_User_NotEnabled_Throws()
        {
            Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill("user", "a", "Name", "Desc", "body"));
        }

        [Fact]
        public void Scope_Project_NoWorkspace_Throws()
        {
            SkillWriter noWs = new SkillWriter(null, null);
            Assert.Throws<SkillWriteException>(() =>
                noWs.CreateSkill(null, "a", "Name", "Desc", "body"));
        }

        [Fact]
        public void Scope_Unknown_Throws()
        {
            Assert.Throws<SkillWriteException>(() =>
                _writer.CreateSkill("elsewhere", "a", "Name", "Desc", "body"));
        }
    }
}
