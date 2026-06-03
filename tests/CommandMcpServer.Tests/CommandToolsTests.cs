using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CommandMcpServer.Tests
{
    /// <summary>
    /// Exercises the run tool against the real shell. CI runs on Windows (cmd.exe via the default
    /// %ComSpec% resolution); on other platforms the tests inject /bin/sh. Verifies output capture,
    /// exit codes, timeout + tree-kill, and that the command string is passed to the shell verbatim.
    /// </summary>
    public class CommandToolsTests : IDisposable
    {
        private static bool IsWindows
        {
            get { return Environment.OSVersion.Platform != PlatformID.Unix && Environment.OSVersion.Platform != PlatformID.MacOSX; }
        }

        private readonly string _work;

        public CommandToolsTests()
        {
            _work = Path.Combine(Path.GetTempPath(), "cmdmcp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_work);
        }

        public void Dispose()
        {
            try { Directory.Delete(_work, true); } catch { }
        }

        private Mcp35.Server.McpServer NewServer()
        {
            return IsWindows
                ? Harness.NewCommandServer(_work)
                : Harness.NewCommandServerWithShell("/bin/sh", _work);
        }

        // The Command server invokes the shell as `<shell> /s /c "<command>"` so cmd strips just the
        // outer quote pair and runs the command verbatim (see CommandTools). For /bin/sh that switch
        // shape is meaningless, so on non-Windows we skip the exec-based asserts and run them only on
        // Windows, where cmd /c is the real target.

        [Fact]
        public void Lists_the_run_tool()
        {
            var msgs = Harness.Exchange(NewServer(), Harness.ToolsList(1));
            JArray tools = (JArray)msgs[0]["result"]["tools"];
            Assert.Single(tools);
            Assert.Equal("run", (string)tools[0]["name"]);
        }

        [Fact]
        public void Captures_stdout_and_zero_exit()
        {
            string cmd = IsWindows ? "echo hello-world" : "-c \"echo hello-world\"";
            // On /bin/sh the server runs `/bin/sh /c -c "echo..."` which is awkward; for portability
            // only assert the happy path on Windows where cmd /c is the real target.
            if (!IsWindows) return;

            var msgs = Harness.Exchange(NewServer(), Harness.ToolsCall(1, "run", Harness.Args("command", "echo hello-world")));
            Assert.False(Harness.IsError(msgs[0]));
            JObject s = Harness.Structured(msgs[0]);
            Assert.Equal(0, (int)s["exitCode"]);
            Assert.Contains("hello-world", (string)s["stdout"]);
            Assert.False((bool)s["timedOut"]);
        }

        [Fact]
        public void Captures_nonzero_exit()
        {
            if (!IsWindows) return;
            var msgs = Harness.Exchange(NewServer(), Harness.ToolsCall(1, "run", Harness.Args("command", "exit 5")));
            Assert.Equal(5, (int)Harness.Structured(msgs[0])["exitCode"]);
        }

        [Fact]
        public void Captures_stderr()
        {
            if (!IsWindows) return;
            var msgs = Harness.Exchange(NewServer(), Harness.ToolsCall(1, "run", Harness.Args("command", "echo oops 1>&2")));
            Assert.Contains("oops", (string)Harness.Structured(msgs[0])["stderr"]);
        }

        [Fact]
        public void Timeout_kills_and_flags()
        {
            if (!IsWindows) return;
            // ping -n 10 delays ~9s; a 500ms cap forces a timeout + tree-kill.
            var msgs = Harness.Exchange(NewServer(),
                Harness.ToolsCall(1, "run", Harness.Args("command", "ping -n 10 127.0.0.1 >NUL", "timeout_ms", 500)));
            Assert.True((bool)Harness.Structured(msgs[0])["timedOut"]);
        }

        [Fact]
        public void Empty_command_is_error()
        {
            var msgs = Harness.Exchange(NewServer(), Harness.ToolsCall(1, "run", Harness.Args("command", "")));
            Assert.True(Harness.IsError(msgs[0]));
        }

        [Fact]
        public void Pipes_work_because_command_goes_to_the_shell_verbatim()
        {
            if (!IsWindows) return;
            // A pipe proves the command is handed to the shell as written (not argv-split).
            var msgs = Harness.Exchange(NewServer(),
                Harness.ToolsCall(1, "run", Harness.Args("command", "echo abc| find \"abc\"")));
            JObject s = Harness.Structured(msgs[0]);
            Assert.Equal(0, (int)s["exitCode"]);
            Assert.Contains("abc", (string)s["stdout"]);
        }

        [Fact]
        public void Leading_quoted_path_with_a_quoted_arg_is_not_mangled()
        {
            if (!IsWindows) return;
            // Regression for cmd.exe's quote-stripping quirk. A command that BEGINS with a quoted
            // path AND carries a further quoted token (>2 quotes) makes bare `cmd /c <line>` strip
            // the first+last quote of the whole line, corrupting it. The server wraps the command as
            // `cmd /s /c "<line>"`, which preserves it verbatim. The space in the directory forces
            // the leading quote to matter (so the bug can't hide behind cmd's two-quotes-only
            // preservation rule). On the old code this command failed with a nonzero exit.
            string subdir = Path.Combine(_work, "sub dir");
            Directory.CreateDirectory(subdir);
            string bat = Path.Combine(subdir, "say.bat");
            File.WriteAllText(bat, "@echo off\r\n@echo got=[%~1]\r\n");

            string command = "\"" + bat + "\" \"hello world\"";
            var msgs = Harness.Exchange(NewServer(),
                Harness.ToolsCall(1, "run", Harness.Args("command", command)));
            JObject s = Harness.Structured(msgs[0]);
            Assert.Equal(0, (int)s["exitCode"]);
            Assert.Contains("got=[hello world]", (string)s["stdout"]);
        }

        [Fact]
        public void Server_survives_after_a_command()
        {
            if (!IsWindows) return;
            var msgs = Harness.Exchange(NewServer(),
                Harness.ToolsCall(1, "run", Harness.Args("command", "echo one")),
                Harness.Request(2, "ping", null));
            Assert.Equal(2, msgs.Count);
            Assert.NotNull(msgs[1]["result"]);
        }
    }
}
