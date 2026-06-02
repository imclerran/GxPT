using System;
using System.Diagnostics;

namespace GxPT
{
    // Detects whether a usable `git` is available to the host (and therefore to the git MCP server,
    // which inherits the host's environment). Used to default the git toggle ON when git is installed
    // and OFF when it isn't, and to avoid launching a git server that could only return errors.
    //
    // The probe runs `git --version` once and caches the result for the process — git being installed
    // doesn't change mid-session in practice (Invalidate() is provided for completeness, not wired to
    // any UI yet).
    internal static class GitProbe
    {
        private static readonly object _lock = new object();
        private static bool _done;
        private static bool _installed;

        public static bool IsInstalled()
        {
            lock (_lock)
            {
                if (_done) return _installed;
                _installed = Probe();
                _done = true;
                return _installed;
            }
        }

        public static void Invalidate()
        {
            lock (_lock) { _done = false; }
        }

        private static bool Probe()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "git";              // resolved via PATH, same as the server will use
                psi.Arguments = "--version";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    // Drain so the child can't block on a full pipe, then bound the wait.
                    try { p.StandardOutput.ReadToEnd(); } catch { }
                    try { p.StandardError.ReadToEnd(); } catch { }
                    if (!p.WaitForExit(3000))
                    {
                        try { if (!p.HasExited) p.Kill(); } catch { }
                        return false;
                    }
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                // Win32Exception (file not found), etc. -> git is not available.
                return false;
            }
        }
    }
}
