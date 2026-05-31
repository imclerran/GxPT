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
        public void Lists_the_four_documented_tools_with_schema()
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
