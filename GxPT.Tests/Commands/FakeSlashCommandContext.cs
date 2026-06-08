using System;
using System.Collections.Generic;
using GxPT;

namespace GxPT.Tests.Commands
{
    // ISlashCommandContext for tests. The HasServer/WorkingDir bits drive prompt-command gating tests;
    // the model/server collections and recorded actions drive client-command tests.
    internal sealed class FakeSlashCommandContext : ISlashCommandContext
    {
        private readonly HashSet<string> _servers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public FakeSlashCommandContext(string workingDir, params string[] servers)
        {
            WorkingDir = workingDir;
            if (servers != null)
            {
                for (int i = 0; i < servers.Length; i++)
                    if (!string.IsNullOrEmpty(servers[i])) _servers.Add(servers[i]);
            }
        }

        public string WorkingDir { get; private set; }

        public bool HasServer(string serverName)
        {
            return !string.IsNullOrEmpty(serverName) && _servers.Contains(serverName);
        }

        // ---- configurable state / recorded actions for client-command tests ----
        public List<string> Models = new List<string>();
        public Dictionary<string, bool> ServerStates =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public List<string> Infos = new List<string>();
        public string LastModelSet;
        public int NewConversationCount;
        public int ExportCount;

        public void WriteInfo(string text) { Infos.Add(text); }

        public IList<string> GetModels() { return Models; }
        public string GetActiveModel() { return LastModelSet; }
        public void SetModel(string slug) { LastModelSet = slug; }

        public IList<string> GetServerNames() { return new List<string>(ServerStates.Keys); }
        public bool GetServerEnabled(string serverName)
        {
            bool v;
            return ServerStates.TryGetValue(serverName, out v) && v;
        }
        public string SetServerEnabled(string serverName, bool enabled)
        {
            ServerStates[serverName] = enabled;
            return null;
        }

        public void NewConversation() { NewConversationCount++; }
        public void ExportConversations() { ExportCount++; }
    }
}
