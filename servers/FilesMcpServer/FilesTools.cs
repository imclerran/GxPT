using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace FilesMcpServer
{
    /// <summary>
    /// The Files server's four tools, all confined to the sandbox root. read/list are ReadOnly,
    /// write is Write(path-scoped), delete is Destructive(path-scoped) — host-gated; the server's
    /// job is safe construction (servers-spec §2).
    /// </summary>
    internal static class FilesTools
    {
        // Caps (servers-spec §2).
        private const long MaxReadBytes = 1024 * 1024;       // 1 MiB
        private const int MaxListEntries = 1000;
        private const int MaxRecursiveDepth = 16;
        private const int BinarySniffBytes = 8000;

        public static void Register(McpServer server, FilesConfig config)
        {
            PathSandbox sandbox = new PathSandbox(config.WorkDir);

            server.AddTool("read", "Read the UTF-8 text contents of a file under the workspace root.",
                SchemaBuilder.Object().Str("path", true, "Path relative to the workspace root").Build(),
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
        }

        // ---- read ----

        private static CallToolResult Read(PathSandbox sandbox, ToolCallContext ctx)
        {
            string full;
            CallToolResult err = ResolvePath(sandbox, ctx, out full);
            if (err != null) return err;

            if (!File.Exists(full)) return ToolResults.Error("file not found");

            FileInfo fi = new FileInfo(full);
            if (fi.Length > MaxReadBytes)
                return ToolResults.Error("file too large (" + fi.Length + " bytes; max " + MaxReadBytes + ")");

            byte[] bytes = File.ReadAllBytes(full);
            if (LooksBinary(bytes)) return ToolResults.Error("not a text file");

            return ToolResults.Text(DecodeUtf8(bytes));
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

            // Atomic-ish write: temp file in the same dir, then replace (mirrors AppSettings).
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

            JObject result = new JObject();
            result["path"] = sandbox.ToRelative(full);
            result["bytesWritten"] = utf8NoBom.GetByteCount(content);
            return ToolResults.Json(result);
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

        private static bool LooksBinary(byte[] bytes)
        {
            int n = Math.Min(bytes.Length, BinarySniffBytes);
            for (int i = 0; i < n; i++)
                if (bytes[i] == 0) return true; // NUL byte → treat as binary
            return false;
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
