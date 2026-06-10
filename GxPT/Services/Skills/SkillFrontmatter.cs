using System;
using System.Text;

namespace GxPT
{
    // Hand-rolled reader for a SKILL.md's leading "--- ... ---" frontmatter block (design S4: net35 has
    // no YAML parser and the repo keeps one JSON lib, so we parse the handful of "key: value" lines
    // ourselves). Only "name" and "description" are read; unknown keys are ignored so new keys stay
    // forward-compatible. Everything after the closing delimiter is the body. Lenient: a missing or
    // unterminated block just yields no frontmatter and treats the whole text as body.
    internal sealed class SkillFrontmatter
    {
        private const char Bom = '\uFEFF';

        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Body { get; private set; }
        public bool HasFrontmatter { get; private set; }

        private SkillFrontmatter()
        {
            Body = string.Empty;
        }

        public static SkillFrontmatter Parse(string text)
        {
            SkillFrontmatter fm = new SkillFrontmatter();
            if (string.IsNullOrEmpty(text)) return fm;

            string s = text;
            if (s.Length > 0 && s[0] == Bom) s = s.Substring(1);   // strip a UTF-8 BOM

            // Normalize line endings so the delimiter scan is CRLF/CR/LF-agnostic.
            string[] lines = s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            int i = 0;
            while (i < lines.Length && lines[i].Trim().Length == 0) i++;   // skip leading blank lines

            if (i >= lines.Length || lines[i].Trim() != "---")
            {
                fm.Body = s.Trim();   // no opening delimiter: the whole text is the body
                return fm;
            }

            int start = i + 1;
            int close = -1;
            for (int j = start; j < lines.Length; j++)
            {
                if (lines[j].Trim() == "---") { close = j; break; }
            }
            if (close < 0)
            {
                fm.Body = s.Trim();   // unterminated frontmatter: treat the whole thing as body
                return fm;
            }

            fm.HasFrontmatter = true;
            for (int j = start; j < close; j++)
            {
                string line = lines[j];
                if (line.Trim().Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;

                string key = line.Substring(0, colon).Trim().ToLowerInvariant();
                string value = line.Substring(colon + 1).Trim();
                // First wins, so a stray duplicate key can't clobber the intended value.
                if (key == "name" && fm.Name == null) fm.Name = value;
                else if (key == "description" && fm.Description == null) fm.Description = value;
            }

            StringBuilder body = new StringBuilder();
            for (int j = close + 1; j < lines.Length; j++)
            {
                if (body.Length > 0) body.Append('\n');
                body.Append(lines[j]);
            }
            fm.Body = body.ToString().Trim();
            return fm;
        }
    }
}
