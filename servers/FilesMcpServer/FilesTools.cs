using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace FilesMcpServer
{
    /// <summary>
    /// The Files server's tools, all confined to the sandbox root. read/list/search are ReadOnly,
    /// write/edit are Write(path-scoped), delete is Destructive(path-scoped) — host-gated; the
    /// server's job is safe construction (servers-spec §2). edit/search and read's line-range/
    /// numbering options are the agentic enhancements (range reads were flagged as a later add).
    /// </summary>
    internal static class FilesTools
    {
        // Caps (servers-spec §2).
        private const long MaxReadBytes = 1024 * 1024;       // 1 MiB
        private const int MaxListEntries = 1000;
        private const int MaxRecursiveDepth = 16;
        private const int BinarySniffBytes = 8000;

        // Search caps (servers-spec §2 — bounded blast radius for the model context).
        private const int DefaultSearchMax = 100;
        private const int MaxSearchMax = 1000;
        private const int MaxMatchLineLength = 1000;
        private const int MaxSearchScanFiles = 5000;

        public static void Register(McpServer server, FilesConfig config)
        {
            PathSandbox sandbox = new PathSandbox(config.WorkDir);

            server.AddTool("read", "Read the UTF-8 text contents of a file under the workspace root. "
                + "Optionally read a line range (1-based, inclusive) and/or prefix each line with its number.",
                SchemaBuilder.Object()
                    .Str("path", true, "Path relative to the workspace root")
                    .Int("start_line", false, "First line to return (1-based, inclusive)")
                    .Int("end_line", false, "Last line to return (1-based, inclusive)")
                    .Bool("line_numbers", false, "Prefix each returned line with its 1-based line number")
                    .Build(),
                delegate(ToolCallContext ctx) { return Read(sandbox, ctx); });

            server.AddTool("list", "List entries of a directory under the workspace root.",
                SchemaBuilder.Object()
                    .Str("path", true, "Directory path relative to the workspace root")
                    .Bool("recursive", false, "Recurse into subdirectories (bounded depth)")
                    .Build(),
                delegate(ToolCallContext ctx) { return List(sandbox, ctx); });

            server.AddTool("write", "Create or overwrite a text file under the workspace root.",
                SchemaBuilder.Object()
                    .Str("path", true, "Path relative to the workspace root")
                    .Str("content", true, "UTF-8 text content to write")
                    .Bool("create_dirs", false, "Create missing parent directories")
                    .Build(),
                delegate(ToolCallContext ctx) { return Write(sandbox, ctx); });

            server.AddTool("delete", "Delete a file or an empty directory under the workspace root.",
                SchemaBuilder.Object().Str("path", true, "Path relative to the workspace root").Build(),
                delegate(ToolCallContext ctx) { return Delete(sandbox, ctx); });

            server.AddTool("edit", "Replace an exact text span in a file under the workspace root. "
                + "Prefer this over write for large files: it is targeted and never rewrites the whole file. "
                + "old_string must match exactly and be unique unless replace_all is set.",
                SchemaBuilder.Object()
                    .Str("path", true, "Path relative to the workspace root")
                    .Str("old_string", true, "Exact text to find (must be unique unless replace_all is set)")
                    .Str("new_string", true, "Replacement text")
                    .Bool("replace_all", false, "Replace every occurrence instead of requiring a unique match")
                    .Build(),
                delegate(ToolCallContext ctx) { return Edit(sandbox, ctx); });

            server.AddTool("search", "Search file contents for a string or regex under the workspace root, "
                + "returning matching {path, line, text}. Recursive, streamed (any file size), skips binary files.",
                SchemaBuilder.Object()
                    .Str("query", true, "Text or regex to search for")
                    .Str("path", false, "Directory (or file) to search under; defaults to the workspace root")
                    .Bool("regex", false, "Treat query as a .NET regular expression (default: literal substring)")
                    .Bool("ignore_case", false, "Case-insensitive match")
                    .Str("glob", false, "Only search files whose name matches this wildcard (e.g. *.cs)")
                    .Int("max_results", false, "Maximum matches to return (default 100, max 1000)")
                    .Build(),
                delegate(ToolCallContext ctx) { return Search(sandbox, ctx); });
        }

        // ---- read ----

        private static CallToolResult Read(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (!File.Exists(full)) return ToolResults.Error("file not found");

            bool hasStart = HasArg(ctx, "start_line");
            bool hasEnd = HasArg(ctx, "end_line");
            bool lineNumbers = BoolArg(ctx, "line_numbers", false);

            // A line range streams, so a slice of a large file is readable even past the whole-file
            // cap (the output is bounded by the range, not the file size).
            if (hasStart || hasEnd)
                return ReadRange(full, ctx, hasStart, hasEnd, lineNumbers);

            // Whole-file read: bounded by the size cap (it all lands in the model context).
            FileInfo fi = new FileInfo(full);
            if (fi.Length > MaxReadBytes)
                return ToolResults.Error("file too large (" + fi.Length + " bytes; max " + MaxReadBytes
                    + ") — read a line range instead");

            byte[] bytes = File.ReadAllBytes(full);
            if (LooksBinary(bytes)) return ToolResults.Error("not a text file");
            string text = DecodeUtf8(bytes);

            // Fast path: whole file, no numbering -> return content verbatim (preserves exact bytes).
            if (!lineNumbers)
                return ToolResults.Text(text);

            string[] lines = SplitLines(text);
            int width = lines.Length.ToString().Length;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append((i + 1).ToString().PadLeft(width));
                sb.Append('\t');
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
            }
            return ToolResults.Text(sb.ToString());
        }

        // Stream a 1-based inclusive line range; output bytes are capped (not the file size), so a
        // slice of an arbitrarily large file is readable. start/end default to file bounds.
        private static CallToolResult ReadRange(string full, ToolCallContext ctx, bool hasStart, bool hasEnd, bool lineNumbers)
        {
            int start = hasStart ? IntArg(ctx, "start_line", 1, 1, int.MaxValue) : 1;
            int end = hasEnd ? IntArg(ctx, "end_line", int.MaxValue, 1, int.MaxValue) : int.MaxValue;
            if (end < start)
                return ToolResults.Error("end_line (" + end + ") is before start_line (" + start + ")");

            StreamReader sr;
            try { sr = OpenTextReaderOrNull(full); }
            catch { return ToolResults.Error("file not found"); }
            if (sr == null) return ToolResults.Error("not a text file");

            List<string> picked = new List<string>();
            int lineNo = 0;
            long outBytes = 0;
            using (sr)
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lineNo++;
                    if (lineNo < start) continue;
                    if (lineNo > end) break;
                    picked.Add(line);
                    outBytes += line.Length + 12; // rough per-line overhead (number + tab + newline)
                    if (outBytes > MaxReadBytes)
                        return ToolResults.Error("requested range is too large (over " + MaxReadBytes
                            + " bytes of text) — narrow start_line/end_line");
                }
            }

            if (picked.Count == 0)
                return ToolResults.Error("start_line " + start + " exceeds file length (" + lineNo + " lines)");

            int lastLineNo = start + picked.Count - 1;
            int width = lastLineNo.ToString().Length;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < picked.Count; i++)
            {
                if (lineNumbers)
                {
                    sb.Append((start + i).ToString().PadLeft(width));
                    sb.Append('\t');
                }
                sb.Append(picked[i]);
                if (i < picked.Count - 1) sb.Append('\n');
            }
            return ToolResults.Text(sb.ToString());
        }

        // ---- list ----

        private static CallToolResult List(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (!Directory.Exists(full)) return ToolResults.Error("directory not found");

            bool recursive = BoolArg(ctx, "recursive", false);

            List<JObject> entries = new List<JObject>();
            bool truncated = Collect(sandbox, full, recursive, recursive ? MaxRecursiveDepth : 0, entries);

            JObject result = new JObject();
            JArray arr = new JArray();
            foreach (JObject e in entries) arr.Add(e);
            result["entries"] = arr;
            result["count"] = entries.Count;
            result["truncated"] = truncated;
            return ToolResults.Json(result);
        }

        private static bool Collect(PathSandbox sandbox, string dir, bool recursive, int depthLeft, List<JObject> entries)
        {
            string[] dirs = Directory.GetDirectories(dir);
            string[] files = Directory.GetFiles(dir);

            foreach (string d in dirs)
            {
                if (entries.Count >= MaxListEntries) return true;
                entries.Add(Entry(sandbox, d, "dir", 0));
                if (recursive && depthLeft > 0)
                {
                    if (Collect(sandbox, d, true, depthLeft - 1, entries)) return true;
                }
            }
            foreach (string f in files)
            {
                if (entries.Count >= MaxListEntries) return true;
                long size = 0;
                try { size = new FileInfo(f).Length; }
                catch { }
                entries.Add(Entry(sandbox, f, "file", size));
            }
            return false;
        }

        private static JObject Entry(PathSandbox sandbox, string full, string type, long size)
        {
            JObject o = new JObject();
            o["name"] = sandbox.ToRelative(full);
            o["type"] = type;
            o["size"] = size;
            return o;
        }

        // ---- write ----

        private static CallToolResult Write(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (Directory.Exists(full)) return ToolResults.Error("path is a directory");

            string content = ctx.Arguments.Value<string>("content");
            if (content == null) return ToolResults.Error("content is required");

            bool createDirs = BoolArg(ctx, "create_dirs", false);
            string parent = Path.GetDirectoryName(full);
            if (!Directory.Exists(parent))
            {
                if (!createDirs) return ToolResults.Error("parent directory does not exist (set create_dirs to create it)");
                Directory.CreateDirectory(parent);
            }

            int bytesWritten = WriteAtomic(full, content);

            JObject result = new JObject();
            result["path"] = sandbox.ToRelative(full);
            result["bytesWritten"] = bytesWritten;
            return ToolResults.Json(result);
        }

        /// <summary>
        /// Atomic-ish write: temp file in the same dir, then replace (mirrors AppSettings'
        /// settings.json write — crash-safe). Returns the UTF-8 (no BOM) byte count written.
        /// </summary>
        private static int WriteAtomic(string full, string content)
        {
            string parent = Path.GetDirectoryName(full);
            string tmp = Path.Combine(parent, "." + Guid.NewGuid().ToString("N") + ".tmp");
            UTF8Encoding utf8NoBom = new UTF8Encoding(false);
            try
            {
                File.WriteAllText(tmp, content, utf8NoBom);
                if (File.Exists(full)) File.Delete(full);
                File.Move(tmp, full);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch { }
            }
            return utf8NoBom.GetByteCount(content);
        }

        // ---- edit ----

        private static CallToolResult Edit(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (Directory.Exists(full)) return ToolResults.Error("path is a directory");
            if (!File.Exists(full)) return ToolResults.Error("file not found");

            string oldString = ctx.Arguments.Value<string>("old_string");
            if (string.IsNullOrEmpty(oldString)) return ToolResults.Error("old_string is required");
            string newString = ctx.Arguments.Value<string>("new_string");
            if (newString == null) return ToolResults.Error("new_string is required");
            bool replaceAll = BoolArg(ctx, "replace_all", false);

            FileInfo fi = new FileInfo(full);
            if (fi.Length > MaxReadBytes)
                return ToolResults.Error("file too large (" + fi.Length + " bytes; max " + MaxReadBytes + ")");

            byte[] bytes = File.ReadAllBytes(full);
            if (LooksBinary(bytes)) return ToolResults.Error("not a text file");
            string text = DecodeUtf8(bytes);

            int count = CountOccurrences(text, oldString);
            if (count == 0) return ToolResults.Error("old_string not found in file");
            if (count > 1 && !replaceAll)
                return ToolResults.Error("old_string is not unique (" + count
                    + " occurrences); add surrounding context or set replace_all");

            string updated = replaceAll
                ? text.Replace(oldString, newString)
                : ReplaceFirst(text, oldString, newString);
            int replacements = replaceAll ? count : 1;

            int bytesWritten = WriteAtomic(full, updated);

            JObject result = new JObject();
            result["path"] = sandbox.ToRelative(full);
            result["replacements"] = replacements;
            result["bytesWritten"] = bytesWritten;
            return ToolResults.Json(result);
        }

        // ---- search ----

        private static CallToolResult Search(PathSandbox sandbox, ToolCallContext ctx)
        {
            string query = ctx.Arguments.Value<string>("query");
            if (string.IsNullOrEmpty(query)) return ToolResults.Error("query is required");

            string rel = ctx.Arguments.Value<string>("path");
            string root;
            if (string.IsNullOrEmpty(rel))
            {
                root = sandbox.Root;
            }
            else
            {
                try { root = sandbox.Resolve(rel); }
                catch (SandboxException ex) { return ToolResults.Error(ex.Message); }
            }
            if (!Directory.Exists(root) && !File.Exists(root))
                return ToolResults.Error("path not found");

            bool useRegex = BoolArg(ctx, "regex", false);
            bool ignoreCase = BoolArg(ctx, "ignore_case", false);
            int maxResults = IntArg(ctx, "max_results", DefaultSearchMax, 1, MaxSearchMax);
            string glob = ctx.Arguments.Value<string>("glob");

            Regex rx = null;
            if (useRegex)
            {
                try
                {
                    RegexOptions opts = RegexOptions.CultureInvariant;
                    if (ignoreCase) opts |= RegexOptions.IgnoreCase;
                    rx = new Regex(query, opts);
                }
                catch (ArgumentException ex)
                {
                    return ToolResults.Error("invalid regex: " + ex.Message);
                }
            }
            Regex globRx = string.IsNullOrEmpty(glob) ? null : GlobToRegex(glob);
            StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            List<JObject> matches = new List<JObject>();
            int[] scanned = new int[1];
            bool truncated = SearchWalk(sandbox, root, MaxRecursiveDepth, rx, query, cmp, globRx,
                maxResults, matches, scanned);

            JObject result = new JObject();
            JArray arr = new JArray();
            foreach (JObject m in matches) arr.Add(m);
            result["matches"] = arr;
            result["count"] = matches.Count;
            result["truncated"] = truncated;
            return ToolResults.Json(result);
        }

        // Returns true if results were truncated (hit a cap before scanning everything).
        private static bool SearchWalk(PathSandbox sandbox, string path, int depthLeft, Regex rx,
            string query, StringComparison cmp, Regex globRx, int maxResults,
            List<JObject> matches, int[] scanned)
        {
            if (File.Exists(path))
                return SearchFile(sandbox, path, rx, query, cmp, globRx, maxResults, matches, scanned);

            string[] files = Directory.GetFiles(path);
            foreach (string f in files)
            {
                if (SearchFile(sandbox, f, rx, query, cmp, globRx, maxResults, matches, scanned))
                    return true;
            }
            if (depthLeft > 0)
            {
                string[] dirs = Directory.GetDirectories(path);
                foreach (string d in dirs)
                {
                    if (SearchWalk(sandbox, d, depthLeft - 1, rx, query, cmp, globRx, maxResults, matches, scanned))
                        return true;
                }
            }
            return false;
        }

        private static bool SearchFile(PathSandbox sandbox, string file, Regex rx, string query,
            StringComparison cmp, Regex globRx, int maxResults, List<JObject> matches, int[] scanned)
        {
            if (globRx != null && !globRx.IsMatch(Path.GetFileName(file))) return false;

            if (scanned[0] >= MaxSearchScanFiles) return true; // scanned-file cap -> truncated
            scanned[0]++;

            // Stream line-by-line: file size is not a limit (output is bounded by maxResults), so
            // large files — exactly where grep matters most — are searchable. Binary/unreadable skip.
            StreamReader sr;
            try { sr = OpenTextReaderOrNull(file); }
            catch { return false; } // unreadable -> skip silently
            if (sr == null) return false; // binary -> skip silently

            string relPath = sandbox.ToRelative(file);
            using (sr)
            {
                int lineNo = 0;
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lineNo++;
                    bool hit = rx != null ? rx.IsMatch(line) : line.IndexOf(query, cmp) >= 0;
                    if (!hit) continue;

                    JObject m = new JObject();
                    m["path"] = relPath;
                    m["line"] = lineNo;
                    m["text"] = CapText(line);
                    matches.Add(m);
                    if (matches.Count >= maxResults) return true; // result cap -> truncated
                }
            }
            return false;
        }

        // ---- delete ----

        private static CallToolResult Delete(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (Directory.Exists(full))
            {
                // Empty directories only — never recursive (bounded blast radius, §2).
                if (Directory.GetFileSystemEntries(full).Length > 0)
                    return ToolResults.Error("directory is not empty");
                Directory.Delete(full, false);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }
            else
            {
                return ToolResults.Error("path not found");
            }

            JObject result = new JObject();
            result["deleted"] = sandbox.ToRelative(full);
            return ToolResults.Json(result);
        }

        // ---- helpers ----

        private static CallToolResult ResolvePath(PathSandbox sandbox, ToolCallContext ctx, out string full)
        {
            full = null;
            string rel = ctx.Arguments.Value<string>("path");
            try
            {
                full = sandbox.Resolve(rel);
                return null;
            }
            catch (SandboxException ex)
            {
                return ToolResults.Error(ex.Message);
            }
        }

        private static bool BoolArg(ToolCallContext ctx, string name, bool fallback)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try { return t.Value<bool>(); }
            catch { return fallback; }
        }

        private static bool HasArg(ToolCallContext ctx, string name)
        {
            JToken t = ctx.Arguments[name];
            return t != null && t.Type != JTokenType.Null;
        }

        private static int IntArg(ToolCallContext ctx, string name, int fallback, int min, int max)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int n;
            try { n = t.Value<int>(); }
            catch { return fallback; }
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        // Split into lines on \n, \r\n, or \r, dropping the separators. A trailing newline does
        // NOT produce a spurious empty final line (so a 3-line file reads as 3 lines).
        private static string[] SplitLines(string text)
        {
            if (text.Length == 0) return new string[] { string.Empty };
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (normalized.Length > 0 && normalized[normalized.Length - 1] == '\n')
                normalized = normalized.Substring(0, normalized.Length - 1);
            return normalized.Split('\n');
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        private static string ReplaceFirst(string text, string oldValue, string newValue)
        {
            int idx = text.IndexOf(oldValue, StringComparison.Ordinal);
            if (idx < 0) return text;
            return text.Substring(0, idx) + newValue + text.Substring(idx + oldValue.Length);
        }

        private static string CapText(string s)
        {
            if (s.Length <= MaxMatchLineLength) return s;
            return s.Substring(0, MaxMatchLineLength);
        }

        // Translate a simple filename wildcard (* and ?) into an anchored, case-insensitive regex.
        private static Regex GlobToRegex(string glob)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('^');
            foreach (char c in glob)
            {
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool LooksBinary(byte[] bytes)
        {
            return LooksBinary(bytes, bytes.Length);
        }

        private static bool LooksBinary(byte[] bytes, int count)
        {
            int n = Math.Min(count, BinarySniffBytes);
            for (int i = 0; i < n; i++)
                if (bytes[i] == 0) return true; // NUL byte → treat as binary
            return false;
        }

        /// <summary>
        /// Open a file as BOM-aware UTF-8 text lines after sniffing its head for a NUL byte. Returns
        /// <c>null</c> (stream disposed) if the file looks binary. Streaming lets search and ranged
        /// read work on files larger than the whole-file cap — output is bounded by matches / range,
        /// not file size. Caller owns the returned reader (wrap in <c>using</c>).
        /// </summary>
        private static StreamReader OpenTextReaderOrNull(string file)
        {
            FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                byte[] head = new byte[BinarySniffBytes];
                int n = fs.Read(head, 0, head.Length);
                if (LooksBinary(head, n)) { fs.Dispose(); return null; }
                fs.Position = 0;
                return new StreamReader(fs, new UTF8Encoding(false, false), true);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        private static string DecodeUtf8(byte[] bytes)
        {
            // BOM-aware: strip a leading UTF-8 BOM if present.
            int start = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                start = 3;
            UTF8Encoding utf8 = new UTF8Encoding(false, false);
            return utf8.GetString(bytes, start, bytes.Length - start);
        }
    }
}
