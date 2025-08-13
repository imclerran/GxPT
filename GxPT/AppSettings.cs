using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GxPT
{
    // JSON-based app settings (XP/.NET 3.5 compatible via JavaScriptSerializer)
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
            get { return Path.Combine(SettingsDirectory, "settings.json"); }
        }

        private static Dictionary<string, object> LoadJson()
        {
            var json = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(SettingsPath)) return json;
                string text = File.ReadAllText(SettingsPath, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return json;
                var ser = new JavaScriptSerializer();
                var obj = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (obj != null)
                {
                    foreach (var kv in obj) json[kv.Key] = kv.Value;
                }
            }
            catch { }
            return json;
        }

        public static string GetString(string key)
        {
            var all = LoadJson();
            object val;
            if (all.TryGetValue(key, out val) && val != null)
                return Convert.ToString(val);
            return null;
        }

        public static List<string> GetList(string key)
        {
            var result = new List<string>();
            var all = LoadJson();
            object val;
            if (all.TryGetValue(key, out val) && val != null)
            {
                try
                {
                    var arr = val as object[];
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            if (item != null) result.Add(Convert.ToString(item));
                        }
                    }
                    else
                    {
                        var list = val as System.Collections.ArrayList;
                        if (list != null)
                        {
                            foreach (var item in list)
                            {
                                if (item != null) result.Add(Convert.ToString(item));
                            }
                        }
                    }
                }
                catch { }
            }
            return result;
        }
    }
}
