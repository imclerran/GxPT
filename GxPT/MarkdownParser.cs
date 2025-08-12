// MarkdownParser.cs
// Simple Markdown parser for chat transcript display
// Target: .NET 3.5, Windows XP compatible (no external deps)

using System;
using System.Collections.Generic;

namespace GxPT
{
    // ---------- Markdown model ----------
    public enum BlockType { Paragraph, Heading, CodeBlock, BulletList, NumberedList }

    [Flags]
    public enum RunStyle { Normal = 0, Bold = 1, Italic = 2, Code = 4 }

    public abstract class Block
    {
        public BlockType Type;
    }

    public sealed class HeadingBlock : Block
    {
        public int Level;
        public List<InlineRun> Inlines;
    }

    public sealed class ParagraphBlock : Block
    {
        public List<InlineRun> Inlines;
    }

    public sealed class CodeBlock : Block
    {
        public string Text;
        public string Language; // Language for syntax highlighting
    }

    public sealed class BulletListBlock : Block
    {
        public List<ListItem> Items = new List<ListItem>();
    }

    public sealed class NumberedListBlock : Block
    {
        public List<ListItem> Items = new List<ListItem>();
    }

    public sealed class ListItem
    {
        public List<InlineRun> Content;
        public int IndentLevel; // 0 = top level, 1 = nested once, etc.
    }

    public sealed class InlineRun
    {
        public string Text;
        public RunStyle Style;
    }

    public static class MarkdownParser
    {
        // ---------- Public API ----------
        public static List<Block> ParseMarkdown(string md)
        {
            var blocks = new List<Block>();
            if (string.IsNullOrEmpty(md))
            {
                blocks.Add(new ParagraphBlock { Type = BlockType.Paragraph, Inlines = new List<InlineRun>() });
                return blocks;
            }

            var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inCode = false;
            string codeLanguage = null; // Track the language for current code block
            var codeAccum = new System.Text.StringBuilder();
            var paraAccum = new System.Text.StringBuilder();
            List<ListItem> bulletAccum = null;
            List<ListItem> numberedAccum = null;

            Action flushParagraph = delegate
            {
                string t = paraAccum.ToString().Trim();
                paraAccum.Length = 0;
                if (t.Length > 0)
                    blocks.Add(new ParagraphBlock { Type = BlockType.Paragraph, Inlines = ParseInlines(t) });
            };

            Action flushBullets = delegate
            {
                if (bulletAccum != null && bulletAccum.Count > 0)
                {
                    var bl = new BulletListBlock { Type = BlockType.BulletList };
                    bl.Items.AddRange(bulletAccum);
                    blocks.Add(bl);
                    bulletAccum = null;
                }
            };

            Action flushNumbered = delegate
            {
                if (numberedAccum != null && numberedAccum.Count > 0)
                {
                    var nl = new NumberedListBlock { Type = BlockType.NumberedList };
                    nl.Items.AddRange(numberedAccum);
                    blocks.Add(nl);
                    numberedAccum = null;
                }
            };

            Action flushCode = delegate
            {
                if (codeAccum.Length > 0 || inCode)
                {
                    var text = codeAccum.ToString();
                    // Trim one leading/trailing newline if present
                    if (text.StartsWith("\n")) text = text.Substring(1);
                    if (text.EndsWith("\n")) text = text.Substring(0, text.Length - 1);
                    blocks.Add(new CodeBlock { Type = BlockType.CodeBlock, Text = text, Language = codeLanguage });
                    codeAccum.Length = 0;
                    codeLanguage = null;
                }
            };

            foreach (string raw in lines)
            {
                string line = raw;
                if (line.StartsWith("```"))
                {
                    if (!inCode)
                    {
                        // enter code: first flush any open containers
                        flushParagraph();
                        flushBullets();
                        flushNumbered();
                        inCode = true;

                        // Extract language from opening fence (e.g., "```csharp")
                        if (line.Length > 3)
                        {
                            codeLanguage = line.Substring(3).Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(codeLanguage))
                                codeLanguage = null;
                        }
                    }
                    else
                    {
                        // leave code
                        inCode = false;
                        flushCode();
                    }
                    continue;
                }

                if (inCode)
                {
                    codeAccum.Append(line).Append('\n');
                    continue;
                }

                // blank line → flush paragraph/bullets
                if (line.Trim().Length == 0)
                {
                    flushParagraph();
                    flushBullets();
                    flushNumbered();
                    continue;
                }

                // heading?
                int h = HeadingLevel(line);
                if (h > 0)
                {
                    flushParagraph();
                    flushBullets();
                    flushNumbered();
                    string content = line.Substring(h + 1).TrimEnd();
                    blocks.Add(new HeadingBlock { Type = BlockType.Heading, Level = h, Inlines = ParseInlines(content) });
                    continue;
                }

                // bullet?
                string bulletText;
                int bulletIndent;
                if (IsBullet(line, out bulletText, out bulletIndent))
                {
                    flushParagraph();
                    flushNumbered(); // switch list types
                    if (bulletAccum == null) bulletAccum = new List<ListItem>();
                    bulletAccum.Add(new ListItem { Content = ParseInlines(bulletText), IndentLevel = bulletIndent });
                    continue;
                }

                // numbered item?
                string numberedText;
                int numberedIndent;
                if (IsNumberedItem(line, out numberedText, out numberedIndent))
                {
                    flushParagraph();
                    flushBullets(); // switch list types
                    if (numberedAccum == null) numberedAccum = new List<ListItem>();
                    numberedAccum.Add(new ListItem { Content = ParseInlines(numberedText), IndentLevel = numberedIndent });
                    continue;
                }

                // otherwise paragraph text (accumulate)
                if (paraAccum.Length > 0) paraAccum.Append(' ');
                paraAccum.Append(line.Trim());
            }

            // end flush
            if (inCode) { inCode = false; }
            flushCode();
            flushParagraph();
            flushBullets();
            flushNumbered();

            if (blocks.Count == 0)
                blocks.Add(new ParagraphBlock { Type = BlockType.Paragraph, Inlines = new List<InlineRun>() });

            return blocks;
        }

        // Very simple inline parser: **bold**, *italic*, __bold__, _italic_, `code`
        // No nested emphasis beyond bold+italic combos; escapes not handled exhaustively (sufficient for chat)
        public static List<InlineRun> ParseInlines(string text)
        {
            var runs = new List<InlineRun>();
            if (string.IsNullOrEmpty(text))
            {
                runs.Add(new InlineRun { Text = "", Style = RunStyle.Normal });
                return runs;
            }

            int i = 0;
            RunStyle style = RunStyle.Normal;
            var sb = new System.Text.StringBuilder();

            Action flush = delegate
            {
                if (sb.Length > 0)
                {
                    runs.Add(new InlineRun { Text = sb.ToString(), Style = style });
                    sb.Length = 0;
                }
            };

            while (i < text.Length)
            {
                // inline code
                if (text[i] == '`')
                {
                    flush();
                    i++;
                    int start = i;
                    while (i < text.Length && text[i] != '`') i++;
                    string code = text.Substring(start, i - start);
                    runs.Add(new InlineRun { Text = code, Style = RunStyle.Code });
                    if (i < text.Length && text[i] == '`') i++;
                    continue;
                }

                // **bold** or __bold__
                if (i + 1 < text.Length && ((text[i] == '*' && text[i + 1] == '*') || (text[i] == '_' && text[i + 1] == '_')))
                {
                    // toggle bold
                    bool turnOn = (style & RunStyle.Bold) == 0;
                    flush();
                    if (turnOn) style |= RunStyle.Bold; else style &= ~RunStyle.Bold;
                    i += 2;
                    continue;
                }

                // *italic* or _italic_
                if (text[i] == '*' || text[i] == '_')
                {
                    bool turnOn = (style & RunStyle.Italic) == 0;
                    flush();
                    if (turnOn) style |= RunStyle.Italic; else style &= ~RunStyle.Italic;
                    i++;
                    continue;
                }

                sb.Append(text[i]);
                i++;
            }
            flush();
            return runs;
        }

        // ---------- Helper methods ----------
        private static bool IsBullet(string line, out string content, out int indentLevel)
        {
            content = null;
            indentLevel = 0;

            // Count leading spaces/tabs for nesting
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            {
                if (line[i] == ' ') indentLevel++;
                else indentLevel += 4; // treat tab as 4 spaces
                i++;
            }
            indentLevel = indentLevel / 2; // 2 spaces = 1 indent level

            if (i >= line.Length) return false;

            char c = line[i];
            if ((c == '-' || c == '*' || c == '+') && i + 1 < line.Length && (line[i + 1] == ' ' || line[i + 1] == '\t'))
            {
                content = line.Substring(i + 2).TrimEnd();
                return true;
            }
            return false;
        }

        private static bool IsNumberedItem(string line, out string content, out int indentLevel)
        {
            content = null;
            indentLevel = 0;

            // Count leading spaces/tabs for nesting
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            {
                if (line[i] == ' ') indentLevel++;
                else indentLevel += 4; // treat tab as 4 spaces
                i++;
            }
            indentLevel = indentLevel / 2; // 2 spaces = 1 indent level

            if (i >= line.Length) return false;

            // Look for number followed by . or )
            int start = i;
            while (i < line.Length && char.IsDigit(line[i])) i++;

            if (i > start && i < line.Length && (line[i] == '.' || line[i] == ')') &&
                i + 1 < line.Length && (line[i + 1] == ' ' || line[i + 1] == '\t'))
            {
                content = line.Substring(i + 2).TrimEnd();
                return true;
            }
            return false;
        }

        private static int HeadingLevel(string line)
        {
            int i = 0;
            while (i < line.Length && i < 6 && line[i] == '#') i++;
            if (i > 0 && i < line.Length && line[i] == ' ') return i;
            return 0;
        }
    }
}
