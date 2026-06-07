using System;
using System.Collections.Generic;
using GxPT;

namespace GxPT.Tests.Commands
{
    // Minimal ISlashCommandContext for exercising gating and path validation without the host.
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
    }
}
