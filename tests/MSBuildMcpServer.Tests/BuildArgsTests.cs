using System.Collections.Generic;
using MSBuildMcpServer;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MSBuildMcpServer.Tests
{
    /// <summary>
    /// Tests MSBuild command-line construction: the parsed arguments map to the right /t:, /p:, /v:,
    /// and /m: switches, the project leads, and /nologo + a default verbosity are always present.
    /// </summary>
    public class BuildArgsTests
    {
        private static List<string> Build(string json)
        {
            return MsBuildTools.BuildArgs(JObject.Parse(json), "App.sln");
        }

        [Fact]
        public void Defaults_emit_project_minimal_verbosity_and_nologo()
        {
            Assert.Equal(new[] { "App.sln", "/v:minimal", "/nologo" }, Build("{}").ToArray());
        }

        [Fact]
        public void Targets_array_joins_with_semicolons()
        {
            List<string> a = Build("{\"targets\":[\"Clean\",\"Build\"]}");
            Assert.Contains("/t:Clean;Build", a);
        }

        [Fact]
        public void Targets_accepts_a_lone_string()
        {
            List<string> a = Build("{\"targets\":\"Rebuild\"}");
            Assert.Contains("/t:Rebuild", a);
        }

        [Fact]
        public void Configuration_and_platform_become_properties()
        {
            List<string> a = Build("{\"configuration\":\"Release\",\"platform\":\"Any CPU\"}");
            Assert.Contains("/p:Configuration=Release", a);
            Assert.Contains("/p:Platform=Any CPU", a);
        }

        [Fact]
        public void Extra_properties_are_passed_through()
        {
            List<string> a = Build("{\"properties\":{\"DefineConstants\":\"TRACE\",\"WarningLevel\":\"4\"}}");
            Assert.Contains("/p:DefineConstants=TRACE", a);
            Assert.Contains("/p:WarningLevel=4", a);
        }

        [Fact]
        public void Verbosity_overrides_the_default()
        {
            List<string> a = Build("{\"verbosity\":\"detailed\"}");
            Assert.Contains("/v:detailed", a);
            Assert.DoesNotContain("/v:minimal", a);
        }

        [Fact]
        public void Max_cpu_count_emits_parallel_switch()
        {
            Assert.Contains("/m:4", Build("{\"max_cpu_count\":4}"));
        }

        [Fact]
        public void Zero_or_missing_cpu_count_omits_parallel_switch()
        {
            Assert.DoesNotContain("/m:0", Build("{\"max_cpu_count\":0}"));
            foreach (string t in Build("{}")) Assert.False(t.StartsWith("/m"));
        }
    }
}
