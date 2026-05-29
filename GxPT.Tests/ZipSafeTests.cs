using System;
using System.IO;
using System.Text;
using Ionic.Zip;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class ZipSafeTests : IDisposable
    {
        private readonly string _root;

        public ZipSafeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_ziptests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        private string Path2(string name)
        {
            return Path.Combine(_root, name);
        }

        [Fact]
        public void SafeExtract_ExtractsNestedFilesWithContent()
        {
            string zipPath = Path2("ok.zip");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("a.txt", Encoding.UTF8.GetBytes("alpha"));
                zip.AddEntry("sub/b.txt", Encoding.UTF8.GetBytes("beta"));
                zip.Save(zipPath);
            }

            string dest = Path2("out_ok");
            ZipSafe.SafeExtract(zipPath, dest, true);

            Assert.Equal("alpha", File.ReadAllText(Path.Combine(dest, "a.txt")));
            Assert.Equal("beta", File.ReadAllText(Path.Combine(dest, "sub", "b.txt")));
        }

        [Fact]
        public void SafeExtract_RejectsParentTraversal_AndDoesNotEscapeDestination()
        {
            string zipPath = Path2("evil.zip");
            using (var zip = new ZipFile())
            {
                // Set FileName directly so the crafted traversal name is stored verbatim.
                var e = zip.AddEntry("placeholder.txt", Encoding.UTF8.GetBytes("x"));
                e.FileName = "../escaped.txt";
                zip.Save(zipPath);
            }

            string dest = Path2("out_evil");
            Assert.Throws<InvalidDataException>(() => ZipSafe.SafeExtract(zipPath, dest, true));

            // The validation pass fails before extraction, so nothing escapes the destination root.
            Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
        }

        [Fact]
        public void SafeExtract_RejectsReservedDeviceName()
        {
            string zipPath = Path2("dev.zip");
            using (var zip = new ZipFile())
            {
                var e = zip.AddEntry("placeholder.txt", Encoding.UTF8.GetBytes("x"));
                e.FileName = "CON.txt";
                zip.Save(zipPath);
            }

            string dest = Path2("out_dev");
            Assert.Throws<InvalidDataException>(() => ZipSafe.SafeExtract(zipPath, dest, true));
        }

        [Fact]
        public void SafeExtract_CreatesDestinationDirectoryWhenMissing()
        {
            string zipPath = Path2("mk.zip");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("only.txt", Encoding.UTF8.GetBytes("hello"));
                zip.Save(zipPath);
            }

            string dest = Path2(Path.Combine("nested", "dest"));
            Assert.False(Directory.Exists(dest));

            ZipSafe.SafeExtract(zipPath, dest, true);

            Assert.True(Directory.Exists(dest));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(dest, "only.txt")));
        }

        [Fact]
        public void SafeExtract_RejectsPercentEncodedTraversal()
        {
            string zipPath = Path2("enc.zip");
            using (var zip = new ZipFile())
            {
                var e = zip.AddEntry("placeholder.txt", Encoding.UTF8.GetBytes("x"));
                e.FileName = "%2e%2e/evil.txt";
                zip.Save(zipPath);
            }

            string dest = Path2("out_enc");
            Assert.Throws<InvalidDataException>(() => ZipSafe.SafeExtract(zipPath, dest, true));
        }

        [Fact]
        public void SafeExtract_CreatesDirectoryEntries()
        {
            string zipPath = Path2("dir.zip");
            using (var zip = new ZipFile())
            {
                zip.AddDirectoryByName("emptydir");
                zip.Save(zipPath);
            }

            string dest = Path2("out_dir");
            ZipSafe.SafeExtract(zipPath, dest, true);

            Assert.True(Directory.Exists(Path.Combine(dest, "emptydir")));
        }

        [Fact]
        public void SafeExtract_WithoutOverwrite_ThrowsWhenFileExists()
        {
            string zipPath = Path2("dup.zip");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("dup.txt", Encoding.UTF8.GetBytes("v1"));
                zip.Save(zipPath);
            }

            string dest = Path2("out_dup");
            ZipSafe.SafeExtract(zipPath, dest, true);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(dest, "dup.txt")));

            // Re-extracting without overwrite must not clobber the existing file.
            Assert.ThrowsAny<IOException>(() => ZipSafe.SafeExtract(zipPath, dest, false));
            Assert.Equal("v1", File.ReadAllText(Path.Combine(dest, "dup.txt")));
        }
    }
}
