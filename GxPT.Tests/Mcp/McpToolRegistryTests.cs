using System.Collections.Generic;
using GxPT;
using Mcp35.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class McpToolRegistryTests
    {
        private static McpToolRegistry NewRegistry(int cap)
        {
            return new McpToolRegistry(cap, null);
        }

        private static List<string> ManifestNames(McpToolRegistry reg)
        {
            string msg = reg.NamesManifestSystemMessage();
            var names = new List<string>();
            foreach (var line in msg.Split('\n'))
                if (line.StartsWith("- ")) names.Add(line.Substring(2));
            return names;
        }

        private static List<string> ExposedNames(McpToolRegistry reg)
        {
            var names = new List<string>();
            foreach (JObject def in reg.ExposedFunctionDefs())
                names.Add((string)def["function"]["name"]);
            return names;
        }

        private static List<string> ManifestNamesFor(McpToolRegistry reg, string workdir)
        {
            var names = new List<string>();
            foreach (var line in reg.NamesManifestSystemMessage(workdir).Split('\n'))
                if (line.StartsWith("- ")) names.Add(line.Substring(2));
            return names;
        }

        private static List<string> ExposedNamesFor(McpToolRegistry reg, string workdir)
        {
            var names = new List<string>();
            foreach (JObject def in reg.ExposedFunctionDefs(workdir))
                names.Add((string)def["function"]["name"]);
            return names;
        }

        // ---- workdir-aware advertisement (a folderless turn must not see another folder's tools) ----

        [Fact]
        public void WorkdirManifest_FolderlessTurn_OmitsScopedTools_KeepsIndependent()
        {
            var reg = NewRegistry(24);
            reg.AddConnection(FakeConn.Ready("web", new ToolDef("search")), null);       // workdir-independent
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read")), "C:\\proj"); // scoped to a folder

            var folderless = ManifestNamesFor(reg, null);
            Assert.Contains("web__search", folderless);
            Assert.DoesNotContain("files__read", folderless); // the other folder's tool is hidden

            var inFolder = ManifestNamesFor(reg, "C:\\proj");
            Assert.Contains("web__search", inFolder);
            Assert.Contains("files__read", inFolder);

            Assert.True(reg.HasToolsForWorkdir(null));         // web (independent) is usable folderless
            Assert.True(reg.HasToolsForWorkdir("C:\\proj"));
            // A different folder still sees the independent web tool, but not C:\proj's scoped files tool.
            var other = ManifestNamesFor(reg, "C:\\other");
            Assert.Contains("web__search", other);
            Assert.DoesNotContain("files__read", other);
        }

        [Fact]
        public void WorkdirExposedDefs_DropsRevealedScopedTool_OnFolderlessTurn()
        {
            var reg = NewRegistry(24);
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read")), "C:\\proj");
            reg.Reveal(new[] { "files__read" }); // revealed during a folder turn

            var folderless = ExposedNamesFor(reg, null);
            Assert.Contains("reveal_tools", folderless);
            Assert.DoesNotContain("files__read", folderless); // not usable folderless -> not sent

            Assert.Contains("files__read", ExposedNamesFor(reg, "C:\\proj")); // exposed in its folder
        }

        // ---- munging / bijection ----

        [Fact]
        public void Munges_qualified_name_and_resolves_back()
        {
            var reg = NewRegistry(8);
            var conn = FakeConn.Ready("files", new ToolDef("read"));
            reg.AddConnection(conn);

            Assert.Contains("files__read", ManifestNames(reg));

            McpServerConnection resolved;
            string toolName;
            Assert.True(reg.TryResolve("files__read", out resolved, out toolName));
            Assert.Same(conn, resolved);
            Assert.Equal("read", toolName);
        }

        [Fact]
        public void Scoped_tool_routes_to_the_connection_for_the_calling_workdir()
        {
            // Two instances of the same scoped server (one per working directory) expose the SAME
            // function name. The model sees it once; resolution picks the folder-specific connection.
            var reg = NewRegistry(8);
            var connA = FakeConn.Ready("files", new ToolDef("read"));
            var connB = FakeConn.Ready("files", new ToolDef("read"));
            reg.AddConnection(connA, "C:\\a");
            reg.AddConnection(connB, "C:\\b");

            // Surface is deduped: the name appears once regardless of how many folders provide it.
            Assert.Single(ManifestNames(reg).FindAll(delegate(string n) { return n == "files__read"; }));

            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("files__read", "C:\\a", out r, out tool));
            Assert.Same(connA, r);
            Assert.True(reg.TryResolve("files__read", "C:\\b", out r, out tool));
            Assert.Same(connB, r);
            // A folder with no instance doesn't resolve the scoped tool.
            Assert.False(reg.TryResolve("files__read", "C:\\other", out r, out tool));
        }

        [Fact]
        public void Workdir_independent_tool_resolves_for_any_calling_workdir()
        {
            var reg = NewRegistry(8);
            reg.AddConnection(FakeConn.Ready("web", new ToolDef("search")), null);

            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("web__search", "C:\\a", out r, out tool)); // independent of folder
            Assert.True(reg.TryResolve("web__search", null, out r, out tool));
        }

        [Fact]
        public void Removing_one_workdir_instance_keeps_the_tool_while_another_provides_it()
        {
            var reg = NewRegistry(8);
            var connA = FakeConn.Ready("files", new ToolDef("read"));
            var connB = FakeConn.Ready("files", new ToolDef("read"));
            reg.AddConnection(connA, "C:\\a");
            reg.AddConnection(connB, "C:\\b");

            reg.RemoveConnection(connA);

            Assert.Contains("files__read", ManifestNames(reg)); // still provided by C:\b
            McpServerConnection r; string tool;
            Assert.False(reg.TryResolve("files__read", "C:\\a", out r, out tool));
            Assert.True(reg.TryResolve("files__read", "C:\\b", out r, out tool));
            Assert.Same(connB, r);

            reg.RemoveConnection(connB);
            Assert.DoesNotContain("files__read", ManifestNames(reg)); // last instance gone
        }

        [Fact]
        public void Sanitizes_illegal_characters()
        {
            var reg = NewRegistry(8);
            reg.AddConnection(FakeConn.Ready("git", new ToolDef("weird/name")));
            Assert.Contains("git__weird_name", ManifestNames(reg));
        }

        [Fact]
        public void Disambiguates_collisions_with_hash_suffix()
        {
            // "x/y" and "x_y" under server "s" both sanitize to base "s__x_y".
            var reg = NewRegistry(8);
            reg.AddConnection(FakeConn.Ready("s", new ToolDef("x/y"), new ToolDef("x_y")));

            var names = ManifestNames(reg);
            Assert.Equal(2, names.Count);
            Assert.Contains("s__x_y", names);                 // first claimant keeps the base
            // the other is base truncated/suffixed: starts with "s__x_y_" + 6 hex, length <= 64
            string other = names[0] == "s__x_y" ? names[1] : names[0];
            Assert.StartsWith("s__x_y_", other);
            Assert.True(other.Length <= 64);

            // both still resolve to their distinct original tools
            McpServerConnection c; string t1, t2;
            Assert.True(reg.TryResolve("s__x_y", out c, out t1));
            Assert.True(reg.TryResolve(other, out c, out t2));
            Assert.NotEqual(t1, t2);
        }

        [Fact]
        public void Over_long_names_truncate_to_64_with_hash()
        {
            var reg = NewRegistry(8);
            string longTool = new string('a', 90);
            reg.AddConnection(FakeConn.Ready("srv", new ToolDef(longTool)));
            var names = ManifestNames(reg);
            Assert.Single(names);
            Assert.True(names[0].Length <= 64);
            Assert.Equal('_', names[0][64 - 7]); // "_" + 6 hex tail
        }

        [Fact]
        public void Munging_is_deterministic_across_remove_and_readd()
        {
            var reg = NewRegistry(8);
            var conn = FakeConn.Ready("s", new ToolDef("x/y"), new ToolDef("x_y"));
            reg.AddConnection(conn);
            var first = ManifestNames(reg);
            first.Sort();

            reg.RemoveConnection(conn);
            reg.AddConnection(conn);
            var second = ManifestNames(reg);
            second.Sort();

            Assert.Equal(first, second);
        }

        // ---- manifest ----

        [Fact]
        public void Manifest_is_names_only_sorted_and_reflects_mutations()
        {
            var reg = NewRegistry(8);
            var files = FakeConn.Ready("files", new ToolDef("write"), new ToolDef("read"));
            var git = FakeConn.Ready("git", new ToolDef("status"));
            reg.AddConnection(files);
            reg.AddConnection(git);

            Assert.Equal(new[] { "files__read", "files__write", "git__status" }, ManifestNames(reg).ToArray());

            reg.RemoveConnection(git);
            Assert.Equal(new[] { "files__read", "files__write" }, ManifestNames(reg).ToArray());

            // names only — no schema/description text in the manifest body
            Assert.DoesNotContain("description", reg.NamesManifestSystemMessage());
        }

        // ---- reveal ----

        [Fact]
        public void Exposed_defs_always_lead_with_reveal_tools()
        {
            var reg = NewRegistry(8);
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read")));

            var exposed = ExposedNames(reg);
            Assert.Equal(McpToolRegistry.RevealToolsName, exposed[0]);
            Assert.Single(exposed); // nothing revealed yet
        }

        [Fact]
        public void Reveal_adds_defs_and_returns_requested_schemas()
        {
            var reg = NewRegistry(8);
            JObject schema = JObject.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}");
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read", "Read a file", schema)));

            string result = reg.Reveal(new[] { "files__read" });
            JArray defs = JArray.Parse(result.Split('\n')[0]);
            Assert.Single(defs);
            Assert.Equal("files__read", (string)defs[0]["name"]);
            Assert.Equal("Read a file", (string)defs[0]["description"]);
            Assert.NotNull(defs[0]["parameters"]["properties"]["path"]);

            // now exposed in the tools array
            Assert.Contains("files__read", ExposedNames(reg));
        }

        [Fact]
        public void Reveal_notes_unknown_names()
        {
            var reg = NewRegistry(8);
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read")));
            string result = reg.Reveal(new[] { "files__read", "files__nope" });
            Assert.Contains("unknown tool: files__nope", result);
        }

        // ---- LRU ----

        [Fact]
        public void Exceeding_cap_evicts_least_recently_used()
        {
            var reg = NewRegistry(2);
            reg.AddConnection(FakeConn.Ready("s", new ToolDef("a"), new ToolDef("b"), new ToolDef("c")));

            reg.Reveal(new[] { "s__a" });
            reg.Reveal(new[] { "s__b" });
            reg.Reveal(new[] { "s__c" }); // cap 2 → s__a (oldest) evicted

            var exposed = ExposedNames(reg);
            Assert.DoesNotContain("s__a", exposed);
            Assert.Contains("s__b", exposed);
            Assert.Contains("s__c", exposed);

            // evicted tool is still in the manifest and re-revealable
            Assert.Contains("s__a", ManifestNames(reg));
            reg.Reveal(new[] { "s__a" });
            Assert.Contains("s__a", ExposedNames(reg));
        }

        [Fact]
        public void Resolving_bumps_recency_to_prevent_eviction()
        {
            var reg = NewRegistry(2);
            reg.AddConnection(FakeConn.Ready("s", new ToolDef("a"), new ToolDef("b"), new ToolDef("c")));

            reg.Reveal(new[] { "s__a" });
            reg.Reveal(new[] { "s__b" });

            // touch s__a via a resolve so it's now most-recent
            McpServerConnection c; string t;
            Assert.True(reg.TryResolve("s__a", out c, out t));

            reg.Reveal(new[] { "s__c" }); // should evict s__b (now the LRU), not s__a

            var exposed = ExposedNames(reg);
            Assert.Contains("s__a", exposed);
            Assert.DoesNotContain("s__b", exposed);
            Assert.Contains("s__c", exposed);
        }

        // ---- lifecycle ----

        [Fact]
        public void Refresh_prunes_removed_tools_from_catalog_and_revealed()
        {
            var reg = NewRegistry(8);
            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("s", out ft, new ToolDef("a"), new ToolDef("b"));
            reg.AddConnection(conn);
            reg.Reveal(new[] { "s__a", "s__b" });
            Assert.Contains("s__b", ExposedNames(reg));

            // server drops tool "b" on a subsequent tools/list
            ft.Tools = new List<ToolDef> { new ToolDef("a") };
            reg.RefreshConnection(conn);

            Assert.Equal(new[] { "s__a" }, ManifestNames(reg).ToArray());
            Assert.DoesNotContain("s__b", ExposedNames(reg)); // pruned from revealed too
            McpServerConnection c; string t;
            Assert.False(reg.TryResolve("s__b", out c, out t));
        }

        [Fact]
        public void Remove_drops_a_faulted_servers_tools()
        {
            var reg = NewRegistry(8);
            var conn = FakeConn.Ready("s", new ToolDef("a"));
            reg.AddConnection(conn);
            reg.Reveal(new[] { "s__a" });

            reg.RemoveConnection(conn);

            Assert.Empty(ManifestNames(reg));
            McpServerConnection c; string t;
            Assert.False(reg.TryResolve("s__a", out c, out t));
            Assert.Equal(new[] { McpToolRegistry.RevealToolsName }, ExposedNames(reg).ToArray());
        }

        [Fact]
        public void HasTools_reflects_the_catalog()
        {
            var reg = NewRegistry(8);
            Assert.False(reg.HasTools);
            var conn = FakeConn.Ready("files", new ToolDef("read"));
            reg.AddConnection(conn);
            Assert.True(reg.HasTools);
            reg.RemoveConnection(conn);
            Assert.False(reg.HasTools);
        }

        [Fact]
        public void Reveal_tools_name_is_never_produced_by_munging()
        {
            var reg = NewRegistry(8);
            reg.AddConnection(FakeConn.Ready("reveal", new ToolDef("tools")));
            Assert.Contains("reveal__tools", ManifestNames(reg)); // double underscore, not "reveal_tools"
            Assert.DoesNotContain("reveal_tools", ManifestNames(reg));
        }

        // ---- git-over-command preference note ----

        private const string GitPreferNote = "prefer the dedicated git__ tools";

        [Fact]
        public void Manifest_steers_to_git_tools_when_command_also_present()
        {
            var reg = NewRegistry(8);
            reg.AddConnection(FakeConn.Ready("git", new ToolDef("status")));
            reg.AddConnection(FakeConn.Ready("command", new ToolDef("run")));
            Assert.Contains(GitPreferNote, reg.NamesManifestSystemMessage());
        }

        [Fact]
        public void Manifest_omits_git_preference_note_without_command()
        {
            var reg = NewRegistry(8);
            reg.AddConnection(FakeConn.Ready("git", new ToolDef("status")));
            Assert.DoesNotContain(GitPreferNote, reg.NamesManifestSystemMessage());
        }

        [Fact]
        public void Manifest_omits_git_preference_note_without_git()
        {
            var reg = NewRegistry(8);
            reg.AddConnection(FakeConn.Ready("command", new ToolDef("run")));
            Assert.DoesNotContain(GitPreferNote, reg.NamesManifestSystemMessage());
        }
    }
}
