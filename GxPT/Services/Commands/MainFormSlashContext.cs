using System.Collections.Generic;

namespace GxPT
{
    // ISlashCommandContext implementation backed by MainForm. It forwards to MainForm's internal Slash*
    // helpers (which read live state -- active tab, model combo, MCP registry/host), so commands never
    // hold stale references and stay decoupled from MainForm itself.
    internal sealed class MainFormSlashContext : ISlashCommandContext
    {
        private readonly MainForm _form;

        public MainFormSlashContext(MainForm form) { _form = form; }

        public string WorkingDir { get { return _form.SlashWorkingDir(); } }
        public bool HasServer(string serverName) { return _form.SlashHasServer(serverName); }
        public void WriteInfo(string text) { _form.SlashWriteInfo(text); }

        public IList<string> GetModels() { return _form.SlashGetModels(); }
        public string GetActiveModel() { return _form.SlashGetActiveModel(); }
        public void SetModel(string slug) { _form.SlashSetModel(slug); }

        public IList<string> GetServerNames() { return _form.SlashGetServerNames(); }
        public bool GetServerEnabled(string serverName) { return _form.SlashGetServerEnabled(serverName); }
        public string SetServerEnabled(string serverName, bool enabled) { return _form.SlashSetServerEnabled(serverName, enabled); }

        public void NewConversation() { _form.SlashNewConversation(); }
        public void ExportConversations() { _form.SlashExportConversations(); }
    }
}
