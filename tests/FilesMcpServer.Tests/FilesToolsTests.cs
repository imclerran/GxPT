using System;
using System.IO;
using System.Text;
using Mcp35.Server;
using Newtonsoft.Json.Linq;
using Xunit;

namespace FilesMcpServer.Tests
{
    public class FilesToolsTests : IDisposable
    {
        private readonly string _root;

        public FilesToolsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "filesmcp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private string Abs(string rel) { return Path.Combine(_root, rel); }

        // ---- Criterion 1: listing ----

        [Fact]
        public void Lists_the_documented_tools_with_schema()
        {
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, Harness.ToolsList(1));

            JArray tools = (JArray)msgs[0]["result"]["tools"];
            var names = new System.Collections.Generic.List<string>();
            foreach (JToken t in tools) names.Add((string)t["name"]);

            Assert.Contains("read", names);
            Assert.Contains("list", names);
            Assert.Contains("write", names);
            Assert.Contains("delete", names);
            Assert.Contains("edit", names);
            Assert.Contains("search", names);
            // schema intact
            foreach (JToken t in tools)
                Assert.Equal("object", (string)t["inputSchema"]["type"]);
        }

        // ---- Criterion 2: sandbox ----

        [Theory]
        [InlineData("../escape.txt")]
        [InlineData("../../etc/passwd")]
        [InlineData("subdir/../../outside.txt")]
        public void Rejects_parent_traversal(string path)
        {
            var server = Harness.NewFilesServer(_root);
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "read", Harness.Args("path", path)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("escape", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Rejects_absolute_paths()
        {
            var server = Harness.NewFilesServer(_root);
            string abs = Path.Combine(_root, "x.txt"); // a rooted path
            var msgs = Harness.Exchange(server, Harness.ToolsCall(1, "read", Harness.Args("path", abs)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("absolute", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Sibling_root_prefix_is_not_within()
        {
            // The classic "/root" vs "/root-evil" boundary trick: a sibling dir whose path shares
            // the root's string prefix must NOT be considered inside the sandbox.
            string sibling = _root + "-evil";
            Directory.CreateDirectory(sibling);
            try
            {
                File.WriteAllText(Path.Combine(sibling, "secret.txt"), "nope");
                var sandbox = new PathSandbox(_root);
                Assert.False(sandbox.IsWithin(Path.Combine(sibling, "secret.txt")));
            }
            finally
            {
                try { Directory.Delete(sibling, true); } catch { }
            }
        }

        [Fact]
        public void In_root_read_write_list_delete_round_trip()
        {
            var server = Harness.NewFilesServer(_root);

            // write
            var w = Harness.Exchange(server, Harness.ToolsCall(1, "write",
                Harness.Args("path", "notes/todo.txt", "content", "hello world", "create_dirs", true)));
            Assert.False(Harness.IsError(w[0]));
            Assert.True(File.Exists(Abs(Path.Combine("notes", "todo.txt"))));

            // read (fresh server instances are fine; same root)
            var r = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "notes/todo.txt")));
            Assert.Equal("hello world", Harness.Text(r[0]));

            // list
            var l = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "list", Harness.Args("path", "notes")));
            Assert.False(Harness.IsError(l[0]));
            Assert.Equal(1, (int)l[0]["result"]["structuredContent"]["count"]);

            // delete
            var d = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "delete", Harness.Args("path", "notes/todo.txt")));
            Assert.False(Harness.IsError(d[0]));
            Assert.False(File.Exists(Abs(Path.Combine("notes", "todo.txt"))));
        }

        [Fact]
        public void Oversize_read_is_error()
        {
            File.WriteAllText(Abs("big.txt"), new string('a', 2 * 1024 * 1024)); // 2 MiB > 1 MiB cap
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "big.txt")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("too large", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Binary_read_is_error()
        {
            File.WriteAllBytes(Abs("blob.bin"), new byte[] { 1, 2, 0, 3, 4 }); // NUL byte → binary
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "blob.bin")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("not a text file", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Write_without_create_dirs_into_missing_parent_is_error()
        {
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "write", Harness.Args("path", "missing/x.txt", "content", "y")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Write_is_atomic_overwrite()
        {
            File.WriteAllText(Abs("f.txt"), "old");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "write", Harness.Args("path", "f.txt", "content", "new")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("new", File.ReadAllText(Abs("f.txt")));
        }

        [Fact]
        public void Delete_refuses_non_empty_directory()
        {
            Directory.CreateDirectory(Abs("full"));
            File.WriteAllText(Abs(Path.Combine("full", "a.txt")), "x");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "delete", Harness.Args("path", "full")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("not empty", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Delete_removes_empty_directory()
        {
            Directory.CreateDirectory(Abs("empty"));
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "delete", Harness.Args("path", "empty")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.False(Directory.Exists(Abs("empty")));
        }

        [Fact]
        public void Read_bom_file_strips_bom()
        {
            byte[] withBom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] text = Encoding.UTF8.GetBytes("café");
            byte[] all = new byte[withBom.Length + text.Length];
            Buffer.BlockCopy(withBom, 0, all, 0, withBom.Length);
            Buffer.BlockCopy(text, 0, all, withBom.Length, text.Length);
            File.WriteAllBytes(Abs("bom.txt"), all);

            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "bom.txt")));
            Assert.Equal("café", Harness.Text(msgs[0])); // no leading BOM char
        }

        // ---- read: line range + numbering ----

        [Fact]
        public void Read_returns_requested_line_range()
        {
            File.WriteAllText(Abs("r.txt"), "one\ntwo\nthree\nfour\nfive");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "r.txt", "start_line", 2, "end_line", 4)));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("two\nthree\nfour", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Read_with_line_numbers_prefixes_each_line()
        {
            File.WriteAllText(Abs("r.txt"), "alpha\nbeta\ngamma");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "r.txt", "line_numbers", true)));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal("1\talpha\n2\tbeta\n3\tgamma", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Read_start_line_past_eof_is_error()
        {
            File.WriteAllText(Abs("r.txt"), "only\ntwo");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "r.txt", "start_line", 9)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("exceeds file length", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Read_whole_file_is_verbatim_when_no_range_or_numbers()
        {
            File.WriteAllText(Abs("r.txt"), "trailing\nnewline\n");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "r.txt")));
            Assert.Equal("trailing\nnewline\n", Harness.Text(msgs[0]));
        }

        // ---- edit ----

        [Fact]
        public void Edit_replaces_unique_span()
        {
            File.WriteAllText(Abs("e.txt"), "hello world");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "e.txt",
                    "old_string", "world", "new_string", "there")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["replacements"]);
            Assert.Equal("hello there", File.ReadAllText(Abs("e.txt")));
        }

        [Fact]
        public void Edit_non_unique_without_replace_all_is_error()
        {
            File.WriteAllText(Abs("e.txt"), "a a a");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "e.txt",
                    "old_string", "a", "new_string", "b")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("not unique", Harness.Text(msgs[0]));
            Assert.Equal("a a a", File.ReadAllText(Abs("e.txt"))); // unchanged
        }

        [Fact]
        public void Edit_replace_all_replaces_every_occurrence()
        {
            File.WriteAllText(Abs("e.txt"), "x x x");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "e.txt",
                    "old_string", "x", "new_string", "y", "replace_all", true)));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(3, (int)msgs[0]["result"]["structuredContent"]["replacements"]);
            Assert.Equal("y y y", File.ReadAllText(Abs("e.txt")));
        }

        [Fact]
        public void Edit_missing_old_string_is_error()
        {
            File.WriteAllText(Abs("e.txt"), "content");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "e.txt",
                    "old_string", "absent", "new_string", "z")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("not found", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Edit_nonexistent_file_is_error()
        {
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "nope.txt",
                    "old_string", "a", "new_string", "b")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("file not found", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Edit_rejects_parent_traversal()
        {
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "edit", Harness.Args("path", "../escape.txt",
                    "old_string", "a", "new_string", "b")));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("escape", Harness.Text(msgs[0]));
        }

        // ---- search ----

        [Fact]
        public void Search_finds_substring_matches_with_line_numbers()
        {
            File.WriteAllText(Abs("a.txt"), "alpha\nneedle here\nbeta");
            File.WriteAllText(Abs("b.txt"), "no match\nanother needle\n");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "needle")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(2, (int)msgs[0]["result"]["structuredContent"]["count"]);
            JArray m = (JArray)msgs[0]["result"]["structuredContent"]["matches"];
            // a.txt match is on line 2
            bool found = false;
            foreach (JToken t in m)
                if ((string)t["path"] == "a.txt" && (int)t["line"] == 2) found = true;
            Assert.True(found);
        }

        [Fact]
        public void Search_ignore_case()
        {
            File.WriteAllText(Abs("a.txt"), "Hello THERE");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "there", "ignore_case", true)));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_regex_mode()
        {
            File.WriteAllText(Abs("a.txt"), "id=42\nid=abc\nid=7");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "id=[0-9]+", "regex", true)));
            Assert.Equal(2, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_invalid_regex_is_error()
        {
            File.WriteAllText(Abs("a.txt"), "x");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "(unclosed", "regex", true)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("invalid regex", Harness.Text(msgs[0]));
        }

        [Fact]
        public void Search_glob_filters_by_filename()
        {
            File.WriteAllText(Abs("keep.cs"), "target");
            File.WriteAllText(Abs("skip.txt"), "target");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "target", "glob", "*.cs")));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
            JArray m = (JArray)msgs[0]["result"]["structuredContent"]["matches"];
            Assert.Equal("keep.cs", (string)m[0]["path"]);
        }

        [Fact]
        public void Search_skips_binary_files()
        {
            File.WriteAllBytes(Abs("blob.bin"), new byte[] { (byte)'h', (byte)'i', 0, (byte)'t' });
            File.WriteAllText(Abs("ok.txt"), "hit");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "hi")));
            // Only ok.txt's "hit" line counts; the binary blob is skipped.
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_recurses_subdirectories()
        {
            Directory.CreateDirectory(Abs("sub"));
            File.WriteAllText(Abs(Path.Combine("sub", "deep.txt")), "buried treasure");
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "treasure")));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
        }

        [Fact]
        public void Search_max_results_truncates()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 10; i++) sb.Append("match\n");
            File.WriteAllText(Abs("many.txt"), sb.ToString());
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "match", "max_results", 3)));
            Assert.Equal(3, (int)msgs[0]["result"]["structuredContent"]["count"]);
            Assert.True((bool)msgs[0]["result"]["structuredContent"]["truncated"]);
        }

        // ---- large files: search streams (no size cap); ranged read streams a slice ----

        [Fact]
        public void Search_finds_matches_in_oversize_file()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 100000; i++) sb.Append("filler line\n"); // ~1.2 MiB > 1 MiB cap
            sb.Append("the needle is here\n");
            File.WriteAllText(Abs("big.txt"), sb.ToString());
            Assert.True(new FileInfo(Abs("big.txt")).Length > 1024 * 1024);

            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "search", Harness.Args("query", "needle")));
            Assert.False(Harness.IsError(msgs[0]));
            Assert.Equal(1, (int)msgs[0]["result"]["structuredContent"]["count"]);
            JArray m = (JArray)msgs[0]["result"]["structuredContent"]["matches"];
            Assert.Equal(100001, (int)m[0]["line"]);
        }

        [Fact]
        public void Read_range_works_on_oversize_file_while_whole_file_is_capped()
        {
            var sb = new StringBuilder();
            sb.Append("first line\n");
            for (int i = 0; i < 120000; i++) sb.Append("padding padding padding\n"); // ~2.8 MiB
            File.WriteAllText(Abs("big.txt"), sb.ToString());
            Assert.True(new FileInfo(Abs("big.txt")).Length > 1024 * 1024);

            // Ranged read of the first line succeeds despite the file exceeding the cap.
            var r = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "big.txt", "start_line", 1, "end_line", 1)));
            Assert.False(Harness.IsError(r[0]));
            Assert.Equal("first line", Harness.Text(r[0]));

            // Whole-file read is still capped.
            var w = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "big.txt")));
            Assert.True(Harness.IsError(w[0]));
            Assert.Contains("too large", Harness.Text(w[0]));
        }

        [Fact]
        public void Read_range_rejects_an_oversize_selection()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 120000; i++) sb.Append("padding padding padding\n"); // ~2.8 MiB
            File.WriteAllText(Abs("big.txt"), sb.ToString());

            // Asking for the whole thing via an open-ended range hits the output cap.
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "big.txt", "start_line", 1)));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.Contains("too large", Harness.Text(msgs[0]));
        }

        // ---- Criterion 6: lifecycle ----

        [Fact]
        public void Missing_file_read_is_error_and_server_survives()
        {
            var msgs = Harness.Exchange(Harness.NewFilesServer(_root),
                Harness.ToolsCall(1, "read", Harness.Args("path", "nope.txt")),
                Harness.Request(2, "ping", null));
            Assert.True(Harness.IsError(msgs[0]));
            Assert.NotNull(msgs[1]["result"]); // ping still answered
        }
    }
}
