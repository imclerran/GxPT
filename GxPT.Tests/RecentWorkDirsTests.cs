using System;
using System.Collections.Generic;
using System.IO;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class RecentWorkDirsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _file;

        public RecentWorkDirsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_recentdirs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _file = Path.Combine(_root, "recent-workdirs.json");
            RecentWorkDirs.FilePathOverride = _file;
        }

        public void Dispose()
        {
            RecentWorkDirs.FilePathOverride = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void Get_OnMissingFile_ReturnsEmpty()
        {
            List<string> list = RecentWorkDirs.Get();
            Assert.Empty(list);
        }

        [Fact]
        public void Add_ThenGet_ReturnsThePath()
        {
            RecentWorkDirs.Add(_root);
            List<string> list = RecentWorkDirs.Get();
            Assert.Single(list);
            Assert.Equal(_root, list[0]);
        }

        [Fact]
        public void Add_MostRecentFirst()
        {
            string a = Path.Combine(_root, "a"); Directory.CreateDirectory(a);
            string b = Path.Combine(_root, "b"); Directory.CreateDirectory(b);
            RecentWorkDirs.Add(a);
            RecentWorkDirs.Add(b);
            List<string> list = RecentWorkDirs.Get();
            Assert.Equal(2, list.Count);
            Assert.Equal(b, list[0]);
            Assert.Equal(a, list[1]);
        }

        [Fact]
        public void Add_DeduplicatesCaseInsensitiveAndTrailingSlash_MovesToFront()
        {
            string a = Path.Combine(_root, "a"); Directory.CreateDirectory(a);
            string b = Path.Combine(_root, "b"); Directory.CreateDirectory(b);
            RecentWorkDirs.Add(a);
            RecentWorkDirs.Add(b);
            // Re-add 'a' with a trailing slash and upper-cased — must collapse to one entry, now first.
            RecentWorkDirs.Add(a.ToUpperInvariant() + Path.DirectorySeparatorChar);
            List<string> list = RecentWorkDirs.Get();
            Assert.Equal(2, list.Count);
            Assert.Equal(a, list[0], StringComparer.OrdinalIgnoreCase);
            Assert.Equal(b, list[1], StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Add_CapsAtFiveAndDropsOldest()
        {
            string[] dirs = new string[7];
            for (int i = 0; i < 7; i++)
            {
                dirs[i] = Path.Combine(_root, "d" + i);
                Directory.CreateDirectory(dirs[i]);
                RecentWorkDirs.Add(dirs[i]);
            }
            List<string> list = RecentWorkDirs.Get();
            Assert.Equal(RecentWorkDirs.MaxEntries, list.Count);
            Assert.Equal(dirs[6], list[0]); // newest first
            Assert.Equal(dirs[2], list[4]); // d0, d1 dropped
        }

        [Fact]
        public void Add_NullOrEmpty_IsIgnored()
        {
            RecentWorkDirs.Add(null);
            RecentWorkDirs.Add("");
            RecentWorkDirs.Add("   ");
            Assert.Empty(RecentWorkDirs.Get());
        }

        [Fact]
        public void List_RoundTripsAcrossCalls()
        {
            RecentWorkDirs.Add(_root);
            // A second independent Get reads back from disk.
            List<string> list = RecentWorkDirs.Get();
            Assert.Single(list);
            Assert.Equal(_root, list[0]);
        }
    }
}
