using System;
using System.IO;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Serialized with the other class that mutates the static SkillEnablement.FilePathOverride, so the
    // shared global state can't race under xUnit's parallel-by-collection execution.
    [Collection("SkillsGlobalState")]
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
            Assert.Null(e.GetSkillOverride("anything"));   // unset = inherit
        }

        [Fact]
        public void SaveThenLoad_RoundTripsFeatureOffAndSkillOverrides()
        {
            SkillEnablement e = SkillEnablement.LoadGlobal();
            e.FeatureOff = true;
            e.SetSkillOverride("noisy-skill", false);    // force off globally
            e.SetSkillOverride("always-pirate", true);   // force on globally
            e.SaveGlobal();

            Assert.True(File.Exists(_file));

            SkillEnablement loaded = SkillEnablement.LoadGlobal();
            Assert.True(loaded.FeatureOff);
            Assert.Equal((bool?)false, loaded.GetSkillOverride("noisy-skill"));
            Assert.Equal((bool?)true, loaded.GetSkillOverride("always-pirate"));
            Assert.Null(loaded.GetSkillOverride("kept"));
        }

        [Fact]
        public void SetSkillOverride_Null_ClearsAndIsCaseInsensitive()
        {
            SkillEnablement e = SkillEnablement.LoadGlobal();
            e.SetSkillOverride("Foo-Bar", false);
            Assert.Equal((bool?)false, e.GetSkillOverride("foo-bar"));   // case-insensitive

            e.SetSkillOverride("foo-bar", null);
            Assert.Null(e.GetSkillOverride("Foo-Bar"));
        }

        [Fact]
        public void Load_BackwardCompat_DisabledArray_ReadsAsForceOff()
        {
            File.WriteAllText(_file, "{\"disabled\":[\"old-skill\"]}", new UTF8Encoding(false));

            SkillEnablement loaded = SkillEnablement.LoadGlobal();
            Assert.Equal((bool?)false, loaded.GetSkillOverride("old-skill"));
        }
    }
}
