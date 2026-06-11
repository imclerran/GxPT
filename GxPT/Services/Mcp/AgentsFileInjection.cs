using System;
using System.IO;
using System.Text;

namespace GxPT
{
    // Host side of AGENTS.md support: reads the workspace root's AGENTS.md (the cross-tool
    // convention for per-project agent instructions) and wraps it with framing for injection as a
    // stable-head system message (McpChatOrchestrator.ProjectInstructions, Zone A of the prompt-
    // caching layout). Zone A rather than the ephemeral tail because the file can be large and
    // changes on a documentation cadence: cached it bills once per conversation, while Zone C
    // would re-bill it on every request. The host reads it once per send, so an on-disk edit
    // takes effect on the next turn at the cost of one cache prefix re-write - the same class of
    // event as a workspace change (docs/prompt-caching-design.md sec.6).
    internal static class AgentsFileInjection
    {
        public const string FileName = "AGENTS.md";

        // Cap the read so a runaway file can't dominate the prompt. More generous than the memory
        // index cap: this content lives in the cached prefix, billed once per conversation rather
        // than per request.
        private const int MaxChars = 32 * 1024;

        // Build the AGENTS.md system block for a workspace. Null when there is no workspace, no
        // AGENTS.md, or the file is empty/unreadable, so a project without one leaves no trace
        // in context.
        public static string Build(string workingDir)
        {
            string text = ReadFile(workingDir);
            if (string.IsNullOrEmpty(text)) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append("The workspace root contains an AGENTS.md file with project-specific ");
            sb.Append("instructions for agents. Follow these instructions while working in ");
            sb.Append("this workspace:\n\n");
            sb.Append(text);
            return sb.ToString();
        }

        private static string ReadFile(string workingDir)
        {
            try
            {
                if (string.IsNullOrEmpty(workingDir)) return null;
                string path = Path.Combine(workingDir, FileName);
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return null;
                if (text.Length > MaxChars)
                    text = text.Substring(0, MaxChars) + "\n[AGENTS.md truncated]";
                return text.TrimEnd('\n', '\r');
            }
            catch
            {
                return null;
            }
        }
    }
}
