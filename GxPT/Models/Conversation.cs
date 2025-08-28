using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace GxPT
{
    // Represents a single conversation instance: messages history and optional generated name.
    internal sealed class Conversation
    {
        private readonly OpenRouterClient _client;
        private volatile bool _namingInProgress;

        private readonly List<ChatMessage> _history = new List<ChatMessage>();
        public List<ChatMessage> History { get { return _history; } }
        public string Name { get; internal set; }
        public string Id { get; internal set; } // UUID-like id
        public string SelectedModel { get; set; }
        public DateTime LastUpdated { get; set; }
        public event Action<string> NameGenerated;

        public Conversation(OpenRouterClient client)
        {
            _client = client;
            Name = "New Conversation"; // initialize to generic name
            SelectedModel = null;
            LastUpdated = DateTime.Now;
        }

        public void AddUserMessage(string content)
        {
            History.Add(new ChatMessage("user", content ?? string.Empty));
            // Attempt (re)generation if still generic
            MaybeTriggerNaming();
            LastUpdated = DateTime.Now;
        }

        public void AddAssistantMessage(string content)
        {
            History.Add(new ChatMessage("assistant", content ?? string.Empty));
            LastUpdated = DateTime.Now;
        }

        // Generate or refine a short title using mistralai/mistral-nemo.
        public void EnsureNameGenerated()
        {
            if (_client == null || !_client.IsConfigured) return;
            // Only generate when name is still generic; otherwise keep
            if (!string.IsNullOrEmpty(Name) && Name != "New Conversation") return;
            if (_namingInProgress) return;
            _namingInProgress = true;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var msgs = new List<ChatMessage>();
                    msgs.Add(new ChatMessage("system",
                        "You generate short, descriptive conversation titles from the conversation so far. If the conversation so far is only a greeting (e.g., 'hi', 'hello', 'hey there') or lacks any clear topical content, return exactly: New Conversation. Otherwise, return only the title: 3 to 6 words, Title Case, no quotes, no trailing punctuation. You only generate conversation titles. Do not answer any user prompts."));
                    msgs.Add(History.Last());

                    string json = _client.CreateCompletion(
                        "google/gemini-2.0-flash-001",
                        msgs,
                        new ClientProperties { Stream = false }
                    );
                    string title = ExtractTitleFromJson(json);
                    title = CleanTitle(title);
                    if (!string.IsNullOrEmpty(title))
                    {
                        Name = title;
                        var handler = NameGenerated;
                        if (handler != null) handler(title);
                    }
                }
                catch { }
                finally { _namingInProgress = false; }
            });
        }

        private void MaybeTriggerNaming()
        {
            if (_client == null || !_client.IsConfigured) return;
            if (_namingInProgress) return;
            if (string.IsNullOrEmpty(Name) || Name == "New Conversation")
            {
                EnsureNameGenerated();
            }
        }

        private static string ExtractTitleFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var ser = new JavaScriptSerializer();
                var root = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("choices")) return null;
                var choices = root["choices"] as object[];
                if (choices == null || choices.Length == 0) return null;
                var first = choices[0] as Dictionary<string, object>;
                if (first == null) return null;
                if (first.ContainsKey("message"))
                {
                    var msg = first["message"] as Dictionary<string, object>;
                    if (msg != null && msg.ContainsKey("content"))
                        return msg["content"] as string;
                }
                if (first.ContainsKey("text"))
                    return first["text"] as string;
            }
            catch { }
            return null;
        }

        private static string CleanTitle(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Replace('\r', '\n');
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s.Substring(0, nl);
            s = s.Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
            {
                s = s.Substring(1, s.Length - 2).Trim();
            }
            while (s.Length > 0 && ".!?\u201d\u2019".IndexOf(s[s.Length - 1]) >= 0) s = s.Substring(0, s.Length - 1);
            if (s.Length > 80) s = s.Substring(0, 80).Trim();
            return s;
        }
    }
}
