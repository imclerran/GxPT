using System.Collections.Generic;
using MSBuildMcpServer;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MSBuildMcpServer.Tests
{
    /// <summary>
    /// Tests devenv.com command-line construction:
    ///   &lt;solution&gt; /Build|/Rebuild|/Clean|/Deploy "Config[|Platform]" [/Project p [/ProjectConfig pc]]
    /// devenv requires a solution-configuration name, so one is always emitted (default Release).
    /// </summary>
    public class DevenvArgsTests
    {
        private static List<string> Build(string json)
        {
            return MsBuildTools.BuildDevenvArgs(JObject.Parse(json), "App.sln");
        }

        [Fact]
        public void Defaults_are_build_release()
        {
            Assert.Equal(new[] { "App.sln", "/Build", "Release" }, Build("{}").ToArray());
        }

        [Fact]
        public void Action_maps_to_the_right_switch()
        {
            Assert.Contains("/Rebuild", Build("{\"action\":\"Rebuild\"}"));
            Assert.Contains("/Clean", Build("{\"action\":\"Clean\"}"));
            Assert.Contains("/Deploy", Build("{\"action\":\"Deploy\"}"));
        }

        [Fact]
        public void Configuration_and_platform_combine_with_a_pipe()
        {
            Assert.Equal(new[] { "App.sln", "/Build", "Debug|x86" },
                Build("{\"configuration\":\"Debug\",\"platform\":\"x86\"}").ToArray());
        }

        [Fact]
        public void Single_project_adds_project_and_project_config_switches()
        {
            List<string> a = Build("{\"project\":\"Setup\",\"project_config\":\"Release\"}");
            int i = a.IndexOf("/Project");
            Assert.True(i >= 0);
            Assert.Equal("Setup", a[i + 1]);
            int j = a.IndexOf("/ProjectConfig");
            Assert.True(j >= 0);
            Assert.Equal("Release", a[j + 1]);
        }
    }
}
