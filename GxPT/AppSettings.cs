using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GxPT
{
    // Minimal YAML reader for simple key: value pairs (no nesting), XP/.NET 3.5 compatible
    internal static class AppSettings
    {
        public static string SettingsDirectory
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GxPT");
            }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(SettingsDirectory, "settings.yaml"); }
        }

        public static Dictionary<string, string> Load()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(SettingsPath)) return map;
                string[] lines = File.ReadAllLines(SettingsPath, Encoding.UTF8);
                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("#")) continue;
                    int idx = line.IndexOf(':');
                    if (idx <= 0) continue;
                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim();
                    // remove optional quotes
                    if (value.StartsWith("\"") && value.EndsWith("\""))
                        value = value.Substring(1, value.Length - 2);
                    map[key] = value;
                }
            }
            catch { }
            return map;
        }

        public static string Get(string key)
        {
            var all = Load();
            string val;
            return all.TryGetValue(key, out val) ? val : null;
        }
    }
}
