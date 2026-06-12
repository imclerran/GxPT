using System;
using System.Threading;
using System.Windows.Forms;

namespace GxPT
{
    // Bridges the synchronous IToolApprovalPrompt (called on the tool-loop worker thread) to the
    // docked ToolApprovalPanel on the UI thread. Ask blocks the worker until the user clicks a
    // button; the turn pauses at the gate, which is correct — the user is present (approval spec §7).
    internal sealed class TranscriptApprovalPrompt : IToolApprovalPrompt
    {
        private readonly Control _uiMarshal;          // any control on the UI thread (the form)
        private readonly Func<ToolApprovalPanel> _getPanel;

        public TranscriptApprovalPrompt(Control uiMarshal, Func<ToolApprovalPanel> getPanel)
        {
            _uiMarshal = uiMarshal;
            _getPanel = getPanel;
        }

        public ApprovalChoice Ask(ApprovalRequest req)
        {
            if (_uiMarshal == null || _getPanel == null) return ApprovalChoice.Deny;

            ApprovalChoice[] result = { ApprovalChoice.Deny };
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                try
                {
                    _uiMarshal.BeginInvoke((MethodInvoker)delegate
                    {
                        // Any UI failure (panel resolver, a disposed panel mid-show) must still
                        // signal the waiting worker - an unset event strands the turn forever.
                        try
                        {
                            ToolApprovalPanel panel = _getPanel();
                            if (panel == null) { done.Set(); return; }
                            panel.ShowFor(req, delegate(ApprovalChoice choice)
                            {
                                result[0] = choice;
                                done.Set();
                            });
                        }
                        catch { done.Set(); }
                    });
                }
                catch
                {
                    return ApprovalChoice.Deny; // UI gone (e.g. closing) -> safe default
                }

                done.WaitOne();
            }
            return result[0];
        }
    }
}
