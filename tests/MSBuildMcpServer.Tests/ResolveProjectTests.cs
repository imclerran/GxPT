using System.IO;
using MSBuildMcpServer;
using Xunit;

namespace MSBuildMcpServer.Tests
{
    /// <summary>
    /// Tests the project/solution resolution: an explicit path is honored (and validated), and when
    /// none is given a lone .sln (or lone project file) in the working directory is auto-selected.
    /// </summary>
    public class ResolveProjectTests
    {
        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gxpt-msbuild-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Missing_explicit_project_is_an_error()
        {
            string dir = NewTempDir();
            try
            {
                string full, rel, err;
                bool ok = MsBuildTools.ResolveProject(dir, "nope.csproj", out full, out rel, out err);
                Assert.False(ok);
                Assert.Contains("not found", err);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void A_lone_solution_is_auto_selected()
        {
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "App.sln"), "");
                string full, rel, err;
                bool ok = MsBuildTools.ResolveProject(dir, null, out full, out rel, out err);
                Assert.True(ok, err);
                Assert.Equal("App.sln", rel);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void A_lone_project_is_auto_selected_when_no_solution()
        {
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "App.csproj"), "");
                string full, rel, err;
                bool ok = MsBuildTools.ResolveProject(dir, null, out full, out rel, out err);
                Assert.True(ok, err);
                Assert.Equal("App.csproj", rel);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Ambiguous_solutions_require_an_explicit_project()
        {
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "One.sln"), "");
                File.WriteAllText(Path.Combine(dir, "Two.sln"), "");
                string full, rel, err;
                bool ok = MsBuildTools.ResolveProject(dir, null, out full, out rel, out err);
                Assert.False(ok);
                Assert.Contains("specify 'project'", err);
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
