using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace GxPT
{
    /// <summary>
    /// Application-wide hover-to-scroll router. Ensures mouse wheel scrolls the control
    /// under the cursor (ChatTranscriptControl, ListView, or TextBoxBase) without changing focus.
    /// Target: .NET 3.5 / Windows XP compatible.
    /// </summary>
    internal static class HoverWheelRouter
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed) return;
            try
            {
                Application.AddMessageFilter(new Filter());
                _installed = true;
            }
            catch { }
        }

        private sealed class Filter : IMessageFilter
        {
            private const int WM_MOUSEWHEEL = 0x020A;

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern IntPtr WindowFromPoint(System.Drawing.Point p);

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL) return false;
                // Extract wheel delta from wParam high word
                int wparam = m.WParam.ToInt32();
                int wheelDelta = (short)((wparam >> 16) & 0xFFFF);
                Point screenPt = Control.MousePosition;

                // 1) ChatTranscriptControl (if any) under mouse: call its scroll method
                ChatTranscriptControl transcript = GetHoveredTranscript(screenPt);
                if (transcript != null)
                {
                    try
                    {
                        Rectangle screenRect = transcript.RectangleToScreen(transcript.ClientRectangle);
                        if (screenRect.Contains(screenPt))
                        {
                            // Let the control decide (supports Shift+Wheel horizontal scrolling)
                            transcript.HandleHoverWheel(wheelDelta, screenPt, Control.ModifierKeys);
                            return true;
                        }
                    }
                    catch { }
                }

                // 2) Otherwise forward to ListView or TextBoxBase under mouse
                try
                {
                    IntPtr hwnd = WindowFromPoint(screenPt);
                    if (hwnd == IntPtr.Zero) return false;
                    Control ctl = Control.FromHandle(hwnd);
                    if (ctl == null) return false;
                    Control cur = ctl;
                    for (int i = 0; i < 3 && cur != null; i++)
                    {
                        if (cur is ListView || cur is TextBoxBase)
                        {
                            SendMessage(cur.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
                            return true;
                        }
                        cur = cur.Parent;
                    }
                }
                catch { }

                return false;
            }

            private static ChatTranscriptControl GetHoveredTranscript(Point screenPt)
            {
                try
                {
                    // Iterate open forms and search for a ChatTranscriptControl whose client rect contains the point
                    for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
                    {
                        var form = Application.OpenForms[i];
                        if (form == null || form.IsDisposed || !form.Visible) continue;
                        // Walk all controls (shallow scan first; ChatTranscriptControl instances are few)
                        ChatTranscriptControl found = FindTranscriptIn(form, screenPt);
                        if (found != null) return found;
                    }
                }
                catch { }
                return null;
            }

            private static ChatTranscriptControl FindTranscriptIn(Control parent, Point screenPt)
            {
                if (parent == null || parent.IsDisposed || !parent.Visible) return null;
                foreach (Control c in parent.Controls)
                {
                    if (c == null || c.IsDisposed || !c.Visible) continue;
                    if (c is ChatTranscriptControl)
                    {
                        Rectangle r = c.RectangleToScreen(c.ClientRectangle);
                        if (r.Contains(screenPt)) return (ChatTranscriptControl)c;
                    }
                    var nested = FindTranscriptIn(c, screenPt);
                    if (nested != null) return nested;
                }
                return null;
            }
        }
    }
}
