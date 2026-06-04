using System;
using System.IO;
using System.Text;

namespace GxPT
{
    // Host side of the memory feature: reads the workspace's primary memory index
    // (<workdir>/.gxpt/memory.md) and wraps it with framing for injection as an ephemeral system
    // message (McpChatOrchestrator.MemorySystemMessageProvider). The MemoryMcpServer is the only
    // writer; this is read-only. All memory framing lives here so a disabled memory system leaves
    // no trace in context (design M5/M6, sec.5).
    internal static class MemoryInjection
    {
        public const string MemoryDirName = ".gxpt";
        public const string IndexFileName = "memory.md";

        // Cap the read so a hand-edited or runaway index can't bloat the prompt; the store keeps it
        // far smaller than this in practice.
        private const int MaxIndexChars = 16 * 1024;

        // Build the memory system block for a workspace, or null when there is no workspace.
        // Returns framing even when the index is empty, so the model knows the capability exists.
        public static string Build(string workingDir)
        {
            if (string.IsNullOrEmpty(workingDir)) return null;

            string index = ReadIndex(workingDir);

            StringBuilder sb = new StringBuilder();
            sb.Append("# Persistent project memory\n\n");
            sb.Append("You have a persistent memory for this workspace, stored under .gxpt/. Use it to ");
            sb.Append("record durable, reusable facts (conventions, architecture, decisions, gotchas) ");
            sb.Append("worth recalling in future conversations. Record a new fact with `remember`; read ");
            sb.Append("the detail behind an index line with `read_memory`; revise with `update_memory`; ");
            sb.Append("remove with `forget`; and merge related entries with `consolidate` to keep the ");
            sb.Append("index small. Prefer updating or consolidating over piling on near-duplicate ");
            sb.Append("entries. Do not mention this memory unless it is relevant.\n\n");

            if (string.IsNullOrEmpty(index))
            {
                sb.Append("Current memory index (.gxpt/memory.md): (empty - nothing recorded yet)");
            }
            else
            {
                sb.Append("Current memory index (.gxpt/memory.md):\n");
                sb.Append(index);
            }
            return sb.ToString();
        }

        private static string ReadIndex(string workingDir)
        {
            try
            {
                string path = Path.Combine(Path.Combine(workingDir, MemoryDirName), IndexFileName);
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return null;
                if (text.Length > MaxIndexChars)
                    text = text.Substring(0, MaxIndexChars) + "\n[index truncated]";
                return text.TrimEnd('\n', '\r');
            }
            catch
            {
                return null;
            }
        }
    }
}
