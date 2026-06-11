using System;
using System.IO;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class AgentsFileInjectionTests : IDisposable
    {
        private readonly string _root;

        public AgentsFileInjectionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_agentsmd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void Builds_framed_block_from_workspace_root_agents_md()
        {
            File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "# Rules\n\nAlways run the tests.\n");

            string block = AgentsFileInjection.Build(_root);

            Assert.NotNull(block);
            Assert.Contains("AGENTS.md", block);                  // framing names the source
            Assert.Contains("Always run the tests.", block);      // and carries the content
            Assert.False(block.EndsWith("\n"));                   // trailing newlines trimmed
        }

        [Fact]
        public void No_workspace_no_file_or_empty_file_yields_null()
        {
            Assert.Null(AgentsFileInjection.Build(null));
            Assert.Null(AgentsFileInjection.Build(_root)); // no AGENTS.md present

            File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "");
            Assert.Null(AgentsFileInjection.Build(_root));
        }

        [Fact]
        public void Oversized_file_is_truncated_with_marker()
        {
            File.WriteAllText(Path.Combine(_root, "AGENTS.md"), new string('x', 40 * 1024));

            string block = AgentsFileInjection.Build(_root);

            Assert.NotNull(block);
            Assert.Contains("[AGENTS.md truncated]", block);
            Assert.True(block.Length < 40 * 1024);
        }
    }
}
