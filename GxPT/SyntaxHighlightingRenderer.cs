// SyntaxHighlightingRenderer.cs
// Syntax highlighting renderer for code blocks and inline code
// Target: .NET 3.5, Windows XP compatible (no external deps)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // Structure for syntax-highlighted text segments
    public struct ColoredSegment
    {
        public string Text;
        public Color Color;
        public Font Font;
        public int StartIndex;
        public int Length;

        public ColoredSegment(string text, Color color, Font font, int startIndex)
        {
            Text = text;
            Color = color;
            Font = font;
            StartIndex = startIndex;
            Length = text != null ? text.Length : 0;
        }
    }

    public static class SyntaxHighlightingRenderer
    {
        // ---------- Token color mapping ----------
        public static Color GetTokenColor(TokenType tokenType)
        {
            switch (tokenType)
            {
                case TokenType.Keyword:
                    return Color.FromArgb(0, 0, 255);      // Blue
                case TokenType.String:
                    return Color.FromArgb(163, 21, 21);    // Dark red
                case TokenType.Comment:
                    return Color.FromArgb(0, 128, 0);      // Green
                case TokenType.Number:
                    return Color.FromArgb(255, 140, 0);    // Orange
                case TokenType.Type:
                    return Color.FromArgb(43, 145, 175);   // Teal
                case TokenType.Method:
                    return Color.FromArgb(255, 20, 147);   // Deep pink
                case TokenType.Normal:
                default:
                    return SystemColors.WindowText;        // Default text color
            }
        }

        // ---------- Segment generation ----------
        public static List<ColoredSegment> GetColoredSegments(string code, string language, Font monoFont)
        {
            var segments = new List<ColoredSegment>();

            if (string.IsNullOrEmpty(code))
                return segments;

            // Get the appropriate syntax highlighter
            ISyntaxHighlighter highlighter = SyntaxHighlighter.GetHighlighter(language);
            if (highlighter == null)
            {
                // No syntax highlighting - return the entire text as one segment
                segments.Add(new ColoredSegment(code, SystemColors.WindowText, monoFont, 0));
                return segments;
            }

            // Tokenize the code
            var tokens = highlighter.Tokenize(code);

            if (tokens.Count == 0)
            {
                // Tokenization failed - return the entire text as one segment
                segments.Add(new ColoredSegment(code, SystemColors.WindowText, monoFont, 0));
                return segments;
            }

            // Fill in any gaps between tokens with normal text
            var allSegments = new List<ColoredSegment>();
            int lastEnd = 0;

            foreach (var token in tokens)
            {
                // Add any gap before this token as normal text
                if (token.StartIndex > lastEnd)
                {
                    string gapText = code.Substring(lastEnd, token.StartIndex - lastEnd);
                    allSegments.Add(new ColoredSegment(gapText, SystemColors.WindowText, monoFont, lastEnd));
                }

                // Add the colored token
                Color tokenColor = GetTokenColor(token.Type);
                allSegments.Add(new ColoredSegment(token.Text, tokenColor, monoFont, token.StartIndex));

                lastEnd = token.StartIndex + token.Length;
            }

            // Add any remaining text after the last token
            if (lastEnd < code.Length)
            {
                string remainingText = code.Substring(lastEnd);
                allSegments.Add(new ColoredSegment(remainingText, SystemColors.WindowText, monoFont, lastEnd));
            }

            return allSegments;
        }

        // ---------- Rendering ----------
        public static void DrawColoredSegments(Graphics g, List<ColoredSegment> segments, Rectangle bounds)
        {
            if (segments.Count == 0)
                return;

            // Use StringFormat.GenericTypographic to minimize GDI+ padding
            using (var stringFormat = StringFormat.GenericTypographic)
            {
                stringFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                stringFormat.Trimming = StringTrimming.None;

                float x = bounds.X;
                float y = bounds.Y;
                float lineHeight = 0;
                float maxWidth = bounds.Width;

                // Calculate line height from the first segment
                if (segments.Count > 0 && segments[0].Font != null)
                    lineHeight = segments[0].Font.Height;

                foreach (var segment in segments)
                {
                    if (string.IsNullOrEmpty(segment.Text))
                        continue;

                    // Handle newlines
                    string[] lines = segment.Text.Split('\n');

                    for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        string line = lines[lineIndex];

                        if (lineIndex > 0)
                        {
                            // Move to next line
                            y += lineHeight;
                            x = bounds.X;
                        }

                        if (!string.IsNullOrEmpty(line))
                        {
                            // Measure and draw the text using Graphics.DrawString with GenericTypographic
                            using (var brush = new SolidBrush(segment.Color))
                            {
                                // Check if text fits on current line
                                SizeF textSize = g.MeasureString(line, segment.Font, PointF.Empty, stringFormat);

                                if (x + textSize.Width > bounds.Right && x > bounds.X)
                                {
                                    // Word wrap - move to next line
                                    y += lineHeight;
                                    x = bounds.X;
                                }

                                // Draw the text
                                g.DrawString(line, segment.Font, brush, new PointF(x, y), stringFormat);
                                x += textSize.Width;
                            }
                        }
                    }
                }
            }
        }
    }
}
