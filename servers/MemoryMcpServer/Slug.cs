using System;
using System.Text;

namespace MemoryMcpServer
{
    /// <summary>
    /// Turns a caller-supplied memory name into a filesystem-safe kebab-case handle: lowercase
    /// <c>[a-z0-9]</c> words joined by single hyphens, at most 5 words. Word boundaries come from
    /// whitespace, separators (<c>_</c>, <c>-</c>, etc.) and camelCase transitions, so "Auth Flow",
    /// "authFlow", and "auth_flow" all normalize to "auth-flow". The slug is both the index key and
    /// the detail filename (<c>&lt;slug&gt;.md</c>), so no name-&gt;file map is needed.
    /// </summary>
    internal static class Slug
    {
        public const int MaxWords = 5;

        // Returns the kebab-case slug, or null with a user-facing reason in <paramref name="error"/>.
        public static string Make(string name, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
            {
                error = "name is required";
                return null;
            }

            string trimmed = name.Trim();

            // "At most 5 words" guidance is counted on whitespace tokens (the human-meaningful word
            // count), independent of the kebab normalization below.
            string[] words = trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > MaxWords)
            {
                error = "name must be at most " + MaxWords + " words";
                return null;
            }

            // Emit lowercase [a-z0-9], inserting a single hyphen at every boundary: any run of
            // non-alphanumeric characters, and lower/digit -> Upper transitions (camelCase).
            StringBuilder sb = new StringBuilder();
            bool sepPending = false;        // a hyphen is due before the next emitted char
            bool prevWasLowerOrDigit = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                bool isUpper = c >= 'A' && c <= 'Z';
                bool isLower = c >= 'a' && c <= 'z';
                bool isDigit = c >= '0' && c <= '9';

                if (!(isUpper || isLower || isDigit))
                {
                    if (sb.Length > 0) sepPending = true; // separator -> boundary (collapsed)
                    prevWasLowerOrDigit = false;
                    continue;
                }

                if (isUpper && prevWasLowerOrDigit) sepPending = true; // camelCase boundary
                if (sepPending && sb.Length > 0) sb.Append('-');
                sepPending = false;

                sb.Append(isUpper ? (char)(c - 'A' + 'a') : c);
                prevWasLowerOrDigit = isLower || isDigit;
            }

            string slug = sb.ToString();
            if (slug.Length == 0)
            {
                error = "name has no usable [A-Za-z0-9] characters";
                return null;
            }
            return slug;
        }
    }
}
