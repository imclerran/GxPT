using System;
using System.IO;

namespace GxPT
{
    // Detects whether any MSBuild engine is available to the host (and therefore to the MSBuild MCP
    // server, which discovers engines the same way). Used to default the MSBuild toggle ON when a
    // build engine exists and OFF when none does, and to avoid launching a server that would expose
    // no build tools.
    //
    // This is a cheap boolean probe (well-known path checks only); the server itself does the full
    // version/bitness enumeration. The result is cached for the process — installed toolsets don't
    // appear mid-session in practice (Invalidate() is provided for completeness).
    internal static class MsBuildProbe
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
                // .NET Framework MSBuild (bundled from Windows XP onward) — the common case.
                string windir = Environment.GetEnvironmentVariable("WINDIR");
                if (string.IsNullOrEmpty(windir)) windir = Environment.GetEnvironmentVariable("SystemRoot");
                if (!string.IsNullOrEmpty(windir))
                {
                    string[] subs = new string[] { "v2.0.50727", "v3.5", "v4.0.30319" };
                    string[] roots = new string[] { "Framework", "Framework64" };
                    foreach (string root in roots)
                        foreach (string sub in subs)
                            if (FileExists(Path.Combine(Path.Combine(Path.Combine(Path.Combine(windir, "Microsoft.NET"), root), sub), "MSBuild.exe")))
                                return true;
                }

                string pf = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                if (string.IsNullOrEmpty(pf)) pf = Environment.GetEnvironmentVariable("ProgramFiles");
                if (!string.IsNullOrEmpty(pf))
                {
                    // Standalone MSBuild (VS2013/2015).
                    string[] vers = new string[] { "12.0", "14.0" };
                    foreach (string v in vers)
                        if (FileExists(Path.Combine(Path.Combine(Path.Combine(Path.Combine(pf, "MSBuild"), v), "Bin"), "MSBuild.exe")))
                            return true;

                    // VS2017+ : vswhere's presence implies a VS installer; the server enumerates the rest.
                    if (FileExists(Path.Combine(Path.Combine(Path.Combine(pf, "Microsoft Visual Studio"), "Installer"), "vswhere.exe")))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool FileExists(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
            catch { return false; }
        }
    }
}
