using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GxPT
{
    // Global (app-wide) skill enablement, persisted to its own JSON file beside settings.json - the
    // dedicated-file pattern of recent-workdirs.json / mcp.json (design S6/S10). Written only by the
    // `... global` slash commands (phase 4b); there is no settings-form surface. The conversation layer
    // (tri-state per-skill overrides) lives on the Conversation and is combined with this default by
    // SkillResolve. XP / .NET 3.5 friendly.
    //
    //   { "feature_off": false, "disabled": ["noisy-skill"] }
    //
    // feature_off  -> the whole feature is off everywhere unless a conversation overrides it.
    // disabled     -> slugs off by default everywhere unless a conversation overrides them.
    internal sealed class SkillEnablement
    {
        public const string FileName = "skills.json";

        // Tests redirect IO here; null means use the default %APPDATA%\GxPT path.
        internal static string FilePathOverride;

        private static readonly object _gate = new object();

        private bool _featureOff;
        private readonly HashSet<string> _disabled;

        public SkillEnablement()
        {
            _disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public bool FeatureOff
        {
            get { return _featureOff; }
            set { _featureOff = value; }
        }

        public bool IsDisabled(string slug)
        {
            return !string.IsNullOrEmpty(slug) && _disabled.Contains(slug);
        }

        public void SetDisabled(string slug, bool disabled)
        {
            if (string.IsNullOrEmpty(slug)) return;
            if (disabled) _disabled.Add(slug);
            else _disabled.Remove(slug);
        }

        // Clears all per-skill disables (used by `/skills reset global`); does not touch FeatureOff.
        public void ClearDisabled()
        {
            _disabled.Clear();
        }

        // Snapshot of the disabled slugs (sorted), for listing/inspection.
        public List<string> DisabledSlugs()
        {
            List<string> list = new List<string>(_disabled);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static string FilePath
        {
            get
            {
                if (!string.IsNullOrEmpty(FilePathOverride)) return FilePathOverride;
                return Path.Combine(AppSettings.SettingsDirectory, FileName);
            }
        }

        public static SkillEnablement LoadGlobal()
        {
            lock (_gate) { return ReadLocked(); }
        }

        public void SaveGlobal()
        {
            lock (_gate) { WriteLocked(this); }
        }

        private static SkillEnablement ReadLocked()
        {
            SkillEnablement result = new SkillEnablement();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return result;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return result;

                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> obj = ser.DeserializeObject(text) as Dictionary<string, object>;
                if (obj == null) return result;

                object featureOff;
                if (obj.TryGetValue("feature_off", out featureOff) && featureOff != null)
                    result._featureOff = Convert.ToBoolean(featureOff);

                object disabled;
                if (obj.TryGetValue("disabled", out disabled))
                {
                    object[] arr = disabled as object[];
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            if (arr[i] == null) continue;
                            string slug = Convert.ToString(arr[i]);
                            if (!string.IsNullOrEmpty(slug)) result._disabled.Add(slug);
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        private static void WriteLocked(SkillEnablement e)
        {
            try
            {
                Dictionary<string, object> obj = new Dictionary<string, object>();
                obj["feature_off"] = e._featureOff;
                obj["disabled"] = e.DisabledSlugs();

                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(obj);

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
