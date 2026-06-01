using System.Collections.Generic;
using System.Linq;
using GxPT;
using Mcp35.Client;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class McpHostTests
    {
        private static List<string> Manifest(McpToolRegistry reg)
        {
            var names = new List<string>();
            foreach (var line in reg.NamesManifestSystemMessage().Split('\n'))
                if (line.StartsWith("- ")) names.Add(line.Substring(2));
            return names;
        }

        private static McpHost NewHost(out FakeServerConnector connector, out McpToolRegistry reg)
        {
            connector = new FakeServerConnector();
            reg = new McpToolRegistry(24, null);
            return new McpHost(connector, reg, null, 2000);
        }

        [Fact]
        public void Start_opens_workdir_independent_servers_and_defers_scoped()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);

            host.Start(new[] { Specs.Eager("web", true), Specs.Eager("github", true), Specs.Scoped("files", true) });

            Assert.Equal(new[] { "web", "github" }, c.CreatedNames.ToArray()); // files deferred
            var m = Manifest(reg);
            Assert.Contains("web__web_tool", m);
            Assert.Contains("github__github_tool", m);
            Assert.DoesNotContain("files__files_tool", m);
        }

        [Fact]
        public void Start_skips_disabled_servers()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);

            host.Start(new[] { Specs.Eager("web", false), Specs.Eager("github", true) });

            Assert.Equal(new[] { "github" }, c.CreatedNames.ToArray());
            Assert.DoesNotContain("web__web_tool", Manifest(reg));
        }

        [Fact]
        public void SetActiveWorkingDir_opens_scoped_servers_with_the_workdir()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true), Specs.Scoped("git", true) });
            Assert.Empty(c.CreatedNames); // nothing opened until a workdir is active

            host.SetActiveWorkingDir("C:\\proj");

            Assert.Equal(new[] { "files", "git" }, c.CreatedNames.ToArray());
            Assert.True(c.Workdirs.All(w => w == "C:\\proj"));
            var m = Manifest(reg);
            Assert.Contains("files__files_tool", m);
            Assert.Contains("git__git_tool", m);
            Assert.Equal("C:\\proj", host.ActiveWorkingDir);
        }

        [Fact]
        public void SetActiveWorkingDir_before_Start_still_launches_scoped_servers()
        {
            // Reproduces the startup race: the working folder is applied before Start() has captured
            // the scoped specs. Start() must honor the already-set workdir and launch them.
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);

            host.SetActiveWorkingDir("C:\\proj");        // arrives first; no specs yet
            Assert.Empty(c.CreatedNames);

            host.Start(new[] { Specs.Scoped("command", true), Specs.Eager("web", true) });

            Assert.Contains("command", c.CreatedNames);  // scoped server launched by Start
            Assert.Contains("command__command_tool", Manifest(reg));
            Assert.Equal("C:\\proj", host.ActiveWorkingDir);
        }

        [Fact]
        public void Switching_workdir_tears_down_old_scoped_and_opens_new()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });

            host.SetActiveWorkingDir("C:\\a");
            var firstConn = c.Created.Last();
            host.SetActiveWorkingDir("C:\\b");

            Assert.Equal(new[] { "files", "files" }, c.CreatedNames.ToArray());
            Assert.Equal(new[] { "C:\\a", "C:\\b" }, c.Workdirs.ToArray());
            Assert.Equal(ConnectionState.Closed, firstConn.State); // old one disposed
            Assert.Contains("files__files_tool", Manifest(reg)); // new one present
        }

        [Fact]
        public void SetActiveWorkingDir_null_tears_down_scoped()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });

            host.SetActiveWorkingDir("C:\\a");
            Assert.Contains("files__files_tool", Manifest(reg));

            host.SetActiveWorkingDir(null);
            Assert.Empty(Manifest(reg));
            Assert.Null(host.ActiveWorkingDir);
        }

        [Fact]
        public void Disabled_scoped_spec_is_not_opened()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", false) });

            host.SetActiveWorkingDir("C:\\a");

            Assert.Empty(c.CreatedNames);
            Assert.Empty(Manifest(reg));
        }

        [Fact]
        public void A_connection_closing_removes_its_tools_from_the_registry()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Eager("web", true) });
            Assert.Contains("web__web_tool", Manifest(reg));

            c.Created[0].Dispose(); // simulates a fault/close → StateChanged(Closed)

            Assert.DoesNotContain("web__web_tool", Manifest(reg));
        }

        [Fact]
        public void Dispose_closes_all_connections()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Eager("web", true), Specs.Scoped("files", true) });
            host.SetActiveWorkingDir("C:\\a");
            Assert.Equal(2, Manifest(reg).Count);

            host.Dispose();

            Assert.Empty(Manifest(reg));
            Assert.True(c.Created.All(conn => conn.State == ConnectionState.Closed));
        }
    }

    // Compile-coverage + smoke for the live connector against the real Mcp35.Client transports.
    public class DefaultServerConnectorTests
    {
        [Fact]
        public void Creates_http_connection_in_created_state_without_opening()
        {
            var ci = new Mcp35.Core.Protocol.Implementation { Name = "GxPT", Version = "test" };
            var connector = new DefaultServerConnector(ci, "curl", null, null);

            var spec = new McpServerSpec
            {
                Name = "github",
                Kind = McpTransportKind.Http,
                Url = "https://api.githubcopilot.com/mcp/",
                Enabled = true
            };
            spec.Headers["Authorization"] = "Bearer ghp_x";

            var conn = connector.Create(spec, null);
            Assert.NotNull(conn);
            Assert.Equal("github", conn.Name);
            Assert.Equal(ConnectionState.Created, conn.State); // not opened → no network
            conn.Dispose();
        }
    }
}
