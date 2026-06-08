using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GxPT;
using GxPT.Tests.Commands;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillCommandsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _work;
        private readonly FakeSlashCommandContext _ctx;

        public SkillCommandsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillcmd_" + Guid.NewGuid().ToString("N"));
            _work = Path.Combine(_root, "work");
            Directory.CreateDirectory(_work);
            SkillEnablement.FilePathOverride = Path.Combine(_root, "skills.json");
            _ctx = new FakeSlashCommandContext(_work);
        }

        public void Dispose()
        {
            SkillEnablement.FilePathOverride = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        // Creates a project skill under <work>/.gxpt/skills/<slug>/SKILL.md.
        private void WriteSkill(string slug, string description, string body)
        {
            string dir = Path.Combine(Path.Combine(Path.Combine(_work, ".gxpt"), "skills"), slug);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\ndescription: " + description + "\n---\n\n" + body + "\n", new UTF8Encoding(false));
        }

        private string LastInfo()
        {
            return _ctx.Infos.Count > 0 ? _ctx.Infos[_ctx.Infos.Count - 1] : null;
        }

        [Fact]
        public void Skills_NoArgs_ListsSkills()
        {
            WriteSkill("alpha", "First.", "b");
            WriteSkill("beta", "Second.", "b");

            SlashCommandResult r = new SkillsCommand().Invoke("", _ctx);

            Assert.False(r.SendToModel);
            Assert.Null(r.Error);
            Assert.Contains("alpha", LastInfo());
            Assert.Contains("beta", LastInfo());
        }

        [Fact]
        public void Skill_OffHere_SetsConversationOverride()
        {
            WriteSkill("greeting", "Be a pirate.", "b");

            SlashCommandResult r = new SkillCommand().Invoke("greeting off", _ctx);

            Assert.False(r.SendToModel);
            Assert.False(_ctx.ConvOverrides["greeting"]);   // forced off for this conversation
        }

        [Fact]
        public void Skill_BareSlug_TogglesConversation()
        {
            WriteSkill("greeting", "Be a pirate.", "b");

            new SkillCommand().Invoke("greeting", _ctx);     // on by default -> toggles off
            Assert.False(_ctx.ConvOverrides["greeting"]);

            new SkillCommand().Invoke("greeting", _ctx);     // now off -> toggles on
            Assert.True(_ctx.ConvOverrides["greeting"]);
        }

        [Fact]
        public void Skill_OffGlobal_PersistsToSkillsJson()
        {
            WriteSkill("greeting", "Be a pirate.", "b");

            new SkillCommand().Invoke("greeting off global", _ctx);

            Assert.True(SkillEnablement.LoadGlobal().IsDisabled("greeting"));
            Assert.Empty(_ctx.ConvOverrides);   // global scope must not touch the conversation
        }

        [Fact]
        public void Skill_UnknownSlug_Fails()
        {
            SlashCommandResult r = new SkillCommand().Invoke("nope off", _ctx);
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void Skills_OffGlobal_ThenReset()
        {
            new SkillsCommand().Invoke("off global", _ctx);
            Assert.True(SkillEnablement.LoadGlobal().FeatureOff);

            new SkillsCommand().Invoke("reset global", _ctx);
            Assert.False(SkillEnablement.LoadGlobal().FeatureOff);
        }

        [Fact]
        public void Skills_OffHere_AndReset()
        {
            new SkillsCommand().Invoke("off", _ctx);
            Assert.Equal(true, _ctx.ConvFeatureOff);

            new SkillsCommand().Invoke("reset", _ctx);
            Assert.Null(_ctx.ConvFeatureOff);
        }

        [Fact]
        public void Skills_UnknownScope_Fails()
        {
            SlashCommandResult r = new SkillsCommand().Invoke("off nowhere", _ctx);
            Assert.NotNull(r.Error);
        }

        [Fact]
        public void Use_KnownSkill_SendsBodyAndText()
        {
            WriteSkill("greeting", "Be a pirate.", "ARRR MATEY");

            SlashCommandResult r = new UseCommand().Invoke("greeting say hi", _ctx);

            Assert.True(r.SendToModel);
            Assert.Contains("# Skill: greeting", r.TextToSend);
            Assert.Contains("ARRR MATEY", r.TextToSend);
            Assert.Contains("say hi", r.TextToSend);
        }

        [Fact]
        public void Use_DisabledSkill_StillWorks()
        {
            WriteSkill("greeting", "Be a pirate.", "ARRR");
            new SkillCommand().Invoke("greeting off global", _ctx);   // disable globally

            SlashCommandResult r = new UseCommand().Invoke("greeting", _ctx);

            Assert.True(r.SendToModel);   // explicit /use ignores enablement
            Assert.Contains("ARRR", r.TextToSend);
        }

        [Fact]
        public void Use_UnknownSkill_Fails()
        {
            SlashCommandResult r = new UseCommand().Invoke("nope", _ctx);
            Assert.NotNull(r.Error);
            Assert.False(r.SendToModel);
        }
    }
}
