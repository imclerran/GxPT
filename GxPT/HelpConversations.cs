using System;
using System.IO;

namespace GxPT
{
    internal static class HelpConversations
    {
        // Load a conversation template from the app's installation directory.
        // fileName can be like "help_api_keys.json"; we will probe BaseDirectory/Help/<fileName> first.
        public static Conversation LoadTemplateConversation(OpenRouterClient client, string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName)) return null;
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string path1 = Path.Combine(baseDir, "Help");
                string path = Path.Combine(path1, fileName);
                if (!File.Exists(path))
                {
                    // Fallback to root of install dir
                    path = Path.Combine(baseDir, fileName);
                    if (!File.Exists(path)) return null;
                }

                var convo = ConversationStore.Load(client, path);
                if (convo == null) return null;
                // Ensure this loads as a fresh instance (do not persist template id)
                try { convo.Id = null; }
                catch { }
                return convo;
            }
            catch { return null; }
        }
    }
}
