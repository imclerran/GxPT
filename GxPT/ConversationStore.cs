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
            File.WriteAllText(path, json);
        }

        public static Conversation Load(OpenRouterClient client, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                string json = File.ReadAllText(path);
                var ser = new JavaScriptSerializer();
                var dto = ser.Deserialize<ConversationDto>(json);
                if (dto == null) return null;
                var convo = new Conversation(client)
                {
                    Id = dto.Id,
                    Name = dto.Name ?? "New Conversation",
                    SelectedModel = dto.SelectedModel,
                    LastUpdated = dto.LastUpdated == default(DateTime) ? File.GetLastWriteTimeUtc(path) : dto.LastUpdated
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
            catch { return null; }
        }

        public static List<ConversationListItem> ListAll()
        {
            var list = new List<ConversationListItem>();
            string root = GetRoot();
            if (!Directory.Exists(root)) return list;
            foreach (var path in Directory.GetFiles(root, "*" + FileExt))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var dto = new JavaScriptSerializer().Deserialize<ConversationDto>(json);
                    if (dto == null) continue;
                    list.Add(new ConversationListItem
                    {
                        Id = dto.Id,
                        Name = string.IsNullOrEmpty(dto.Name) ? "New Conversation" : dto.Name,
                        SelectedModel = dto.SelectedModel,
                        LastUpdated = dto.LastUpdated,
                        Path = path
                    });
                }
                catch { }
            }
            // Order by LastUpdated descending
            list = list.OrderByDescending(i => i.LastUpdated).ToList();
            return list;
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
