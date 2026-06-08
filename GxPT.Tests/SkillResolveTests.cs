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

        // Rung 1: this skill, here -- beats everything (even the feature being off both ways).
        [Fact]
        public void SkillHere_BeatsEverything()
        {
            SkillEnablement g = new SkillEnablement();
            g.FeatureOff = true;
            g.SetSkillOverride("x", false);
            Assert.True(SkillResolve.IsEnabled(g, "x", true, true));    // convSkill on wins
            Assert.False(SkillResolve.IsEnabled(g, "x", false, false)); // convSkill off wins
        }

        // Rung 2: this skill, global -- beats the feature-level rules (rung 3/4).
        [Fact]
        public void SkillGlobal_BeatsFeature()
        {
            SkillEnablement g = new SkillEnablement();
            g.SetSkillOverride("x", false);                 // x off globally
            Assert.False(SkillResolve.IsEnabled(g, "x", null, false)); // beats "all skills on here"

            g.SetSkillOverride("y", true);                  // y on globally
            g.FeatureOff = true;                            // all skills off globally
            Assert.True(SkillResolve.IsEnabled(g, "y", null, null));   // rung 2 on beats rung 4 off
        }

        // Rung 3 beats rung 4: this conversation's feature setting beats the global feature default.
        [Fact]
        public void FeatureHere_BeatsFeatureGlobal()
        {
            SkillEnablement g = new SkillEnablement();
            g.FeatureOff = true;                            // off globally
            Assert.True(SkillResolve.IsEnabled(g, "z", null, false));  // on here wins
        }

        [Fact]
        public void Default_IsOn()
        {
            Assert.True(SkillResolve.IsEnabled(new SkillEnablement(), "z", null, null));
            Assert.True(SkillResolve.IsEnabled(null, "z", null, null));
        }

        [Fact]
        public void Resolve_ReportsDecidingRule()
        {
            SkillEnablement g = new SkillEnablement();
            SkillRule rule;

            SkillResolve.Resolve(g, "x", true, null, out rule);
            Assert.Equal(SkillRule.SkillHere, rule);

            g.SetSkillOverride("x", false);
            SkillResolve.Resolve(g, "x", null, null, out rule);
            Assert.Equal(SkillRule.SkillGlobal, rule);

            SkillResolve.Resolve(new SkillEnablement(), "y", null, true, out rule);
            Assert.Equal(SkillRule.FeatureHere, rule);

            SkillEnablement off = new SkillEnablement(); off.FeatureOff = true;
            SkillResolve.Resolve(off, "y", null, null, out rule);
            Assert.Equal(SkillRule.FeatureGlobal, rule);

            SkillResolve.Resolve(new SkillEnablement(), "y", null, null, out rule);
            Assert.Equal(SkillRule.Default, rule);
        }

        // The transcript fix: feature off "here", but a per-skill "on here" keeps that one skill enabled.
        [Fact]
        public void EnabledSkills_PerSkillOnHere_SurvivesFeatureOffHere()
        {
            WriteSkill("a", "A.");
            WriteSkill("b", "B.");
            WriteSkill("c", "C.");
            SkillCatalog cat = SkillCatalog.Build(_root, null);

            Dictionary<string, bool> conv = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            conv["a"] = true;   // forced on for this conversation

            List<Skill> r = SkillResolve.EnabledSkills(cat.Skills, new SkillEnablement(), true /*feature off here*/, conv);

            Assert.Single(r);
            Assert.Equal("a", r[0].Slug);
        }
    }
}
