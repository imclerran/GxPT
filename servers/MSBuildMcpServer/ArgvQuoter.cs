using System.Collections.Generic;
using System.Text;

namespace MSBuildMcpServer
{
    /// <summary>
    /// Quotes a list of discrete argument tokens into a single Windows command line that the
    /// CRT / CommandLineToArgvW will parse back into exactly those tokens. This keeps model-supplied
    /// values (project paths, property values, platforms like "Any CPU") from being misread as
    /// switches or split into extra arguments — no value is ever concatenated raw (servers-spec §4).
    ///
    /// Rules (the standard MSVC argv quoting algorithm):
    ///  - a token with no space/tab/quote is emitted as-is;
    ///  - otherwise it is wrapped in double quotes;
    ///  - backslashes are doubled only when they precede a quote (or the closing quote);
    ///  - embedded quotes are backslash-escaped.
    /// </summary>
    internal static class ArgvQuoter
    {
        public static string Join(IList<string> tokens)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                AppendToken(sb, tokens[i] ?? string.Empty);
            }
            return sb.ToString();
        }

        private static void AppendToken(StringBuilder sb, string token)
        {
            if (token.Length > 0 && token.IndexOfAny(NeedsQuote) < 0)
            {
                sb.Append(token); // safe to emit bare
                return;
            }

            sb.Append('"');
            int backslashes = 0;
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    // Double the run of backslashes, then escape the quote.
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                }
                else
                {
                    if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
                    sb.Append(c);
                }
            }
            // Backslashes immediately before the closing quote must be doubled.
            if (backslashes > 0) sb.Append('\\', backslashes * 2);
            sb.Append('"');
        }

        private static readonly char[] NeedsQuote = new char[] { ' ', '\t', '"' };
    }
}
