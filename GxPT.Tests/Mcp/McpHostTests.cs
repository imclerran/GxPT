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
        public void EnsureWorkingDir_opens_scoped_servers_with_the_workdir()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true), Specs.Scoped("git", true) });
            Assert.Empty(c.CreatedNames); // nothing opened until a workdir is ensured

            host.EnsureWorkingDir("C:\\proj");

            Assert.Equal(new[] { "files", "git" }, c.CreatedNames.ToArray());
            Assert.True(c.Workdirs.All(w => w == "C:\\proj"));
            var m = Manifest(reg);
            Assert.Contains("files__files_tool", m);
            Assert.Contains("git__git_tool", m);
            Assert.Contains("C:\\proj", host.ActiveWorkingDirs);
        }

        [Fact]
        public void EnsureWorkingDir_is_idempotent_for_the_same_folder()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });

            host.EnsureWorkingDir("C:\\a");
            host.EnsureWorkingDir("C:\\a"); // second call must NOT spawn another set

            Assert.Equal(new[] { "files" }, c.CreatedNames.ToArray());
        }

        [Fact]
        public void EnsureWorkingDir_before_Start_still_launches_scoped_servers()
        {
            // Reproduces the startup race: the working folder is applied before Start() has captured
            // the scoped specs. Start() must honor the already-requested workdir and launch them.
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);

            host.EnsureWorkingDir("C:\\proj");           // arrives first; no specs yet
            Assert.Empty(c.CreatedNames);

            host.Start(new[] { Specs.Scoped("command", true), Specs.Eager("web", true) });

            Assert.Contains("command", c.CreatedNames);  // scoped server launched by Start
            Assert.Contains("command__command_tool", Manifest(reg));
            Assert.Contains("C:\\proj", host.ActiveWorkingDirs);
        }

        [Fact]
        public void Different_workdirs_get_independent_scoped_sets_and_route_per_folder()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });

            host.EnsureWorkingDir("C:\\a");
            var connA = c.Created.Last();
            host.EnsureWorkingDir("C:\\b");
            var connB = c.Created.Last();

            // Both folders served by their own process; neither torn down by the other.
            Assert.Equal(new[] { "files", "files" }, c.CreatedNames.ToArray());
            Assert.Equal(new[] { "C:\\a", "C:\\b" }, c.Workdirs.ToArray());
            Assert.NotSame(connA, connB);
            Assert.Equal(ConnectionState.Ready, connA.State); // NOT closed by ensuring "C:\b"
            Assert.Equal(ConnectionState.Ready, connB.State);

            // The same tool name routes to the connection bound to the calling folder.
            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("files__files_tool", "C:\\a", out r, out tool));
            Assert.Same(connA, r);
            Assert.True(reg.TryResolve("files__files_tool", "C:\\b", out r, out tool));
            Assert.Same(connB, r);
        }

        [Fact]
        public void ReleaseWorkingDir_tears_down_only_that_folder()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });
            host.EnsureWorkingDir("C:\\a");
            var connA = c.Created.Last();
            host.EnsureWorkingDir("C:\\b");
            var connB = c.Created.Last();

            host.ReleaseWorkingDir("C:\\a");

            Assert.Equal(ConnectionState.Closed, connA.State);
            Assert.Equal(ConnectionState.Ready, connB.State);
            Assert.DoesNotContain("C:\\a", host.ActiveWorkingDirs);
            Assert.Contains("C:\\b", host.ActiveWorkingDirs);
            Assert.Contains("files__files_tool", Manifest(reg)); // still provided by "C:\b"
        }

        [Fact]
        public void RetainOnly_tears_down_folders_no_longer_in_use()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", true) });
            host.EnsureWorkingDir("C:\\a");
            var connA = c.Created.Last();
            host.EnsureWorkingDir("C:\\b");
            var connB = c.Created.Last();

            host.RetainOnly(new[] { "C:\\b" }); // only the folder with an open tab survives

            Assert.Equal(ConnectionState.Closed, connA.State);
            Assert.Equal(ConnectionState.Ready, connB.State);
            Assert.Equal(new[] { "C:\\b" }, host.ActiveWorkingDirs);
        }

        [Fact]
        public void Disabled_scoped_spec_is_not_opened()
        {
            FakeServerConnector c; McpToolRegistry reg;
            var host = NewHost(out c, out reg);
            host.Start(new[] { Specs.Scoped("files", false) });

            host.EnsureWorkingDir("C:\\a");

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
            host.EnsureWorkingDir("C:\\a");
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
