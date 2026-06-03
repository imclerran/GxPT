using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GxPT
{
    // Remembers the most-recently-used working directories (most-recent-first, capped),
    // persisted to its own JSON file alongside settings.json. XP / .NET 3.5 compatible.
    internal static class RecentWorkDirs
    {
        public const int MaxEntries = 5;

        // Tests redirect IO here; null means use the default %APPDATA%\GxPT path.
        internal static string FilePathOverride;

        private static readonly object _gate = new object();

        private static string FilePath
        {
            get
            {
                if (!string.IsNullOrEmpty(FilePathOverride)) return FilePathOverride;
                return Path.Combine(AppSettings.SettingsDirectory, "recent-workdirs.json");
            }
        }

        // Returns the stored list, most-recent-first. No existence filtering here.
        public static List<string> Get()
        {
            lock (_gate)
            {
                return ReadLocked();
            }
        }

        // Records 'path' as the most-recent entry: dedup (case-insensitive), move to front, cap.
        public static void Add(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Trim().Length == 0) return;

            string norm = path.Trim();
            try { norm = Path.GetFullPath(norm); }
            catch { }
            norm = norm.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (norm.Length == 0) return;

            lock (_gate)
            {
                List<string> list = ReadLocked();
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(list[i], norm, StringComparison.OrdinalIgnoreCase))
                        list.RemoveAt(i);
                }
                list.Insert(0, norm);
                while (list.Count > MaxEntries) list.RemoveAt(list.Count - 1);
                WriteLocked(list);
            }
        }

        private static List<string> ReadLocked()
        {
            List<string> result = new List<string>();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return result;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return result;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                object[] arr = ser.DeserializeObject(text) as object[];
                if (arr != null)
                {
                    foreach (object item in arr)
                    {
                        if (item != null) result.Add(Convert.ToString(item));
                    }
                }
            }
            catch { }
            return result;
        }

        private static void WriteLocked(List<string> list)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(list);
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                FileSafe.WriteAllTextAtomic(path, json, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
