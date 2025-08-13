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

        // ---------- Segment generation ----------
        public static List<ColoredSegment> GetColoredSegments(string code, string language, Font monoFont)
        {
            var segments = new List<ColoredSegment>();

            if (string.IsNullOrEmpty(code))
                return segments;

            var tokens = SyntaxHighlighter.Highlight(language, code);
            if (tokens == null || tokens.Count == 0)
            {
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
                Color tokenColor = SyntaxHighlighter.GetTokenColor(token.Type);
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

        /// <summary>
        /// Measures the rendered size of the colored segments inside a given width.
        /// This mirrors DrawColoredSegments line-wrap logic so measurement matches drawing.
        /// </summary>
        public static Size MeasureColoredSegments(Graphics g, List<ColoredSegment> segments, int maxWidth)
        {
            if (segments == null || segments.Count == 0)
                return Size.Empty;

            using (var stringFormat = StringFormat.GenericTypographic)
            {
                stringFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                stringFormat.Trimming = StringTrimming.None;

                float x = 0f;
                float y = 0f;
                float lineHeight = segments[0].Font != null ? segments[0].Font.Height : 0f;
                float maxLineWidth = 0f;

                foreach (var segment in segments)
                {
                    if (string.IsNullOrEmpty(segment.Text))
                        continue;

                    var font = segment.Font;
                    string[] lines = segment.Text.Split('\n');
                    for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        string line = lines[lineIndex];
                        if (lineIndex > 0)
                        {
                            // new visual line
                            maxLineWidth = Math.Max(maxLineWidth, x);
                            y += lineHeight;
                            x = 0f;
                        }
                        if (line.Length == 0)
                            continue;

                        // basic wrap (same as draw path)
                        SizeF textSize = g.MeasureString(line, font, PointF.Empty, stringFormat);
                        if (x + textSize.Width > maxWidth && x > 0f)
                        {
                            // wrap
                            maxLineWidth = Math.Max(maxLineWidth, x);
                            y += lineHeight;
                            x = 0f;
                        }
                        x += textSize.Width;
                    }
                }
                maxLineWidth = Math.Max(maxLineWidth, x);
                return new Size((int)Math.Ceiling(Math.Min(maxLineWidth, maxWidth)), (int)Math.Ceiling(y + lineHeight));
            }
        }
    }
}
