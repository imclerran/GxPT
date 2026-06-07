using System;

namespace GxPT
{
    // Host-side ISlashCommandContext that reads live state through delegates, so it never captures a
    // stale working directory or a recreated McpToolRegistry (the registry is rebuilt when the working
    // folder changes). Lives in the app project only -- the test core uses FakeSlashCommandContext.
    internal sealed class DelegateSlashCommandContext : ISlashCommandContext
    {
        private readonly Func<string> _workingDir;
        private readonly Func<string, bool> _hasServer;

        public DelegateSlashCommandContext(Func<string> workingDir, Func<string, bool> hasServer)
        {
            _workingDir = workingDir;
            _hasServer = hasServer;
        }

        public string WorkingDir
        {
            get { return _workingDir != null ? _workingDir() : null; }
        }

        public bool HasServer(string serverName)
        {
            return _hasServer != null && _hasServer(serverName);
        }
    }
}
