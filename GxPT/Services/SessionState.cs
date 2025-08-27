using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GxPT
{
    // Persists transient session state (like open tabs) separately from settings.json
    internal static class SessionState
    {
        private static string GetSessionPath()
        {
            try
            {
                string dir = AppSettings.SettingsDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, "session.json");
            }
            catch
            {
                // Fallback to %AppData%\GxPT
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GxPT");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
                catch { }
                return Path.Combine(dir, "session.json");
            }
        }

        internal sealed class SessionStateDto
        {
            public List<string> OpenTabs { get; set; }
            public string ActiveTab { get; set; }
        }

        public static void SaveOpenTabs(IList<string> openTabIds, string activeTabId)
        {
            try
            {
                var dto = new SessionStateDto
                {
                    OpenTabs = openTabIds != null ? new List<string>(openTabIds) : new List<string>(),
                    ActiveTab = activeTabId ?? string.Empty
                };
                var ser = new JavaScriptSerializer();
                string json = ser.Serialize(dto);
                File.WriteAllText(GetSessionPath(), json, Encoding.UTF8);
            }
            catch { }
        }

        public static void LoadOpenTabs(out List<string> openTabIds, out string activeTabId)
        {
            openTabIds = new List<string>();
            activeTabId = null;
            try
            {
                string path = GetSessionPath();
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(json)) return;
                var ser = new JavaScriptSerializer();
                var dto = ser.Deserialize<SessionStateDto>(json);
                if (dto != null)
                {
                    if (dto.OpenTabs != null) openTabIds = new List<string>(dto.OpenTabs);
                    activeTabId = string.IsNullOrEmpty(dto.ActiveTab) ? null : dto.ActiveTab;
                }
            }
            catch { }
        }
    }
}
