using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GxPT
{
    /// <summary>
    /// Applies SyntaxHighlighter tokens to a RichTextBox efficiently (WinForms .NET 3.5 friendly).
    /// </summary>
    internal static class RichTextBoxSyntaxHighlighter
    {
        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void BeginUpdate(RichTextBox rtb)
        {
            if (rtb.IsHandleCreated)
            {
                SendMessage(rtb.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            }
            rtb.SuspendLayout();
        }

        private static void EndUpdate(RichTextBox rtb)
        {
            if (rtb.IsHandleCreated)
            {
                SendMessage(rtb.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
            }
            rtb.ResumeLayout();
            rtb.Invalidate();
            rtb.Update();
        }

        /// <summary>
        /// Highlights the RichTextBox content for the given language using registered highlighters.
        /// Non-destructive (preserves caret) and optimized for small to medium texts.
        /// </summary>
        public static void Highlight(RichTextBox rtb, string language)
        {
            if (rtb == null || rtb.IsDisposed) return;

            string text = rtb.Text ?? string.Empty;
            if (text.Length == 0)
            {
                // Reset formatting to default
                int selStart0 = rtb.SelectionStart, selLen0 = rtb.SelectionLength;
                BeginUpdate(rtb);
                try
                {
                    rtb.SelectAll();
                    rtb.SelectionColor = SystemColors.WindowText;
                }
                finally
                {
                    rtb.SelectionStart = selStart0;
                    rtb.SelectionLength = selLen0;
                    EndUpdate(rtb);
                }
                return;
            }

            List<CodeToken> tokens = SyntaxHighlighter.Highlight(language, text);

            int savedStart = rtb.SelectionStart;
            int savedLength = rtb.SelectionLength;

            // Build a prefix-sum of carriage returns to translate .NET string indices
            // (which count "\r\n" as two chars) into RichEdit CP indices (which treat
            // CRLF as a single line break). This fixes the per-line +1 offset.
            int[] crPrefix = BuildCrPrefix(text);

            BeginUpdate(rtb);
            try
            {
                // Reset all to default color first
                rtb.SelectAll();
                rtb.SelectionColor = SystemColors.WindowText;

                // Apply token colors (use .NET text length for token bounds)
                int maxLen = text.Length;
                for (int i = 0; i < tokens.Count; i++)
                {
                    CodeToken t = tokens[i];
                    if (t.Length <= 0 || t.StartIndex < 0 || t.StartIndex >= maxLen) continue;
                    if (t.Type == TokenType.Normal) continue;

                    int length = t.Length;
                    int end = t.StartIndex + length;
                    if (end > maxLen)
                    {
                        length = Math.Max(0, maxLen - t.StartIndex);
                        if (length == 0) continue;
                    }

                    // Translate indices/length into RichEdit CP space
                    int selStart = ToRtfIndex(text.Length, crPrefix, t.StartIndex);
                    int selLen = ToRtfLength(text.Length, crPrefix, t.StartIndex, length);
                    if (selStart < 0 || selLen <= 0) continue;

                    rtb.SelectionStart = selStart;
                    rtb.SelectionLength = selLen;
                    rtb.SelectionColor = SyntaxHighlighter.GetTokenColor(t.Type);
                }
            }
            finally
            {
                // Restore caret/selection
                // Convert saved selection from .NET string indices to RichEdit CPs
                int restoreStart = ToRtfIndex(text.Length, crPrefix, savedStart);
                int restoreLen = ToRtfLength(text.Length, crPrefix, savedStart, savedLength);
                int totalLenCp = ToRtfIndex(text.Length, crPrefix, rtb.TextLength);
                rtb.SelectionStart = Math.Max(0, Math.Min(restoreStart, totalLenCp));
                rtb.SelectionLength = Math.Max(0, Math.Min(restoreLen, totalLenCp - rtb.SelectionStart));
                EndUpdate(rtb);
            }
        }

        // Prefix sum of carriage returns ("\r") to help translate indices
        private static int[] BuildCrPrefix(string s)
        {
            int n = s != null ? s.Length : 0;
            int[] pref = new int[n + 1];
            if (n == 0) return pref;
            for (int i = 0; i < n; i++)
            {
                pref[i + 1] = pref[i] + (s[i] == '\r' ? 1 : 0);
            }
            return pref;
        }

        // Translate a .NET string index to RichEdit CP by subtracting preceding CRs
        private static int ToRtfIndex(int textLength, int[] crPrefix, int dotNetIndex)
        {
            if (dotNetIndex <= 0) return 0;
            if (dotNetIndex > textLength) dotNetIndex = textLength;
            if (crPrefix == null || crPrefix.Length == 0) return dotNetIndex;
            return dotNetIndex - crPrefix[dotNetIndex];
        }

        // Translate a .NET substring length to RichEdit CP length by removing CRs in span
        private static int ToRtfLength(int textLength, int[] crPrefix, int startDotNet, int lengthDotNet)
        {
            if (lengthDotNet <= 0) return 0;
            int start = Math.Max(0, Math.Min(startDotNet, textLength));
            int end = Math.Max(start, Math.Min(startDotNet + lengthDotNet, textLength));
            if (crPrefix == null || crPrefix.Length == 0) return end - start;
            int crsInSpan = crPrefix[end] - crPrefix[start];
            int len = Math.Max(0, end - start - crsInSpan);
            return len;
        }
    }
}
