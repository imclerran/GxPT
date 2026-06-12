using System.Collections.Generic;
using GxPT;
using Mcp35.Client;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GxPT.Tests.Mcp
{
    public class McpToolRegistryTests
    {
        // Reveal state lives on the conversation now (prompt-caching design): tests thread an
        // explicit revealed-name list through Reveal / ExposedFunctionDefs the way the
        // orchestrator threads the conversation's list.
        private static McpToolRegistry NewRegistry()
        {
            return new McpToolRegistry(null);
        }

        private static List<string> ManifestNames(McpToolRegistry reg)
        {
            string msg = reg.NamesManifestSystemMessage();
            var names = new List<string>();
            foreach (var line in msg.Split('\n'))
                if (line.StartsWith("- ")) names.Add(line.Substring(2));
            return names;
        }

        private static List<string> ExposedNames(McpToolRegistry reg, List<string> revealed)
        {
            var names = new List<string>();
            foreach (JObject def in reg.ExposedFunctionDefs(revealed))
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

        private static List<string> ExposedNamesFor(McpToolRegistry reg, string workdir, List<string> revealed)
        {
            var names = new List<string>();
            foreach (JObject def in reg.ExposedFunctionDefs(workdir, revealed))
                names.Add((string)def["function"]["name"]);
            return names;
        }

        // ---- workdir-aware advertisement (a folderless turn must not see another folder's tools) ----

        [Fact]
        public void WorkdirManifest_FolderlessTurn_OmitsScopedTools_KeepsIndependent()
        {
            var reg = NewRegistry();
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
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read")), "C:\\proj");
            var revealed = new List<string>();
            reg.Reveal(new[] { "files__read" }, revealed); // revealed during a folder turn

            var folderless = ExposedNamesFor(reg, null, revealed);
            Assert.Contains("reveal_tools", folderless);
            Assert.DoesNotContain("files__read", folderless); // not usable folderless -> not sent

            Assert.Contains("files__read", ExposedNamesFor(reg, "C:\\proj", revealed)); // exposed in its folder
        }

        // ---- workdir-aware schema selection across a scoped/independent name collision (skills) ----

        // The skills server runs as TWO instances under one server name: an eager workdir-less one
        // (default scope "user") and a per-folder workdir-scoped one (default scope "project"). They
        // collide on one function name, so the model must be shown — and must reveal — the schema of the
        // instance its call will actually reach. Before the fix the surface always used list[0] (the
        // eager workdir-less entry), so a folder turn read "default user" but its call ran against the
        // project-default instance.
        private static string DescOf(string revealJson)
        {
            JArray defs = JArray.Parse(revealJson.Split('\n')[0]);
            return (string)defs[0]["description"];
        }

        [Fact]
        public void WorkdirReveal_PicksSchemaOfTheInstanceTheCallWillReach()
        {
            var reg = NewRegistry();
            // Eager workdir-less instance registers FIRST, so it is list[0] (reproduces the collision).
            reg.AddConnection(FakeConn.Ready("skills", new ToolDef("edit", "scope default user", null)), null);
            reg.AddConnection(FakeConn.Ready("skills", new ToolDef("edit", "scope default project", null)), "C:\\proj");

            // A folder turn reveals the workdir-scoped instance's schema (the one TryResolve will call).
            var revealed = new List<string>();
            Assert.Equal("scope default project", DescOf(reg.Reveal(new[] { "skills__edit" }, "C:\\proj", revealed)));
            // A folderless turn reveals the workdir-less instance's schema.
            Assert.Equal("scope default user", DescOf(reg.Reveal(new[] { "skills__edit" }, null, revealed)));
        }

        [Fact]
        public void WorkdirExposedDefs_UsesSchemaOfTheInstanceTheCallWillReach()
        {
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("skills", new ToolDef("edit", "scope default user", null)), null);
            reg.AddConnection(FakeConn.Ready("skills", new ToolDef("edit", "scope default project", null)), "C:\\proj");
            var revealed = new List<string>();
            reg.Reveal(new[] { "skills__edit" }, "C:\\proj", revealed);

            string desc = null;
            foreach (JObject def in reg.ExposedFunctionDefs("C:\\proj", revealed))
                if ((string)def["function"]["name"] == "skills__edit") desc = (string)def["function"]["description"];
            Assert.Equal("scope default project", desc);
        }

        // ---- munging / bijection ----

        [Fact]
        public void Munges_qualified_name_and_resolves_back()
        {
            var reg = NewRegistry();
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
            var reg = NewRegistry();
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
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("web", new ToolDef("search")), null);

            McpServerConnection r; string tool;
            Assert.True(reg.TryResolve("web__search", "C:\\a", out r, out tool)); // independent of folder
            Assert.True(reg.TryResolve("web__search", null, out r, out tool));
        }

        [Fact]
        public void Removing_one_workdir_instance_keeps_the_tool_while_another_provides_it()
        {
            var reg = NewRegistry();
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
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("git", new ToolDef("weird/name")));
            Assert.Contains("git__weird_name", ManifestNames(reg));
        }

        [Fact]
        public void Disambiguates_collisions_with_hash_suffix()
        {
            // "x/y" and "x_y" under server "s" both sanitize to base "s__x_y".
            var reg = NewRegistry();
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
            var reg = NewRegistry();
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
            var reg = NewRegistry();
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
            var reg = NewRegistry();
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
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read")));

            var exposed = ExposedNames(reg, new List<string>());
            Assert.Equal(McpToolRegistry.RevealToolsName, exposed[0]);
            Assert.Single(exposed); // nothing revealed yet
        }

        [Fact]
        public void Reveal_adds_defs_and_returns_requested_schemas()
        {
            var reg = NewRegistry();
            JObject schema = JObject.Parse("{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}");
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read", "Read a file", schema)));

            var revealed = new List<string>();
            string result = reg.Reveal(new[] { "files__read" }, revealed);
            JArray defs = JArray.Parse(result.Split('\n')[0]);
            Assert.Single(defs);
            Assert.Equal("files__read", (string)defs[0]["name"]);
            Assert.Equal("Read a file", (string)defs[0]["description"]);
            Assert.NotNull(defs[0]["parameters"]["properties"]["path"]);

            // recorded in the caller's list and now exposed in the tools array
            Assert.Contains("files__read", revealed);
            Assert.Contains("files__read", ExposedNames(reg, revealed));
        }

        [Fact]
        public void Reveal_notes_unknown_names()
        {
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read")));
            var revealed = new List<string>();
            string result = reg.Reveal(new[] { "files__read", "files__nope" }, revealed);
            Assert.Contains("unknown tool: files__nope", result);
            Assert.DoesNotContain("files__nope", revealed); // unknown names are not recorded
        }

        // ---- reveal-set stability (prompt caching) ----

        [Fact]
        public void Registry_never_evicts_from_the_callers_revealed_list()
        {
            // Eviction is the orchestrator's job and provider-gated; the registry itself only
            // appends. Reveal any number of tools and all of them stay exposed.
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("s", new ToolDef("a"), new ToolDef("b"), new ToolDef("c")));

            var revealed = new List<string>();
            reg.Reveal(new[] { "s__a" }, revealed);
            reg.Reveal(new[] { "s__b" }, revealed);
            reg.Reveal(new[] { "s__c" }, revealed);

            var exposed = ExposedNames(reg, revealed);
            Assert.Contains("s__a", exposed);
            Assert.Contains("s__b", exposed);
            Assert.Contains("s__c", exposed);
        }

        [Fact]
        public void Exposed_defs_are_name_sorted_regardless_of_reveal_order()
        {
            // The tools array renders at position 0 of the prompt: any membership or order change
            // invalidates the provider's prompt cache for the whole conversation, so emission must
            // not depend on reveal order or recency.
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("s", new ToolDef("a"), new ToolDef("b")));

            var revealed = new List<string>();
            reg.Reveal(new[] { "s__b" }, revealed);
            reg.Reveal(new[] { "s__a" }, revealed);
            Assert.Equal(new[] { "reveal_tools", "s__a", "s__b" }, ExposedNames(reg, revealed).ToArray());

            // A re-reveal bumps recency (the list reorders) without changing the emitted order.
            reg.Reveal(new[] { "s__b" }, revealed);
            Assert.Equal(new[] { "s__a", "s__b" }, revealed.ToArray()); // recency: a older than b
            Assert.Equal(new[] { "reveal_tools", "s__a", "s__b" }, ExposedNames(reg, revealed).ToArray());
        }

        [Fact]
        public void Duplicate_names_in_the_revealed_list_emit_one_def()
        {
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("s", new ToolDef("a")));
            var revealed = new List<string> { "s__a", "s__a" };
            Assert.Equal(new[] { "reveal_tools", "s__a" }, ExposedNames(reg, revealed).ToArray());
        }

        // ---- lifecycle ----

        [Fact]
        public void Refresh_prunes_removed_tools_from_catalog_and_exposure()
        {
            var reg = NewRegistry();
            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("s", out ft, new ToolDef("a"), new ToolDef("b"));
            reg.AddConnection(conn);
            var revealed = new List<string>();
            reg.Reveal(new[] { "s__a", "s__b" }, revealed);
            Assert.Contains("s__b", ExposedNames(reg, revealed));

            // server drops tool "b" on a subsequent tools/list
            ft.Tools = new List<ToolDef> { new ToolDef("a") };
            reg.RefreshConnection(conn);

            Assert.Equal(new[] { "s__a" }, ManifestNames(reg).ToArray());
            // The conversation's list keeps the name (so the def reappears if the server brings the
            // tool back); emission skips it while it is absent from the catalog.
            Assert.Contains("s__b", revealed);
            Assert.DoesNotContain("s__b", ExposedNames(reg, revealed));
            McpServerConnection c; string t;
            Assert.False(reg.TryResolve("s__b", out c, out t));
        }

        [Fact]
        public void Remove_drops_a_faulted_servers_tools()
        {
            var reg = NewRegistry();
            var conn = FakeConn.Ready("s", new ToolDef("a"));
            reg.AddConnection(conn);
            var revealed = new List<string>();
            reg.Reveal(new[] { "s__a" }, revealed);

            reg.RemoveConnection(conn);

            Assert.Empty(ManifestNames(reg));
            McpServerConnection c; string t;
            Assert.False(reg.TryResolve("s__a", out c, out t));
            Assert.Equal(new[] { McpToolRegistry.RevealToolsName }, ExposedNames(reg, revealed).ToArray());
        }

        [Fact]
        public void HasTools_reflects_the_catalog()
        {
            var reg = NewRegistry();
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
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("reveal", new ToolDef("tools")));
            Assert.Contains("reveal__tools", ManifestNames(reg)); // double underscore, not "reveal_tools"
            Assert.DoesNotContain("reveal_tools", ManifestNames(reg));
        }

        // ---- NamesForWorkdir / Changed (the status bar's tool count) ----

        [Fact]
        public void NamesForWorkdir_matches_the_manifests_workdir_filtering()
        {
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("web", new ToolDef("search")), null);       // workdir-independent
            reg.AddConnection(FakeConn.Ready("files", new ToolDef("read")), "C:\\proj"); // scoped to a folder

            var folderless = new List<string>(reg.NamesForWorkdir(null));
            folderless.Sort();
            Assert.Equal(ManifestNamesFor(reg, null), folderless);

            var inFolder = new List<string>(reg.NamesForWorkdir("C:\\proj"));
            inFolder.Sort();
            Assert.Equal(ManifestNamesFor(reg, "C:\\proj"), inFolder);
        }

        [Fact]
        public void Changed_fires_on_add_refresh_and_remove()
        {
            var reg = NewRegistry();
            int fired = 0;
            reg.Changed += delegate { fired++; };

            RegistryFakeTransport ft;
            var conn = FakeConn.Ready("s", out ft, new ToolDef("a"));
            reg.AddConnection(conn);
            Assert.Equal(1, fired);

            ft.Tools = new List<ToolDef> { new ToolDef("b") };
            reg.RefreshConnection(conn);
            Assert.Equal(2, fired);

            reg.RemoveConnection(conn);
            Assert.Equal(3, fired);
        }

        // ---- git-over-command preference note ----

        private const string GitPreferNote = "prefer the dedicated git__ tools";

        [Fact]
        public void Manifest_steers_to_git_tools_when_command_also_present()
        {
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("git", new ToolDef("status")));
            reg.AddConnection(FakeConn.Ready("command", new ToolDef("run")));
            Assert.Contains(GitPreferNote, reg.NamesManifestSystemMessage());
        }

        [Fact]
        public void Manifest_omits_git_preference_note_without_command()
        {
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("git", new ToolDef("status")));
            Assert.DoesNotContain(GitPreferNote, reg.NamesManifestSystemMessage());
        }

        [Fact]
        public void Manifest_omits_git_preference_note_without_git()
        {
            var reg = NewRegistry();
            reg.AddConnection(FakeConn.Ready("command", new ToolDef("run")));
            Assert.DoesNotContain(GitPreferNote, reg.NamesManifestSystemMessage());
        }
    }
}
