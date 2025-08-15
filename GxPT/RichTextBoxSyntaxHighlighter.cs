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
                    rtb.SelectionStart = 0;
                    rtb.SelectionLength = 0;
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

            BeginUpdate(rtb);
            try
            {
                // Reset all to default color first
                rtb.SelectionStart = 0;
                rtb.SelectionLength = rtb.TextLength;
                rtb.SelectionColor = SystemColors.WindowText;

                // Apply token colors
                int maxLen = rtb.TextLength;
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

                    rtb.SelectionStart = t.StartIndex;
                    rtb.SelectionLength = length;
                    rtb.SelectionColor = SyntaxHighlighter.GetTokenColor(t.Type);
                }
            }
            finally
            {
                // Restore caret/selection
                rtb.SelectionStart = Math.Max(0, Math.Min(savedStart, rtb.TextLength));
                rtb.SelectionLength = Math.Max(0, Math.Min(savedLength, rtb.TextLength - rtb.SelectionStart));
                EndUpdate(rtb);
            }
        }
    }
}
