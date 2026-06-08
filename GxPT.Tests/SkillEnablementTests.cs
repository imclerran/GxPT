using System;
using System.Collections.Generic;
using System.IO;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillEnablementTests : IDisposable
    {
        private readonly string _root;
        private readonly string _file;

        public SkillEnablementTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillen_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _file = Path.Combine(_root, "skills.json");
            SkillEnablement.FilePathOverride = _file;
        }

        public void Dispose()
        {
            SkillEnablement.FilePathOverride = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void LoadGlobal_MissingFile_DefaultsToAllOn()
        {
            SkillEnablement e = SkillEnablement.LoadGlobal();
            Assert.False(e.FeatureOff);
            Assert.False(e.IsDisabled("anything"));
            Assert.Empty(e.DisabledSlugs());
        }

        [Fact]
        public void SaveThenLoad_RoundTripsFeatureOffAndDisabled()
        {
            SkillEnablement e = SkillEnablement.LoadGlobal();
            e.FeatureOff = true;
            e.SetDisabled("noisy-skill", true);
            e.SetDisabled("other", true);
            e.SaveGlobal();

            Assert.True(File.Exists(_file));

            SkillEnablement loaded = SkillEnablement.LoadGlobal();
            Assert.True(loaded.FeatureOff);
            Assert.True(loaded.IsDisabled("noisy-skill"));
            Assert.True(loaded.IsDisabled("other"));
            Assert.False(loaded.IsDisabled("kept"));
        }

        [Fact]
        public void SetDisabled_False_RemovesAndIsCaseInsensitive()
        {
            SkillEnablement e = SkillEnablement.LoadGlobal();
            e.SetDisabled("Foo-Bar", true);
            Assert.True(e.IsDisabled("foo-bar"));   // case-insensitive

            e.SetDisabled("foo-bar", false);
            Assert.False(e.IsDisabled("Foo-Bar"));
            Assert.Empty(e.DisabledSlugs());
        }
    }
}
