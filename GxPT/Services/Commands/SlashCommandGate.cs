namespace GxPT
{
    // Server-availability gating, shared by the processor (to block execution) and the autocomplete
    // popup (to grey out / annotate unavailable commands). A command is available when every server in
    // Requires is present AND, if RequiresAny is non-empty, at least one of those servers is present.
    internal static class SlashCommandGate
    {
        public static bool IsAvailable(ISlashCommand cmd, ISlashCommandContext ctx)
        {
            return UnavailableReason(cmd, ctx) == null;
        }

        // Returns null when available; otherwise a short user-facing reason naming a missing server.
        public static string UnavailableReason(ISlashCommand cmd, ISlashCommandContext ctx)
        {
            if (cmd == null) return null;
            if (ctx == null) return null;

            var requires = cmd.Requires;
            if (requires != null)
            {
                for (int i = 0; i < requires.Count; i++)
                {
                    string name = requires[i];
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!ctx.HasServer(name))
                        return "needs the " + name + " server";
                }
            }

            var any = cmd.RequiresAny;
            if (any != null && any.Count > 0)
            {
                bool satisfied = false;
                for (int i = 0; i < any.Count; i++)
                {
                    string name = any[i];
                    if (string.IsNullOrEmpty(name)) continue;
                    if (ctx.HasServer(name)) { satisfied = true; break; }
                }
                if (!satisfied)
                    return "needs the " + JoinOr(any) + " server";
            }

            return null;
        }

        private static string JoinOr(System.Collections.Generic.IList<string> names)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (int i = 0; i < names.Count; i++)
            {
                if (!string.IsNullOrEmpty(names[i])) parts.Add(names[i]);
            }
            if (parts.Count == 0) return string.Empty;
            if (parts.Count == 1) return parts[0];
            string head = string.Join(", ", parts.GetRange(0, parts.Count - 1).ToArray());
            return head + " or " + parts[parts.Count - 1];
        }
    }
}
