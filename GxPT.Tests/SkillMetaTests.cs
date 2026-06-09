using System.Collections.Generic;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillMetaTests
    {
        private static Skill MakeSkill(string slug)
        {
            return new Skill(slug, slug, "desc", "/dir/" + slug, "/dir/" + slug + "/SKILL.md", SkillSource.Project);
        }

        [Fact]
        public void HiddenTools_MetaSkillDisabled_HidesAuthoringTools()
        {
            ICollection<string> hidden = SkillMeta.HiddenTools(new List<Skill> { MakeSkill("greeting") });
            Assert.Contains("skills__create_skill", hidden);
            Assert.Contains("skills__write_skill_file", hidden);
            Assert.Contains("skills__update_skill", hidden);
        }

        [Fact]
        public void HiddenTools_MetaSkillEnabled_HidesNothing()
        {
            var enabled = new List<Skill> { MakeSkill("greeting"), MakeSkill(SkillMeta.SkillWriterSlug) };
            Assert.Empty(SkillMeta.HiddenTools(enabled));
        }

        [Fact]
        public void HiddenTools_NullOrEmpty_HidesAuthoringTools()
        {
            Assert.NotEmpty(SkillMeta.HiddenTools(null));
            Assert.NotEmpty(SkillMeta.HiddenTools(new List<Skill>()));
        }
    }

    // The orchestrator-side filtering that applies the hidden set to the exposed defs + names manifest.
    public sealed class OrchestratorHiddenToolsTests
    {
        private static JObject Def(string name)
        {
            JObject fn = new JObject(); fn["name"] = name;
            JObject d = new JObject(); d["type"] = "function"; d["function"] = fn;
            return d;
        }

        [Fact]
        public void FilterHiddenDefs_RemovesHiddenByFunctionName()
        {
            var defs = new List<JObject> { Def("web__search"), Def("skills__create_skill") };
            var hidden = new HashSet<string> { "skills__create_skill" };

            IList<JObject> result = McpChatOrchestrator.FilterHiddenDefs(defs, hidden);

            Assert.Single(result);
            Assert.Equal("web__search", (string)result[0]["function"]["name"]);
        }

        [Fact]
        public void FilterHiddenDefs_NoHidden_ReturnsUnchanged()
        {
            var defs = new List<JObject> { Def("web__search") };
            Assert.Same(defs, McpChatOrchestrator.FilterHiddenDefs(defs, new HashSet<string>()));
        }

        [Fact]
        public void FilterHiddenManifest_DropsHiddenLinesKeepsOthers()
        {
            string manifest = "Available tools:\n- web__search\n- skills__create_skill\n- files__read";
            var hidden = new HashSet<string> { "skills__create_skill" };

            string result = McpChatOrchestrator.FilterHiddenManifest(manifest, hidden);

            Assert.DoesNotContain("skills__create_skill", result);
            Assert.Contains("- web__search", result);
            Assert.Contains("- files__read", result);
        }
    }
}
