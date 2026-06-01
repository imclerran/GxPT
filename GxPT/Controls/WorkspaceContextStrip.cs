using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A thin strip docked at the top of each chat tab, above its transcript, showing that
    // conversation's working folder (the MCP files/git/command sandbox root, GXPT_WORKDIR) with
    // links to set / change / clear it (and dismiss the strip when unset).
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
        private readonly FlowLayoutPanel _links;
        private readonly LinkLabel _change;
        private readonly LinkLabel _clear;
        private readonly LinkLabel _dismiss;

        public event EventHandler ChangeRequested;
        public event EventHandler ClearRequested;
        public event EventHandler DismissRequested;

        public WorkspaceContextStrip()
        {
            this.Dock = DockStyle.Top;
            this.Height = 26;
            this.Padding = new Padding(8, 0, 8, 0);

            _change = MakeLink("Set folder...", delegate { Raise(ChangeRequested); });
            _clear = MakeLink("Clear", delegate { Raise(ClearRequested); });
            _dismiss = MakeLink("Dismiss", delegate { Raise(DismissRequested); });

            // Links flow left-to-right in add order, right-docked as a group.
            _links = new FlowLayoutPanel();
            _links.Dock = DockStyle.Right;
            _links.FlowDirection = FlowDirection.LeftToRight;
            _links.WrapContents = false;
            _links.AutoSize = true;
            _links.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _links.Margin = new Padding(0);
            _links.Controls.Add(_change);
            _links.Controls.Add(_clear);
            _links.Controls.Add(_dismiss);

            _text = new Label();
            _text.Dock = DockStyle.Fill;
            _text.AutoEllipsis = true;
            _text.TextAlign = ContentAlignment.MiddleLeft;
            _text.ForeColor = TextColor;

            // Fill first, edge-docked controls after — matches the app's working docking order.
            this.Controls.Add(_text);
            this.Controls.Add(_links);

            SetWorkingDir(null);
        }

        private static LinkLabel MakeLink(string text, EventHandler onClick)
        {
            var lnk = new LinkLabel();
            lnk.AutoSize = true;
            lnk.Text = text;
            lnk.TextAlign = ContentAlignment.MiddleLeft;
            lnk.LinkColor = LinkColor;
            lnk.ActiveLinkColor = LinkColor;
            lnk.VisitedLinkColor = LinkColor;
            lnk.LinkBehavior = LinkBehavior.HoverUnderline;
            lnk.Margin = new Padding(10, 0, 0, 0);
            lnk.Anchor = AnchorStyles.None; // vertically centered in the flow panel
            lnk.LinkClicked += delegate { onClick(null, EventArgs.Empty); };
            return lnk;
        }

        private void Raise(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }

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
                _dismiss.Visible = false; // can't dismiss while a folder is set (use Clear)
            }
            else
            {
                this.BackColor = UnsetBack;
                _text.Text = "No working folder — file, git, and command tools are disabled for this conversation.";
                _change.Text = "Set folder...";
                _clear.Visible = false;
                _dismiss.Visible = true;
            }
        }
    }
}
