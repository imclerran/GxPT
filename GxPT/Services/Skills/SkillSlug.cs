using System;
using System.Text;

namespace GxPT
{
    // Normalizes a skill folder name into a kebab-case slug: lowercase [a-z0-9] runs joined by single
    // hyphens, with boundaries at any run of non-alphanumeric characters and at lower/digit -> Upper
    // (camelCase) transitions, so "Release Notes", "releaseNotes", and "release_notes" all become
    // "release-notes". Same algorithm as the memory system's Slug (design S5), minus the authoring-time
    // "max 5 words" cap, which does not apply to discovering an existing folder. XP / .NET 3.5 friendly.
    // Returns null when the name has no usable [A-Za-z0-9] characters.
    internal static class SkillSlug
    {
        public static string Make(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string trimmed = name.Trim();

            StringBuilder sb = new StringBuilder();
            bool sepPending = false;            // a hyphen is due before the next emitted char
            bool prevWasLowerOrDigit = false;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                bool isUpper = c >= 'A' && c <= 'Z';
                bool isLower = c >= 'a' && c <= 'z';
                bool isDigit = c >= '0' && c <= '9';

                if (!(isUpper || isLower || isDigit))
                {
                    if (sb.Length > 0) sepPending = true;   // separator -> boundary (collapsed)
                    prevWasLowerOrDigit = false;
                    continue;
                }

                if (isUpper && prevWasLowerOrDigit) sepPending = true;   // camelCase boundary
                if (sepPending && sb.Length > 0) sb.Append('-');
                sepPending = false;

                sb.Append(isUpper ? (char)(c - 'A' + 'a') : c);
                prevWasLowerOrDigit = isLower || isDigit;
            }

            if (sb.Length == 0) return null;
            return sb.ToString();
        }
    }
}
