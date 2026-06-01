using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A thin strip docked at the top of each chat tab, above its transcript, showing that
    // conversation's working folder (the MCP files/git/command sandbox root, GXPT_WORKDIR) with a
    // link to set / change / clear it.
    //
    // Styling note: this intentionally uses FIXED colors and does NOT follow the app's light/dark
    // theme — it is meant to match the tab strip above it (which is also un-themed system chrome).
    internal sealed class WorkspaceContextStrip : Panel
    {
        // Fixed palette (does not follow the app theme).
        private static readonly Color SetBack = Color.FromArgb(237, 244, 237);   // subtle green-grey
        private static readonly Color UnsetBack = Color.FromArgb(252, 246, 220); // cream / warning
        private static readonly Color TextColor = Color.FromArgb(55, 55, 55);
        private static readonly Color LinkColor = Color.FromArgb(0, 90, 158);

        private readonly Label _text;
        private readonly LinkLabel _change;
        private readonly LinkLabel _clear;

        public event EventHandler ChangeRequested;
        public event EventHandler ClearRequested;

        public WorkspaceContextStrip()
        {
            this.Dock = DockStyle.Top;
            this.Height = 26;
            this.Padding = new Padding(8, 0, 8, 0);

            _change = new LinkLabel();
            _change.AutoSize = true;
            _change.Dock = DockStyle.Right;
            _change.TextAlign = ContentAlignment.MiddleRight;
            _change.LinkColor = LinkColor;
            _change.ActiveLinkColor = LinkColor;
            _change.VisitedLinkColor = LinkColor;
            _change.LinkBehavior = LinkBehavior.HoverUnderline;
            _change.LinkClicked += delegate { if (ChangeRequested != null) ChangeRequested(this, EventArgs.Empty); };

            _clear = new LinkLabel();
            _clear.AutoSize = true;
            _clear.Dock = DockStyle.Right;
            _clear.Text = "Clear";
            _clear.TextAlign = ContentAlignment.MiddleRight;
            _clear.LinkColor = LinkColor;
            _clear.ActiveLinkColor = LinkColor;
            _clear.VisitedLinkColor = LinkColor;
            _clear.LinkBehavior = LinkBehavior.HoverUnderline;
            _clear.Padding = new Padding(0, 0, 12, 0);
            _clear.Visible = false;
            _clear.LinkClicked += delegate { if (ClearRequested != null) ClearRequested(this, EventArgs.Empty); };

            _text = new Label();
            _text.Dock = DockStyle.Fill;
            _text.AutoEllipsis = true;
            _text.TextAlign = ContentAlignment.MiddleLeft;
            _text.ForeColor = TextColor;

            // Fill first, edge-docked controls after — matches the app's working docking order
            // (e.g. pnlInput). Do NOT BringToFront the strip itself or the Fill transcript stops
            // reserving space for it.
            this.Controls.Add(_text);
            this.Controls.Add(_clear);
            this.Controls.Add(_change);

            SetWorkingDir(null);
        }

        // Reflect the given working folder (null/empty => "no folder" warning state).
        public void SetWorkingDir(string dir)
        {
            bool has = !string.IsNullOrEmpty(dir);
            if (has)
            {
                this.BackColor = SetBack;
                _text.Text = "Working folder:  " + dir;
                _change.Text = "Change...";
                _clear.Visible = true;
            }
            else
            {
                this.BackColor = UnsetBack;
                _text.Text = "No working folder — file, git, and command tools are disabled for this conversation.";
                _change.Text = "Set folder...";
                _clear.Visible = false;
            }
        }
    }
}
