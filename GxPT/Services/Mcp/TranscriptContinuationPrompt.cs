using System;
using System.Threading;
using System.Windows.Forms;

namespace GxPT
{
    // Bridges the orchestrator's synchronous iteration-cap decision (called on the tool-loop worker
    // thread) to the docked ToolApprovalPanel on the UI thread, reusing the same blocking pattern as
    // TranscriptApprovalPrompt. Ask blocks the worker until the user clicks Continue/Stop; the turn
    // pauses at the cap, which is correct — the user is present and choosing whether to keep going.
    internal sealed class TranscriptContinuationPrompt
    {
        private readonly Control _uiMarshal;          // any control on the UI thread (the form)
        private readonly Func<ToolApprovalPanel> _getPanel;

        public TranscriptContinuationPrompt(Control uiMarshal, Func<ToolApprovalPanel> getPanel)
        {
            _uiMarshal = uiMarshal;
            _getPanel = getPanel;
        }

        // Returns true to continue (grant another budget), false to stop (wrap up). Defaults to false
        // if the UI is unavailable (e.g. the tab is closing) so the turn still ends cleanly.
        public bool Ask(int iterationsSoFar)
        {
            if (_uiMarshal == null || _getPanel == null) return false;

            bool[] result = { false };
            using (ManualResetEvent done = new ManualResetEvent(false))
            {
                try
                {
                    _uiMarshal.BeginInvoke((MethodInvoker)delegate
                    {
                        ToolApprovalPanel panel = _getPanel();
                        if (panel == null) { done.Set(); return; }
                        panel.ShowContinuation(iterationsSoFar, delegate(bool cont)
                        {
                            result[0] = cont;
                            done.Set();
                        });
                    });
                }
                catch
                {
                    return false; // UI gone (e.g. closing) -> stop and wrap up
                }

                done.WaitOne();
            }
            return result[0];
        }
    }
}
