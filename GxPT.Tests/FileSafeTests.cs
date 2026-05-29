using System;
using System.IO;
using System.Text;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class FileSafeTests : IDisposable
    {
        private readonly string _root;

        public FileSafeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_filesafetests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void WriteAllTextAtomic_CreatesFileWithContent()
        {
            string path = Path.Combine(_root, "new.txt");
            FileSafe.WriteAllTextAtomic(path, "hello", new UTF8Encoding(false));
            Assert.True(File.Exists(path));
            Assert.Equal("hello", File.ReadAllText(path));
        }

        [Fact]
        public void WriteAllTextAtomic_OverwritesExistingFile()
        {
            string path = Path.Combine(_root, "over.txt");
            FileSafe.WriteAllTextAtomic(path, "first", new UTF8Encoding(false));
            FileSafe.WriteAllTextAtomic(path, "second", new UTF8Encoding(false));
            Assert.Equal("second", File.ReadAllText(path));
        }

        [Fact]
        public void WriteAllTextAtomic_CreatesMissingDirectory()
        {
            string path = Path.Combine(_root, "sub", "deep", "file.txt");
            FileSafe.WriteAllTextAtomic(path, "content", new UTF8Encoding(false));
            Assert.True(File.Exists(path));
            Assert.Equal("content", File.ReadAllText(path));
        }

        [Fact]
        public void WriteAllTextAtomic_LeavesNoTempFiles()
        {
            string path = Path.Combine(_root, "clean.txt");
            FileSafe.WriteAllTextAtomic(path, "data", new UTF8Encoding(false));
            // The temp file is named "<path>.tmp-<guid>" and must be gone after a successful write.
            string[] leftovers = Directory.GetFiles(_root, "clean.txt.tmp-*");
            Assert.Empty(leftovers);
        }

        [Fact]
        public void WriteAllTextAtomic_WritesUtf8WithoutBom()
        {
            string path = Path.Combine(_root, "nobom.txt");
            FileSafe.WriteAllTextAtomic(path, "abc", new UTF8Encoding(false));
            byte[] bytes = File.ReadAllBytes(path);
            // No UTF-8 BOM (EF BB BF) at the start.
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal(new byte[] { 0x61, 0x62, 0x63 }, bytes);
        }
    }
}
