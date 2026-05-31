namespace Mcp35.Server.Process
{
    /// <summary>The outcome of a <see cref="ProcessRunner"/> invocation.</summary>
    public sealed class ProcessResult
    {
        public int ExitCode;
        public string StdOut;   // UTF-8 decoded
        public string StdErr;   // UTF-8 decoded
        public bool TimedOut;
    }
}
