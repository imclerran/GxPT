using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillResolveTests : IDisposable
    {
        private readonly string _root;

        public SkillResolveTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillres_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        private void WriteSkill(string folder, string description)
        {
            string dir = Path.Combine(_root, folder);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\ndescription: " + description + "\n---\nbody\n", new UTF8Encoding(false));
        }

        [Fact]
        public void FeatureOn_ConversationOverridesGlobal()
        {
            SkillEnablement global = new SkillEnablement();
            global.FeatureOff = true;

            Assert.False(SkillResolve.FeatureOn(global, null));   // inherit global off
            Assert.True(SkillResolve.FeatureOn(global, false));   // conversation forces on
            Assert.False(SkillResolve.FeatureOn(global, true));   // conversation forces off
            Assert.True(SkillResolve.FeatureOn(null, null));      // default on
        }

        [Fact]
        public void SkillEnabled_ConversationOverridesGlobalDisable()
        {
            SkillEnablement global = new SkillEnablement();
            global.SetDisabled("x", true);

            Assert.False(SkillResolve.SkillEnabled(global, "x", null));  // inherit global disable
            Assert.True(SkillResolve.SkillEnabled(global, "x", true));   // force-on over global-off
            Assert.True(SkillResolve.SkillEnabled(global, "y", null));   // not disabled -> on
            Assert.False(SkillResolve.SkillEnabled(global, "y", false)); // force-off
        }

        [Fact]
        public void EnabledSkills_FeatureOff_ReturnsEmpty()
        {
            WriteSkill("a", "A.");
            WriteSkill("b", "B.");
            SkillCatalog cat = SkillCatalog.Build(_root, null);

            List<Skill> r = SkillResolve.EnabledSkills(cat.Skills, new SkillEnablement(), true, null);
            Assert.Empty(r);
        }

        [Fact]
        public void EnabledSkills_FiltersGlobalDisabled_RespectsConversationForceOn()
        {
            WriteSkill("a", "A.");
            WriteSkill("b", "B.");
            WriteSkill("c", "C.");
            SkillCatalog cat = SkillCatalog.Build(_root, null);

            SkillEnablement global = new SkillEnablement();
            global.SetDisabled("a", true);   // a off by default
            global.SetDisabled("b", true);   // b off by default

            Dictionary<string, bool> overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            overrides["b"] = true;           // but this conversation forces b on
            overrides["c"] = false;          // and forces c off

            List<Skill> r = SkillResolve.EnabledSkills(cat.Skills, global, null, overrides);

            List<string> slugs = new List<string>();
            for (int i = 0; i < r.Count; i++) slugs.Add(r[i].Slug);
            slugs.Sort(StringComparer.Ordinal);

            Assert.Equal(new string[] { "b" }, slugs.ToArray());   // a (global off), c (forced off) excluded
        }
    }
}
