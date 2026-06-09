using System.Collections.Generic;

namespace GxPT
{
    // The host surface a slash command may consult or drive. Kept free of WinForms types so the command
    // core compiles into the unit-test assembly; the app provides a MainForm-backed implementation and
    // tests provide a fake. Prompt commands use only WorkingDir/HasServer; client commands use the rest.
    internal interface ISlashCommandContext
    {
        // The conversation's working folder (may be null/empty when none is set). Path arguments are
        // always resolved relative to this root.
        string WorkingDir { get; }

        // True when an MCP server's toolset is actually available right now (enabled, connected, and
        // workdir-bound). Backed by McpToolRegistry.HasServer.
        bool HasServer(string serverName);

        // Show a short status line in the active transcript (UI only; not saved to history).
        void WriteInfo(string text);

        // ---- model control ----
        IList<string> GetModels();      // known "author/model" slugs (for completion)
        string GetActiveModel();        // the active tab's current model
        void SetModel(string slug);     // switch the active tab's model

        // ---- MCP server control (built-in toggleable servers) ----
        IList<string> GetServerNames();          // names that can be toggled
        bool GetServerEnabled(string serverName); // effective current state
        // Apply a new enabled state. Returns null on success, or a message explaining why it didn't
        // change (e.g. the tool isn't installed).
        string SetServerEnabled(string serverName, bool enabled);

        // ---- skills: per-conversation enablement overrides (tri-state; null = inherit the global
        // default in skills.json). Global-scope changes go straight to SkillEnablement, not through here.
        bool? GetConversationSkillsFeatureOff();            // null = inherit, true = off, false = on
        void SetConversationSkillsFeatureOff(bool? value);  // persists the active conversation
        IDictionary<string, bool> GetConversationSkillOverrides(); // copy; slug -> force on/off
        void SetConversationSkillOverride(string slug, bool? value); // null clears the slug; persists
        void ResetConversationSkills();                     // clear feature override + all per-skill overrides

        // Bring the Skills MCP server into line with skill enablement (it runs iff any skill is enabled).
        // Call after any skills enablement change - global or per-conversation. A no-op unless the change
        // crosses the on/off boundary.
        void RefreshSkillsServer();

        // ---- conversation / app actions ----
        void NewConversation();
        void ExportConversations();

        // Summarize the current conversation and open the summary as context in a new conversation tab.
        // Runs asynchronously; the original conversation is left untouched.
        void Compact();
    }
}
