namespace GxPT
{
    // The host surface a slash command may consult while running. Kept deliberately small for v1
    // (prompt commands need only gating + the working directory for path validation); client commands
    // will widen this later with tab/model/settings access. Intentionally free of WinForms types so
    // the whole command core compiles into the unit-test assembly.
    internal interface ISlashCommandContext
    {
        // The conversation's working folder (may be null/empty when none is set). Path arguments are
        // always resolved relative to this root.
        string WorkingDir { get; }

        // True when an MCP server's toolset is actually available right now (enabled, connected, and
        // workdir-bound). Backed by McpToolRegistry.HasServer, so it reflects reality rather than the
        // settings toggle alone. Used to gate commands via Requires / RequiresAny.
        bool HasServer(string serverName);
    }
}
