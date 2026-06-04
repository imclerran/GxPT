using System;
using System.Collections.Generic;
using System.Text;

namespace MemoryMcpServer
{
    /// <summary>
    /// Turns a caller-supplied memory name into a filesystem-safe handle: at most 5 words, each
    /// word reduced to its <c>[A-Za-z0-9]</c> characters, words joined by hyphens. The slug is both
    /// the index key and the detail filename (<c>&lt;slug&gt;.md</c>), so no name->file map is needed.
    /// </summary>
    internal static class Slug
    {
        public const int MaxWords = 5;

        // Returns the slug, or null with a user-facing reason in <paramref name="error"/>.
        public static string Make(string name, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
            {
                error = "name is required";
                return null;
            }

            string[] words = name.Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > MaxWords)
            {
                error = "name must be at most " + MaxWords + " words";
                return null;
            }

            List<string> parts = new List<string>();
            for (int i = 0; i < words.Length; i++)
            {
                StringBuilder sb = new StringBuilder();
                string w = words[i];
                for (int j = 0; j < w.Length; j++)
                {
                    char c = w[j];
                    if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                        sb.Append(c);
                }
                if (sb.Length > 0) parts.Add(sb.ToString());
            }

            if (parts.Count == 0)
            {
                error = "name has no usable [A-Za-z0-9] characters";
                return null;
            }
            return string.Join("-", parts.ToArray());
        }
    }
}
