using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
// no reflection; patterns can be injected from callers

namespace GxPT
{
    internal sealed class TextFileAttachmentExtractor : IAttachmentExtractor
    {
        private readonly object _lock = new object();
        private List<string> _cachedPatterns; // instance cache (deduped)
        private List<string> _additionalPatterns; // optional injected patterns

        public TextFileAttachmentExtractor()
            : this(null) { }

        public TextFileAttachmentExtractor(IEnumerable<string> additionalPatterns)
        {
            if (additionalPatterns != null)
            {
                _additionalPatterns = new List<string>();
                foreach (var p in additionalPatterns)
                {
                    if (!string.IsNullOrEmpty(p)) _additionalPatterns.Add(p);
                }
            }
        }

        public void SetAdditionalPatterns(IEnumerable<string> patterns)
        {
            lock (_lock)
            {
                _additionalPatterns = null;
                if (patterns != null)
                {
                    _additionalPatterns = new List<string>();
                    foreach (var p in patterns)
                    {
                        if (!string.IsNullOrEmpty(p)) _additionalPatterns.Add(p);
                    }
                }
                _cachedPatterns = null; // invalidate cache
            }
        }

        // Guard against binary by sampling bytes (mirrors previous logic)
        private static bool LooksLikeText(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Length > 10 * 1024 * 1024) return false; // 10 MB
                int sampleSize = (int)Math.Min(8192, fi.Length);
                if (sampleSize <= 0) return true; // empty OK
                byte[] buffer = new byte[sampleSize];
                int read = 0;
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    read = fs.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return true;
                }

                // Fast-path: recognize common Unicode BOMs as definitely text
                // UTF-32 LE: FF FE 00 00, UTF-32 BE: 00 00 FE FF
                if (read >= 4)
                {
                    if ((buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00) ||
                        (buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF))
                    {
                        return true;
                    }
                }
                // UTF-8 BOM: EF BB BF
                if (read >= 3)
                {
                    if (buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                        return true;
                }
                // UTF-16 LE: FF FE, UTF-16 BE: FE FF
                if (read >= 2)
                {
                    if ((buffer[0] == 0xFF && buffer[1] == 0xFE) || (buffer[0] == 0xFE && buffer[1] == 0xFF))
                        return true;
                }

                int nulls = 0, ctrls = 0;
                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    if (b == 0) nulls++;
                    else if (b < 32 && b != 9 && b != 10 && b != 13) ctrls++;
                }
                double nratio = (double)nulls / read;
                double cratio = (double)ctrls / read;
                return nratio < 0.01 && cratio < 0.02;
            }
            catch { return false; }
        }

        public bool CanHandle(string filePath)
        {
            try { return LooksLikeText(filePath); }
            catch { return false; }
        }

        public AttachedFile Extract(string filePath)
        {
            string name = Path.GetFileName(filePath);
            string content = string.Empty;
            using (var sr = new StreamReader(filePath, Encoding.UTF8, true))
            {
                content = sr.ReadToEnd();
            }
            return new AttachedFile(name, content);
        }

        public IList<string> GetFileDialogPatterns()
        {
            // Build once and cache: base patterns + any injected patterns (no reflection)
            if (_cachedPatterns != null) return _cachedPatterns;
            lock (_lock)
            {
                if (_cachedPatterns != null) return _cachedPatterns;
                var list = new List<string>();

                // Base/common text patterns
                list.AddRange(new[]
                {
                    "*.txt","*.md","*.markdown","*.log","*.toml","*.gitignore","*.dockerfile","*.cmake","*.diff","*.patch","*.csv","*.sln",
                    // common names without extension
                    "Dockerfile","Dockerfile.*","Makefile","makefile","GNUmakefile","Makefile.*","makefile.*"
                });

                // Add any injected patterns (from MainForm or caller)
                if (_additionalPatterns != null && _additionalPatterns.Count > 0)
                {
                    list.AddRange(_additionalPatterns);
                }

                // Deduplicate (case-insensitive) and materialize
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var deduped = new List<string>(list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (string.IsNullOrEmpty(p)) continue;
                    if (seen.Contains(p)) continue;
                    seen.Add(p);
                    deduped.Add(p);
                }
                _cachedPatterns = deduped;
                return _cachedPatterns;
            }
        }

        public string GetCategoryLabel()
        {
            return "Text Files";
        }
    }
}
