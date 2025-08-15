using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

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
                Messages = convo.History.Select(m => new MessageDto { Role = m.Role, Content = m.Content }).ToList()
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
                        if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                            convo.AddAssistantMessage(m.Content);
                        else if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                            convo.History.Add(new ChatMessage("system", m.Content));
                        else
                            convo.AddUserMessage(m.Content);
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
