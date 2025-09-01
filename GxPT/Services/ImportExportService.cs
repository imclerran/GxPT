using System;
using System.IO;
using System.Linq;
using System.Text;
using Ionic.Zip;

namespace GxPT
{
    internal static class ImportExportService
    {
        // Core operations (no UI): throw on failure so callers can handle UX.
        public static void ExportAll(string sourceDir, string archivePath)
        {
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
                throw new InvalidOperationException("No conversations folder found to export.");
            bool hasAny = false;
            try { hasAny = Directory.GetFiles(sourceDir, "*.json").Length > 0; }
            catch { }
            if (!hasAny)
                throw new InvalidOperationException("There are no saved conversations to export.");

            using (var zip = new ZipFile())
            {
                zip.AlternateEncoding = Encoding.UTF8;
                zip.AlternateEncodingUsage = ZipOption.AsNecessary;
                zip.CompressionLevel = Ionic.Zlib.CompressionLevel.BestCompression;
                zip.AddDirectory(sourceDir, "");
                zip.Save(archivePath);
            }
        }

        public static void ImportAll(string zipPath, string targetDir, bool overwriteExisting)
        {
            if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath))
                throw new FileNotFoundException("Archive not found.", zipPath);
            if (string.IsNullOrEmpty(targetDir))
                throw new ArgumentException("Target folder is required.", "targetDir");
            Directory.CreateDirectory(targetDir);
            ZipSafe.SafeExtract(zipPath, targetDir, overwriteExisting);
        }

        public static void ExportSingle(string conversationFilePath, string archivePath)
        {
            if (string.IsNullOrEmpty(conversationFilePath) || !File.Exists(conversationFilePath))
                throw new FileNotFoundException("Conversation file not found.", conversationFilePath);
            using (var zip = new ZipFile())
            {
                zip.AlternateEncoding = Encoding.UTF8;
                zip.AlternateEncodingUsage = ZipOption.AsNecessary;
                zip.CompressionLevel = Ionic.Zlib.CompressionLevel.BestCompression;
                zip.AddFile(conversationFilePath, "");
                zip.Save(archivePath);
            }
        }

        // Helpers
        public static string GetConversationsFolderPath()
        {
            try
            {
                var items = ConversationStore.ListAll();
                var first = items.FirstOrDefault();
                if (first != null && !string.IsNullOrEmpty(first.Path))
                {
                    var dir = Path.GetDirectoryName(first.Path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        try { Directory.CreateDirectory(dir); }
                        catch { }
                        return dir;
                    }
                }
            }
            catch { }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dirFallback = Path.Combine(Path.Combine(appData, "GxPT"), "Conversations");
            try { Directory.CreateDirectory(dirFallback); }
            catch { }
            return dirFallback;
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            try
            {
                var invalid = Path.GetInvalidFileNameChars();
                var sb = new StringBuilder(name.Length);
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    if (invalid.Contains(c) || c == '\\' || c == '/') sb.Append('_');
                    else sb.Append(c);
                }
                string s = sb.ToString().Trim();
                if (s.Length == 0) return null;
                s = s.TrimEnd(' ', '.');
                if (s.Length == 0) return null;
                return s;
            }
            catch { return name; }
        }
    }
}
