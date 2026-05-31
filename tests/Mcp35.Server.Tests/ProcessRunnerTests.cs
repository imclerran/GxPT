using System;
using System.Text;
using Mcp35.Server.Process;
using Xunit;

namespace Mcp35.Server.Tests
{
    /// <summary>
    /// Exercises ProcessRunner against real OS commands. CI runs on windows-latest; the helpers
    /// pick cmd.exe vs /bin/sh so the suite is robust if run on either platform.
    /// </summary>
    public class ProcessRunnerTests
    {
        private static bool IsWindows
        {
            get { return Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX; }
        }

        private static ProcessRequest Shell(string command, int timeoutMs)
        {
            ProcessRequest req = new ProcessRequest();
            req.TimeoutMs = timeoutMs;
            if (IsWindows)
            {
                req.FileName = "cmd.exe";
                req.Arguments = "/c " + command;
            }
            else
            {
                req.FileName = "/bin/sh";
                req.Arguments = "-c \"" + command.Replace("\"", "\\\"") + "\"";
            }
            return req;
        }

        [Fact]
        public void Captures_stdout_and_zero_exit()
        {
            var runner = new ProcessRunner(null);
            var result = runner.Run(Shell("echo hello", 10000));

            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("hello", result.StdOut);
        }

        [Fact]
        public void Captures_nonzero_exit_code()
        {
            var runner = new ProcessRunner(null);
            // 'exit 3' on both shells.
            var result = runner.Run(Shell("exit 3", 10000));
            Assert.Equal(3, result.ExitCode);
        }

        [Fact]
        public void Captures_stderr_separately()
        {
            var runner = new ProcessRunner(null);
            string cmd = IsWindows ? "echo oops 1>&2" : "echo oops 1>&2";
            var result = runner.Run(Shell(cmd, 10000));
            Assert.Contains("oops", result.StdErr);
        }

        [Fact]
        public void Timeout_kills_and_flags()
        {
            var runner = new ProcessRunner(null);
            // Sleep well past the timeout: ping -n is a portable Windows delay; sleep on unix.
            string cmd = IsWindows ? "ping -n 10 127.0.0.1 >NUL" : "sleep 10";
            var result = runner.Run(Shell(cmd, 500));
            Assert.True(result.TimedOut);
        }

        [Fact]
        public void Large_output_does_not_deadlock()
        {
            var runner = new ProcessRunner(null);
            // Emit a lot of stdout; the separate drain thread must keep the pipe from blocking.
            string cmd;
            if (IsWindows)
                cmd = "for /L %i in (1,1,5000) do @echo line-%i";
            else
                cmd = "for i in $(seq 1 5000); do echo line-$i; done";

            var result = runner.Run(Shell(cmd, 30000));
            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("line-5000", result.StdOut);
            Assert.True(result.StdOut.Length > 10000);
        }

        [Fact]
        public void Stdin_text_is_passed_through()
        {
            var runner = new ProcessRunner(null);
            // 'sort' reads stdin on both platforms.
            ProcessRequest req = new ProcessRequest();
            req.TimeoutMs = 10000;
            req.StdinText = "banana\napple\ncherry\n";
            if (IsWindows) { req.FileName = "sort.exe"; req.Arguments = ""; }
            else { req.FileName = "/usr/bin/sort"; req.Arguments = ""; }

            var result = runner.Run(req);
            // apple should sort first regardless of platform line endings.
            Assert.StartsWith("apple", result.StdOut.TrimStart());
        }
    }
}
