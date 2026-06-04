using System;
using System.Collections.Generic;
using System.IO;
using Mcp35.Core.Diagnostics;
using Mcp35.Server.Process;
using Newtonsoft.Json.Linq;

namespace MSBuildMcpServer
{
    /// <summary>
    /// One discovered MSBuild version. A version may ship a 32-bit engine, a 64-bit engine, or both;
    /// <see cref="X86Path"/>/<see cref="X64Path"/> is null when that bitness isn't present. The engine
    /// bitness is the bitness of the MSBuild *process* — the architecture of the build *output* is
    /// controlled by the project's Platform/PlatformTarget, not by which engine runs.
    /// </summary>
    internal sealed class MsBuildInstall
    {
        public string Version;   // "2.0", "4.0", "17.0" — the MSBuild/toolset version
        public string Label;     // human label, e.g. "MSBuild 4.0 (.NET Framework)"
        public string X86Path;   // 32-bit MSBuild.exe, or null
        public string X64Path;   // 64-bit MSBuild.exe, or null

        public bool HasBoth { get { return !string.IsNullOrEmpty(X86Path) && !string.IsNullOrEmpty(X64Path); } }

        // The engine to use for a requested bitness; falls back to whichever exists.
        public string PathForBitness(string bitness)
        {
            bool wantX64 = string.Equals(bitness, "x64", StringComparison.OrdinalIgnoreCase);
            bool wantX86 = string.Equals(bitness, "x86", StringComparison.OrdinalIgnoreCase);
            if (wantX64 && !string.IsNullOrEmpty(X64Path)) return X64Path;
            if (wantX86 && !string.IsNullOrEmpty(X86Path)) return X86Path;
            // Default: prefer the 64-bit engine when present (only true on a 64-bit OS), else 32-bit.
            return !string.IsNullOrEmpty(X64Path) ? X64Path : X86Path;
        }
    }

    /// <summary>
    /// Probes the system for installed MSBuild engines. Covers the whole XP-32 → Win11-64 range:
    ///   - .NET Framework MSBuild bundled with the OS (v2.0/v3.5/v4.0, under %WINDIR%\Microsoft.NET);
    ///   - standalone MSBuild from VS2013/2015 (12.0/14.0, under %ProgramFiles(x86)%\MSBuild);
    ///   - VS2017+ toolsets (15/16/17) located via vswhere.exe, with a directory-probe fallback.
    /// Every probe is defensive: any failure for one source simply yields no entry for it, never an
    /// exception (a server must not crash during startup discovery).
    /// </summary>
    internal static class MsBuildDiscovery
    {
        public static IList<MsBuildInstall> Discover(ILogSink log)
        {
            // Keyed by version so the same toolset found via two routes collapses to one entry.
            Dictionary<string, MsBuildInstall> byVersion =
                new Dictionary<string, MsBuildInstall>(StringComparer.OrdinalIgnoreCase);

            try { DiscoverFramework(byVersion); } catch (Exception ex) { Note(log, "framework probe failed: " + ex.Message); }
            try { DiscoverStandalone(byVersion); } catch (Exception ex) { Note(log, "standalone probe failed: " + ex.Message); }
            try { DiscoverViaVsWhere(byVersion, log); } catch (Exception ex) { Note(log, "vswhere probe failed: " + ex.Message); }
            try { DiscoverVsFallback(byVersion); } catch (Exception ex) { Note(log, "vs fallback probe failed: " + ex.Message); }

            List<MsBuildInstall> result = new List<MsBuildInstall>(byVersion.Values);
            result.Sort(delegate(MsBuildInstall a, MsBuildInstall b) { return CompareVersion(a.Version, b.Version); });
            return result;
        }

        // ---- .NET Framework MSBuild (bundled; present from Windows XP onward) ----

        private static void DiscoverFramework(IDictionary<string, MsBuildInstall> into)
        {
            string windir = Environment.GetEnvironmentVariable("WINDIR");
            if (string.IsNullOrEmpty(windir)) windir = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrEmpty(windir)) return;

            // (framework subdir, toolset version)
            string[][] table = new string[][]
            {
                new string[] { "v2.0.50727", "2.0" },
                new string[] { "v3.5",       "3.5" },
                new string[] { "v4.0.30319", "4.0" },
            };

            foreach (string[] row in table)
            {
                string sub = row[0], ver = row[1];
                string x86 = Path.Combine(Path.Combine(Path.Combine(windir, "Microsoft.NET"), "Framework"), Path.Combine(sub, "MSBuild.exe"));
                string x64 = Path.Combine(Path.Combine(Path.Combine(windir, "Microsoft.NET"), "Framework64"), Path.Combine(sub, "MSBuild.exe"));
                AddPaths(into, ver, "MSBuild " + ver + " (.NET Framework)", Exists(x86) ? x86 : null, Exists(x64) ? x64 : null);
            }
        }

        // ---- Standalone MSBuild from VS2013 (12.0) / VS2015 (14.0) ----

        private static void DiscoverStandalone(IDictionary<string, MsBuildInstall> into)
        {
            string pf = ProgramFilesX86();
            if (string.IsNullOrEmpty(pf)) return;

            string[][] table = new string[][]
            {
                new string[] { "12.0", "Visual Studio 2013" },
                new string[] { "14.0", "Visual Studio 2015" },
            };

            foreach (string[] row in table)
            {
                string ver = row[0], vs = row[1];
                string bin = Path.Combine(Path.Combine(Path.Combine(pf, "MSBuild"), ver), "Bin");
                string x86 = Path.Combine(bin, "MSBuild.exe");
                string x64 = Path.Combine(Path.Combine(bin, "amd64"), "MSBuild.exe");
                AddPaths(into, ver, "MSBuild " + ver + " (" + vs + ")", Exists(x86) ? x86 : null, Exists(x64) ? x64 : null);
            }
        }

        // ---- VS2017+ via vswhere.exe (the supported discovery route for 15.0/Current toolsets) ----

        private static void DiscoverViaVsWhere(IDictionary<string, MsBuildInstall> into, ILogSink log)
        {
            string pf = ProgramFilesX86();
            if (string.IsNullOrEmpty(pf)) return;
            string vswhere = Path.Combine(Path.Combine(Path.Combine(pf, "Microsoft Visual Studio"), "Installer"), "vswhere.exe");
            if (!Exists(vswhere)) return;

            ProcessRequest req = new ProcessRequest();
            req.FileName = vswhere;
            // JSON is the cleanest machine-readable form and Newtonsoft is already referenced.
            req.Arguments = "-products * -requires Microsoft.Component.MSBuild -format json -utf8";
            req.WorkingDirectory = pf;
            req.TimeoutMs = 15000;

            ProcessResult res = new ProcessRunner(null).Run(req);
            if (res.TimedOut || res.ExitCode != 0 || string.IsNullOrEmpty(res.StdOut)) return;

            JArray arr;
            try { arr = JArray.Parse(res.StdOut); }
            catch { return; }

            foreach (JToken tok in arr)
            {
                JObject o = tok as JObject;
                if (o == null) continue;
                string installPath = (string)o["installationPath"];
                string installVer = (string)o["installationVersion"];
                if (string.IsNullOrEmpty(installPath)) continue;

                int major = MajorOf(installVer);
                if (major <= 0) continue;
                string ver = major + ".0";                 // 15.0 / 16.0 / 17.0

                // VS2017 lays MSBuild under MSBuild\15.0; VS2019/2022 under MSBuild\Current.
                string x86 = FirstExisting(
                    Path.Combine(Path.Combine(Path.Combine(installPath, "MSBuild"), "Current"), Path.Combine("Bin", "MSBuild.exe")),
                    Path.Combine(Path.Combine(Path.Combine(installPath, "MSBuild"), "15.0"), Path.Combine("Bin", "MSBuild.exe")));
                string x64 = FirstExisting(
                    Path.Combine(Path.Combine(Path.Combine(installPath, "MSBuild"), "Current"), Path.Combine(Path.Combine("Bin", "amd64"), "MSBuild.exe")),
                    Path.Combine(Path.Combine(Path.Combine(installPath, "MSBuild"), "15.0"), Path.Combine(Path.Combine("Bin", "amd64"), "MSBuild.exe")));

                AddPaths(into, ver, "MSBuild " + ver + " (" + VsName(major) + ")", x86, x64);
            }
        }

        // ---- Fallback when vswhere is absent: probe the well-known VS2017/2019/2022 install roots ----

        private static void DiscoverVsFallback(IDictionary<string, MsBuildInstall> into)
        {
            string pf = ProgramFilesX86();
            if (string.IsNullOrEmpty(pf)) return;

            // (year folder, toolset version, MSBuild subfolder)
            string[][] years = new string[][]
            {
                new string[] { "2017", "15.0", "15.0" },
                new string[] { "2019", "16.0", "Current" },
                new string[] { "2022", "17.0", "Current" },
            };
            string[] editions = new string[] { "Enterprise", "Professional", "Community", "BuildTools", "Preview" };

            foreach (string[] y in years)
            {
                string year = y[0], ver = y[1], sub = y[2];
                if (into.ContainsKey(ver)) continue; // vswhere already found this toolset
                foreach (string ed in editions)
                {
                    string bin = Path.Combine(Path.Combine(Path.Combine(Path.Combine(Path.Combine(pf, "Microsoft Visual Studio"), year), ed), "MSBuild"), Path.Combine(sub, "Bin"));
                    string x86 = Path.Combine(bin, "MSBuild.exe");
                    string x64 = Path.Combine(Path.Combine(bin, "amd64"), "MSBuild.exe");
                    if (Exists(x86) || Exists(x64))
                    {
                        AddPaths(into, ver, "MSBuild " + ver + " (Visual Studio " + year + ")", Exists(x86) ? x86 : null, Exists(x64) ? x64 : null);
                        break; // first edition found wins for this year
                    }
                }
            }
        }

        // ---- helpers ----

        private static void AddPaths(IDictionary<string, MsBuildInstall> into, string ver, string label, string x86, string x64)
        {
            if (string.IsNullOrEmpty(x86) && string.IsNullOrEmpty(x64)) return;
            MsBuildInstall existing;
            if (into.TryGetValue(ver, out existing))
            {
                if (string.IsNullOrEmpty(existing.X86Path)) existing.X86Path = x86;
                if (string.IsNullOrEmpty(existing.X64Path)) existing.X64Path = x64;
                return;
            }
            into[ver] = new MsBuildInstall { Version = ver, Label = label, X86Path = x86, X64Path = x64 };
        }

        private static string ProgramFilesX86()
        {
            string pf = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (string.IsNullOrEmpty(pf)) pf = Environment.GetEnvironmentVariable("ProgramFiles");
            return pf;
        }

        private static bool Exists(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
            catch { return false; }
        }

        private static string FirstExisting(string a, string b)
        {
            if (Exists(a)) return a;
            if (Exists(b)) return b;
            return null;
        }

        private static int MajorOf(string version)
        {
            if (string.IsNullOrEmpty(version)) return 0;
            int dot = version.IndexOf('.');
            string head = dot > 0 ? version.Substring(0, dot) : version;
            int n;
            return int.TryParse(head, out n) ? n : 0;
        }

        private static string VsName(int major)
        {
            switch (major)
            {
                case 15: return "Visual Studio 2017";
                case 16: return "Visual Studio 2019";
                case 17: return "Visual Studio 2022";
                default: return "Visual Studio";
            }
        }

        // Numeric, dotted-version comparison ("4.0" < "12.0" < "17.0"), not lexicographic.
        private static int CompareVersion(string a, string b)
        {
            int am = MajorOf(a), bm = MajorOf(b);
            if (am != bm) return am.CompareTo(bm);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static void Note(ILogSink log, string msg)
        {
            if (log != null) log.Log("msbuild", msg);
        }
    }
}
