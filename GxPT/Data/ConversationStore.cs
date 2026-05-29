using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Text;

namespace GxPT
{
    internal static class ConversationStore
    {
        private const string FolderName = "Conversations";
        private const string FileExt = ".json";

        // Per-file metadata cache for ListAll, so repeated sidebar refreshes don't re-read and
        // re-parse every conversation file. Entries are keyed by path and validated against the
        // file's last-write timestamp, so a stale entry is refreshed automatically when the file
        // changes, and entries for deleted files are pruned.
        private static readonly object _listCacheGate = new object();
        private static readonly Dictionary<string, CachedListMeta> _listCache =
            new Dictionary<string, CachedListMeta>(StringComparer.OrdinalIgnoreCase);

        private sealed class CachedListMeta
        {
            public DateTime StampUtc;
            public ConversationListItem Item;
        }

        // Lightweight DTO for listing: only the fields the sidebar needs, so we avoid
        // allocating the (potentially large) message list when reading metadata.
        private sealed class ConversationMetaDto
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string SelectedModel { get; set; }
            public DateTime LastUpdated { get; set; }
        }

        private static string GetRoot()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string root = Path.Combine(appData, "GxPT");
            root = Path.Combine(root, FolderName);
            try { if (!Directory.Exists(root)) Directory.CreateDirectory(root); }
            catch { }
            return root;
        }

        private static string SanitizeId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
            return id;
        }

        public static string GetPathForId(string id)
        {
            id = SanitizeId(id);
            if (string.IsNullOrEmpty(id)) return null;
            return Path.Combine(GetRoot(), id + FileExt);
        }

        public static string EnsureConversationId(Conversation convo)
        {
            if (convo == null) return null;
            if (!string.IsNullOrEmpty(convo.Id)) return convo.Id;
            convo.Id = Guid.NewGuid().ToString("N");
            return convo.Id;
        }

        public static void Save(Conversation convo)
        {
            if (convo == null) return;
            EnsureConversationId(convo);

            var dto = new ConversationDto
            {
                Id = convo.Id,
                Name = convo.Name,
                SelectedModel = convo.SelectedModel,
                LastUpdated = convo.LastUpdated,
                Messages = convo.History.Select(m => new MessageDto { Role = m.Role, Content = m.Content, Attachments = (m.Attachments != null && m.Attachments.Count > 0) ? m.Attachments : null }).ToList()
            };

            var ser = new JavaScriptSerializer();
            string json = ser.Serialize(dto);

            string path = GetPathForId(convo.Id);
            if (string.IsNullOrEmpty(path)) return;
            // Crash-safe write so a failure mid-save can't corrupt the conversation file.
            // UTF-8 without BOM matches the default File.WriteAllText behavior used previously.
            FileSafe.WriteAllTextAtomic(path, json, new UTF8Encoding(false));
        }

        public static Conversation Load(OpenRouterClient client, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                var ser = new JavaScriptSerializer();
                var dto = ser.Deserialize<ConversationDto>(json);
                return FromDto(client, dto, path);
            }
            catch { return null; }
        }

        public static Conversation LoadFromJson(OpenRouterClient client, string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json)) return null;
                var ser = new JavaScriptSerializer();
                var dto = ser.Deserialize<ConversationDto>(json);
                return FromDto(client, dto, null);
            }
            catch { return null; }
        }

        private static Conversation FromDto(OpenRouterClient client, ConversationDto dto, string path)
        {
            if (dto == null) return null;
            var convo = new Conversation(client)
            {
                Id = dto.Id,
                Name = dto.Name ?? "New Conversation",
                SelectedModel = dto.SelectedModel,
                LastUpdated = dto.LastUpdated == default(DateTime) && !string.IsNullOrEmpty(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : (dto.LastUpdated == default(DateTime) ? DateTime.Now : dto.LastUpdated)
            };
            if (dto.Messages != null)
            {
                foreach (var m in dto.Messages)
                {
                    if (m == null) continue;
                    string role = m.Role ?? "user";
                    string content = m.Content ?? string.Empty;
                    List<AttachedFile> atts = m.Attachments;
                    // Backward-compat: extract attachments if they were embedded in content with markers
                    string baseContent;
                    List<AttachedFile> parsed;
                    if ((atts == null || atts.Count == 0) && TryExtractAttachmentsFromContent(content, out baseContent, out parsed))
                    {
                        content = baseContent;
                        atts = parsed;
                    }
                    // Add chat message directly to history (no naming trigger needed on load)
                    convo.History.Add(new ChatMessage(role, content, atts));
                }
            }
            return convo;
        }

        public static List<ConversationListItem> ListAll()
        {
            var list = new List<ConversationListItem>();
            string root = GetRoot();
            if (!Directory.Exists(root))
            {
                lock (_listCacheGate) { _listCache.Clear(); }
                return list;
            }

            string[] files;
            try { files = Directory.GetFiles(root, "*" + FileExt); }
            catch { files = new string[0]; }

            lock (_listCacheGate)
            {
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in files)
                {
                    present.Add(path);
                    try
                    {
                        DateTime stamp;
                        try { stamp = File.GetLastWriteTimeUtc(path); }
                        catch { stamp = DateTime.MinValue; }

                        CachedListMeta cached;
                        if (_listCache.TryGetValue(path, out cached) && cached != null
                            && cached.Item != null && cached.StampUtc == stamp)
                        {
                            // Unchanged since last read: reuse cached metadata, no file I/O.
                            list.Add(cached.Item);
                            continue;
                        }

                        var item = ReadListItem(path);
                        if (item == null) continue;
                        _listCache[path] = new CachedListMeta { StampUtc = stamp, Item = item };
                        list.Add(item);
                    }
                    catch { }
                }

                // Prune cache entries for files that no longer exist.
                if (_listCache.Count > present.Count)
                {
                    var stale = new List<string>();
                    foreach (var kv in _listCache)
                        if (!present.Contains(kv.Key)) stale.Add(kv.Key);
                    for (int i = 0; i < stale.Count; i++) _listCache.Remove(stale[i]);
                }
            }

            // Order by LastUpdated descending
            list = list.OrderByDescending(i => i.LastUpdated).ToList();
            return list;
        }

        private static ConversationListItem ReadListItem(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                var dto = new JavaScriptSerializer().Deserialize<ConversationMetaDto>(json);
                if (dto == null) return null;
                return new ConversationListItem
                {
                    Id = dto.Id,
                    Name = string.IsNullOrEmpty(dto.Name) ? "New Conversation" : dto.Name,
                    SelectedModel = dto.SelectedModel,
                    LastUpdated = dto.LastUpdated,
                    Path = path
                };
            }
            catch { return null; }
        }

        public static void DeleteById(string id)
        {
            try
            {
                string path = GetPathForId(id);
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        public static int DeleteAll()
        {
            int count = 0;
            try
            {
                string root = GetRoot();
                if (!Directory.Exists(root)) return 0;
                foreach (var path in Directory.GetFiles(root, "*" + FileExt))
                {
                    try
                    {
                        File.Delete(path);
                        count++;
                    }
                    catch { }
                }
            }
            catch { }
            return count;
        }

        public static void DeletePath(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        internal sealed class ConversationDto
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string SelectedModel { get; set; }
            public DateTime LastUpdated { get; set; }
            public List<MessageDto> Messages { get; set; }
        }

        internal sealed class MessageDto
        {
            public string Role { get; set; }
            public string Content { get; set; }
            public List<AttachedFile> Attachments { get; set; }
        }

        // Extract attachments embedded using the delimiter format:
        // --- Attached File: <name> ---\n<content>\n--- End Attached File: <name> ---
        private static bool TryExtractAttachmentsFromContent(string content, out string baseContent, out List<AttachedFile> attachments)
        {
            baseContent = content ?? string.Empty;
            attachments = null;
            if (string.IsNullOrEmpty(content)) return false;
            const string startPrefix = "--- Attached File:";
            const string endPrefix = "--- End Attached File:";

            var lines = content.Replace('\r', '\n').Split(new[] { '\n' }, StringSplitOptions.None);
            var baseSb = new StringBuilder();
            var atts = new List<AttachedFile>();
            bool inBlock = false;
            string currentName = null;
            var fileSb = new StringBuilder();

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!inBlock)
                {
                    if (line != null && line.TrimStart().StartsWith(startPrefix, StringComparison.Ordinal))
                    {
                        // Parse file name
                        string tail = line.Substring(line.IndexOf(startPrefix, StringComparison.Ordinal) + startPrefix.Length).Trim();
                        if (tail.EndsWith("---", StringComparison.Ordinal)) tail = tail.Substring(0, tail.Length - 3).Trim();
                        if (tail.StartsWith(":", StringComparison.Ordinal)) tail = tail.Substring(1).Trim();
                        currentName = tail;
                        inBlock = true;
                        fileSb.Length = 0;
                    }
                    else
                    {
                        baseSb.Append(line ?? string.Empty).Append('\n');
                    }
                }
                else
                {
                    if (line != null && line.TrimStart().StartsWith(endPrefix, StringComparison.Ordinal))
                    {
                        // End of block
                        atts.Add(new AttachedFile(currentName, fileSb.ToString()));
                        inBlock = false;
                        currentName = null;
                        fileSb.Length = 0;
                    }
                    else
                    {
                        fileSb.Append(line ?? string.Empty).Append('\n');
                    }
                }
            }

            if (atts.Count > 0)
            {
                baseContent = baseSb.ToString();
                // Trim trailing newlines added by split/append
                while (baseContent.EndsWith("\n", StringComparison.Ordinal)) baseContent = baseContent.Substring(0, baseContent.Length - 1);
                attachments = atts;
                return true;
            }
            return false;
        }

        internal sealed class ConversationListItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string SelectedModel { get; set; }
            public DateTime LastUpdated { get; set; }
            public string Path { get; set; }
        }
    }
}
