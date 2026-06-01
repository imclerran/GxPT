using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A thin strip docked at the top of the chat/transcript area showing the active conversation's
    // working folder (the MCP files/git/command sandbox root, GXPT_WORKDIR) with controls to set or
    // clear it. Raised events are handled by MainForm, which updates the tab context + the host.
    internal sealed class WorkspaceContextStrip : Panel
    {
        private readonly Label _icon;
        private readonly Label _path;
        private readonly LinkLabel _change;
        private readonly LinkLabel _clear;

        public event EventHandler ChangeRequested;
        public event EventHandler ClearRequested;

        public WorkspaceContextStrip()
        {
            this.Dock = DockStyle.Top;
            this.Height = 24;
            this.Padding = new Padding(8, 3, 8, 3);

            _icon = new Label();
            _icon.AutoSize = true;
            _icon.Dock = DockStyle.Left;
            _icon.Text = "Workspace:";
            _icon.TextAlign = ContentAlignment.MiddleLeft;

            _path = new Label();
            _path.AutoSize = false;
            _path.Dock = DockStyle.Fill;
            _path.AutoEllipsis = true;
            _path.TextAlign = ContentAlignment.MiddleLeft;
            _path.Padding = new Padding(6, 0, 6, 0);

            _change = new LinkLabel();
            _change.AutoSize = true;
            _change.Dock = DockStyle.Right;
            _change.Text = "Set folder";
            _change.TextAlign = ContentAlignment.MiddleRight;
            _change.LinkClicked += delegate { if (ChangeRequested != null) ChangeRequested(this, EventArgs.Empty); };

            _clear = new LinkLabel();
            _clear.AutoSize = true;
            _clear.Dock = DockStyle.Right;
            _clear.Text = "Clear";
            _clear.TextAlign = ContentAlignment.MiddleRight;
            _clear.Padding = new Padding(0, 0, 8, 0);
            _clear.Visible = false;
            _clear.LinkClicked += delegate { if (ClearRequested != null) ClearRequested(this, EventArgs.Empty); };

            // Fill must be added first so the docked siblings flank it correctly.
            this.Controls.Add(_path);
            this.Controls.Add(_change);
            this.Controls.Add(_clear);
            this.Controls.Add(_icon);
        }

        // Reflect the given working folder (null/empty => "no folder set").
        public void SetWorkingDir(string dir)
        {
            bool has = !string.IsNullOrEmpty(dir);
            _path.Text = has ? dir : "(no folder set — file, git, and command tools are disabled)";
            _path.ForeColor = has ? this.ForeColor : SystemColors.GrayText;
            _change.Text = has ? "Change" : "Set folder";
            _clear.Visible = has;
        }

        // Apply theme colors (called by MainForm's theme application).
        public void ApplyColors(Color back, Color fore)
        {
            this.BackColor = back;
            _icon.ForeColor = fore;
            _path.ForeColor = fore;
        }
    }
}
