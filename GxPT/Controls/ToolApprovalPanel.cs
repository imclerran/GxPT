using System;
using System.Drawing;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace GxPT
{
    // A native panel docked at the bottom of the chat area that asks the user to approve a pending
    // MCP tool call (approval spec §4, rendered in-transcript rather than as a modal). Shown only
    // while a call awaits a decision; the buttons offered depend on the tool's remember scope.
    //
    // Threading: the tool-loop worker calls Ask (blocking) via TranscriptApprovalPrompt, which
    // marshals ShowFor onto the UI thread; the user's button click signals back the chosen result.
    internal sealed class ToolApprovalPanel : Panel
    {
        private readonly Label _header;
        private readonly Label _tierBadge;
        private readonly Label _previewLabel;
        private readonly TextBox _preview;
        private readonly FlowLayoutPanel _buttons;

        private Action<ApprovalChoice> _onChoose;

        public ToolApprovalPanel()
        {
            this.Dock = DockStyle.Bottom;
            this.Visible = false;
            this.AutoSize = false;
            this.Height = 150;
            this.Padding = new Padding(8);
            this.BorderStyle = BorderStyle.FixedSingle;

            _header = new Label();
            _header.Dock = DockStyle.Top;
            _header.Height = 20;
            _header.Font = new Font(this.Font, FontStyle.Bold);
            _header.AutoEllipsis = true;

            _tierBadge = new Label();
            _tierBadge.Dock = DockStyle.Top;
            _tierBadge.Height = 18;

            _previewLabel = new Label();
            _previewLabel.Dock = DockStyle.Top;
            _previewLabel.Height = 16;
            _previewLabel.Text = "Details:";

            _preview = new TextBox();
            _preview.Multiline = true;
            _preview.ReadOnly = true;
            _preview.ScrollBars = ScrollBars.Vertical;
            _preview.Dock = DockStyle.Fill;
            _preview.Font = new Font("Consolas", 9F);

            _buttons = new FlowLayoutPanel();
            _buttons.Dock = DockStyle.Bottom;
            _buttons.Height = 34;
            _buttons.FlowDirection = FlowDirection.RightToLeft;
            _buttons.WrapContents = false;
            _buttons.AutoScroll = true;

            // Order added (Fill must be added before docked siblings to lay out correctly):
            this.Controls.Add(_preview);
            this.Controls.Add(_previewLabel);
            this.Controls.Add(_tierBadge);
            this.Controls.Add(_header);
            this.Controls.Add(_buttons);
        }

        // Populate + show for one request. choiceCallback is invoked (on the UI thread) with the
        // user's decision. Builds the scope-appropriate buttons per approval spec §4.
        public void ShowFor(ApprovalRequest req, Action<ApprovalChoice> choiceCallback)
        {
            _onChoose = choiceCallback;

            _header.Text = (req.ServerName != null ? req.ServerName : "?") + "  ·  " +
                           (req.ToolName != null ? req.ToolName : req.FunctionName);

            ToolTier tier = req.Policy != null ? req.Policy.Tier : ToolTier.Write;
            _tierBadge.Text = "Tier: " + tier;
            _tierBadge.ForeColor = (tier == ToolTier.Destructive) ? Color.Firebrick
                                  : (tier == ToolTier.Write ? Color.DarkGoldenrod : Color.ForestGreen);

            _preview.Text = BuildPreviewText(req);

            _buttons.Controls.Clear();
            // Deny is always present (added first => rightmost in RightToLeft flow).
            AddButton("Deny", ApprovalChoice.Deny, tier == ToolTier.Destructive);
            AddRememberButtons(req);
            AddButton("Allow once", ApprovalChoice.AllowOnce, false);

            this.Visible = true;
            this.BringToFront();
        }

        public void HidePanel()
        {
            this.Visible = false;
            _onChoose = null;
        }

        private void AddRememberButtons(ApprovalRequest req)
        {
            RememberScope scope = req.Policy != null ? req.Policy.Scope : RememberScope.Tool;
            string argPath = req.Policy != null ? req.Policy.ScopeArgPath : null;

            if (scope == RememberScope.Tool)
            {
                AddButton("Always allow this tool", ApprovalChoice.RememberTool, false);
            }
            else if (scope == RememberScope.Argument && argPath == "command")
            {
                AddButton("Always allow this exact command", ApprovalChoice.RememberExactArg, false);
                AddButton("Always allow this base command", ApprovalChoice.RememberPrefixArg, false);
            }
            else if (scope == RememberScope.Argument && argPath == "path")
            {
                AddButton("Always allow this directory", ApprovalChoice.RememberPrefixArg, false);
                AddButton("Always allow this file", ApprovalChoice.RememberExactArg, false);
            }
            // Scope == None: no remember buttons (Allow once / Deny only).
        }

        private void AddButton(string text, ApprovalChoice choice, bool defaultFocus)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(4, 4, 4, 4);
            b.Tag = choice;
            b.Click += delegate
            {
                Action<ApprovalChoice> cb = _onChoose;
                HidePanel();
                if (cb != null) cb(choice);
            };
            _buttons.Controls.Add(b);
            if (defaultFocus) { try { b.Select(); } catch { } }
        }

        private static string BuildPreviewText(ApprovalRequest req)
        {
            string preview = req.Preview != null ? req.Preview : string.Empty;
            string args = req.Arguments != null ? req.Arguments.ToString(Formatting.Indented) : "{}";
            if (!string.IsNullOrEmpty(preview) && preview != args)
                return preview + "\r\n\r\n" + args;
            return args;
        }
    }
}
