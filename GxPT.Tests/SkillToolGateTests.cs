using System.Collections.Generic;
using GxPT;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillToolGateTests
    {
        private static Skill MakeSkill(string slug)
        {
            return new Skill(slug, slug, "desc", "/dir/" + slug, "/dir/" + slug + "/SKILL.md", SkillSource.Project);
        }

        [Fact]
        public void HiddenTools_SkillEnabledButNotWriter_HidesAuthoring_ShowsRunScript()
        {
            // A non-writer skill is enabled: the 8 authoring tools are hidden, but run_skill_script is
            // available (any skill may ship a script).
            ICollection<string> hidden = SkillToolGate.HiddenTools(new List<Skill> { MakeSkill("greeting") });
            // tier 1
            Assert.Contains("skills__create_skill", hidden);
            Assert.Contains("skills__write_skill_file", hidden);
            Assert.Contains("skills__update_skill", hidden);
            // tier 2 (maintenance)
            Assert.Contains("skills__edit_skill_file", hidden);
            Assert.Contains("skills__list_skill_files", hidden);
            Assert.Contains("skills__delete_skill_file", hidden);
            Assert.Contains("skills__delete_skill", hidden);
            Assert.Contains("skills__validate_skill", hidden);
            // execution stays visible
            Assert.DoesNotContain("skills__run_skill_script", hidden);
        }

        [Fact]
        public void HiddenTools_MetaSkillEnabled_HidesNothing()
        {
            var enabled = new List<Skill> { MakeSkill("greeting"), MakeSkill(SkillToolGate.SkillWriterSlug) };
            Assert.Empty(SkillToolGate.HiddenTools(enabled));
        }

        [Fact]
        public void HiddenTools_NoSkillEnabled_HidesEverySkillsTool()
        {
            // No skill enabled at all: authoring AND run_skill_script are hidden.
            foreach (ICollection<string> hidden in new[] { SkillToolGate.HiddenTools(null), SkillToolGate.HiddenTools(new List<Skill>()) })
            {
                Assert.Contains("skills__create_skill", hidden);
                Assert.Contains("skills__validate_skill", hidden);
                Assert.Contains("skills__run_skill_script", hidden);
            }
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
