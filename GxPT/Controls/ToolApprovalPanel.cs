using System;
using System.Drawing;
using System.IO;
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
        private readonly DiffPreviewPanel _diffPanel;   // shown instead of _preview for files__edit
        private readonly Font _monoFont;
        private readonly FlowLayoutPanel _buttons;

        private Action<ApprovalChoice> _onChoose;
        private Action<bool> _onContinue;   // set instead of _onChoose for the iteration-cap prompt

        // Supplies the active workspace root so an edit approval can show a few lines of real file
        // context around the change. Set by the host (MainForm); null disables context (bare diff).
        public Func<string> WorkingDirProvider;

        // The details area (diff/preview) is sized to its content between these bounds: short prompts
        // (e.g. a one-line command) collapse instead of leaving a tall empty box, while long content
        // is capped here and scrolls. See LayoutToContent.
        private const int MinDetailsHeight = 24;
        private const int MaxDetailsHeight = 200;

        // Clamped content height for the current prompt; the panel height is rebuilt from this plus
        // the (possibly multi-row) button strip whenever the panel resizes. _handledDetails records
        // whether the active prompt uses the diff panel (vs the raw preview) so the height can be
        // re-measured at the new width on resize. Re-entrancy guard stops the height we set from
        // recursing through OnSizeChanged.
        private int _detailsHeight = MinDetailsHeight;
        private bool _handledDetails;
        private bool _inLayoutToContent;

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

            _monoFont = new Font("Consolas", 9F);

            _preview = new TextBox();
            _preview.Multiline = true;
            _preview.ReadOnly = true;
            _preview.ScrollBars = ScrollBars.Vertical;
            _preview.Dock = DockStyle.Fill;
            _preview.Font = _monoFont;

            _diffPanel = new DiffPreviewPanel();
            _diffPanel.Dock = DockStyle.Fill;
            _diffPanel.Visible = false;

            _buttons = new FlowLayoutPanel();
            _buttons.Dock = DockStyle.Bottom;
            _buttons.FlowDirection = FlowDirection.RightToLeft;
            // Wrap onto extra rows when the window is too narrow to fit every button on one line, and
            // AutoSize so the strip grows upward (shrinking the preview above) to keep them all
            // visible. The old fixed-height, no-wrap, AutoScroll setup let a horizontal scrollbar
            // appear on resize and slice the buttons' bottoms off — and the scroll offset persisted,
            // so they stayed cut off after the resize.
            _buttons.WrapContents = true;
            _buttons.AutoScroll = false;
            _buttons.AutoSize = true;
            _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _buttons.MinimumSize = new Size(0, 34);

            // Order added (Fill must be added before docked siblings to lay out correctly):
            this.Controls.Add(_diffPanel);
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

            // files__edit -> a colored diff (with a little live file context); command__run -> the
            // command line, syntax-highlighted. Either replaces the raw JSON preview.
            bool handled = false;
            if (req.Arguments != null)
            {
                bool dark = false;
                try
                {
                    string th = AppSettings.GetString("theme");
                    dark = !string.IsNullOrEmpty(th) && th.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                }
                catch { }
                ThemeColors tc = ThemeService.GetColors(dark);

                if (string.Equals(req.FunctionName, "files__edit", StringComparison.Ordinal))
                {
                    string path = req.Arguments.Value<string>("path") ?? string.Empty;
                    string oldS = req.Arguments.Value<string>("old_string") ?? string.Empty;
                    string newS = req.Arguments.Value<string>("new_string") ?? string.Empty;
                    string workdir = WorkingDirProvider != null ? WorkingDirProvider() : null;
                    string fileText = ReadWorkspaceFile(workdir, path);
                    LineDiffResult diff = DiffUtil.BuildLineDiffWithContext(fileText, oldS, newS, 3);
                    _diffPanel.SetContent(path, diff.Body, "diff", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Diff:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "command__run", StringComparison.Ordinal))
                {
                    string cmd = req.Arguments.Value<string>("command") ?? string.Empty;
                    if (cmd.Trim().Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, cmd, "batch", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Command:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "files__write", StringComparison.Ordinal))
                {
                    string path = req.Arguments.Value<string>("path") ?? string.Empty;
                    string content = req.Arguments.Value<string>("content") ?? string.Empty;
                    string lang = SyntaxHighlighter.GetLanguageForFileName(path) ?? "text";
                    _diffPanel.SetContent(path, content, lang, dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Write:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "git__commit", StringComparison.Ordinal))
                {
                    string msg = req.Arguments.Value<string>("message") ?? string.Empty;
                    if (msg.Trim().Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, msg, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Commit message:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "web__extract", StringComparison.Ordinal))
                {
                    string urls = JoinUrlArgs(req.Arguments);
                    if (urls.Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, urls, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Fetch URLs:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "web__http", StringComparison.Ordinal))
                {
                    string method = (req.Arguments.Value<string>("method") ?? "GET").Trim().ToUpperInvariant();
                    if (method.Length == 0) method = "GET";
                    string url = req.Arguments.Value<string>("url") ?? string.Empty;
                    string body = req.Arguments.Value<string>("body") ?? string.Empty;
                    string text = method + " " + url;
                    if (body.Trim().Length > 0) text += "\r\n\r\n" + body;
                    _diffPanel.SetContent(string.Empty, text, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "HTTP request:";
                    handled = true;
                }
                else if (string.Equals(req.FunctionName, "files__delete", StringComparison.Ordinal))
                {
                    string path = req.Arguments.Value<string>("path") ?? string.Empty;
                    if (path.Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, path, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Delete:";
                        handled = true;
                    }
                }
                else if (string.Equals(req.FunctionName, "git__push", StringComparison.Ordinal))
                {
                    string remote = req.Arguments.Value<string>("remote") ?? string.Empty;
                    string branch = req.Arguments.Value<string>("branch") ?? string.Empty;
                    string tgt = remote.Length > 0 ? (branch.Length > 0 ? remote + "/" + branch : remote) : (branch.Length > 0 ? branch : "(default remote/branch)");
                    _diffPanel.SetContent(string.Empty, tgt, "text", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                    _previewLabel.Text = "Push to:";
                    handled = true;
                }
                else if (req.FunctionName != null && req.FunctionName.StartsWith("git__", StringComparison.Ordinal))
                {
                    // Other git tools: show the equivalent command line so a destructive op (reset
                    // --hard, rm, rebase, ...) is legible before approving.
                    string summary = GitOpSummary(req);
                    if (summary.Length > 0)
                    {
                        _diffPanel.SetContent(string.Empty, summary, "batch", dark, _monoFont, tc.CodeBack, tc.UiForeground);
                        _previewLabel.Text = "Git command:";
                        handled = true;
                    }
                }
            }

            if (handled)
            {
                _preview.Visible = false;
                _diffPanel.Visible = true;
            }
            else
            {
                _preview.Text = BuildPreviewText(req);
                _previewLabel.Text = "Details:";
                _diffPanel.Visible = false;
                _preview.Visible = true;
            }

            _buttons.Controls.Clear();
            // Deny is always present (added first => rightmost in RightToLeft flow).
            AddButton("Deny", ApprovalChoice.Deny, tier == ToolTier.Destructive);
            AddRememberButtons(req);
            AddButton("Allow once", ApprovalChoice.AllowOnce, false);

            // Size to fit the content (buttons built above so their height is counted, including any
            // wrapping at the current width; LayoutToContent measures the details at the live width).
            _handledDetails = handled;
            LayoutToContent();

            this.Visible = true;
            // Keep this Bottom-docked panel BEHIND the Fill transcript in z-order. WinForms fills the
            // remaining space with the front-most Fill control, so the panel must stay back for the
            // transcript to shrink *above* it (rather than the panel overlaying the transcript's
            // bottom edge and its right-docked scrollbar). BringToFront would cause exactly that.
            this.SendToBack();
        }

        // Iteration-cap confirmation, reusing this docked panel so it reads like the tool-approval
        // prompt. callback(true) => grant another batch, callback(false) => stop (wrap up). Marshalled
        // onto the UI thread by TranscriptContinuationPrompt; the click signals the blocked worker.
        public void ShowContinuation(int iterationsSoFar, Action<bool> callback)
        {
            _onChoose = null;
            _onContinue = callback;

            _header.Text = "Tool-call limit reached";
            _tierBadge.Text = "Paused after " + iterationsSoFar + " tool iteration(s) this turn";
            _tierBadge.ForeColor = Color.DarkGoldenrod;

            _previewLabel.Text = "Details:";
            _preview.Text = "The agent has been working for a long time. Do you want to continue?\r\n\r\n"
                + "Choose Continue to let it keep working, or Stop to have it summarize progress and "
                + "ask how you'd like to proceed.";
            _diffPanel.Visible = false;
            _preview.Visible = true;

            _buttons.Controls.Clear();
            // Added first => rightmost in the RightToLeft flow.
            AddContinuationButton("Stop", false, false);
            AddContinuationButton("Continue", true, true);

            _handledDetails = false;
            LayoutToContent();

            this.Visible = true;
            this.SendToBack();
        }

        public void HidePanel()
        {
            this.Visible = false;
            _onChoose = null;
            _onContinue = null;
        }

        // The clamped natural height of the current details control's content, in [Min,Max].
        private int ClampedDetailsHeight(bool handled)
        {
            int detailsContent = handled
                ? _diffPanel.GetPreferredContentHeight(DiffAvailableWidth())
                : MeasurePreviewContentHeight();
            return Math.Max(MinDetailsHeight, Math.Min(MaxDetailsHeight, detailsContent));
        }

        // Width the diff panel has for its content, used to predict whether a horizontal scrollbar
        // will appear (so its height can be reserved). The diff panel fills this panel minus padding;
        // prefer its actual width, with fallbacks for when it hasn't been laid out yet.
        private int DiffAvailableWidth()
        {
            // The approval panel's own client width is authoritative and current (even mid-resize,
            // when the Fill child's width may lag); the diff panel fills it minus padding.
            int w = this.ClientSize.Width - this.Padding.Horizontal;
            if (w <= 0 && _diffPanel != null) w = _diffPanel.ClientSize.Width;
            if (w <= 0 && this.Parent != null) w = this.Parent.ClientSize.Width - this.Padding.Horizontal;
            return w > 0 ? w : 400;
        }

        // Rebuild the panel height so the Fill details control keeps its clamped content height even
        // as the button strip wraps to more rows on a narrow window: the panel grows to fit the taller
        // strip instead of squeezing (and clipping) the details. Capped so it can't swallow the
        // transcript above it; past the cap the details give way and scroll. Recomputed on every
        // resize because button wrapping depends on the current width.
        private void LayoutToContent()
        {
            if (_inLayoutToContent) return;
            _inLayoutToContent = true;
            try
            {
                // Re-measure the details at the current width: a horizontal scrollbar (long command)
                // appears/disappears as the panel widens or narrows, and that changes the height needed.
                _detailsHeight = ClampedDetailsHeight(_handledDetails);

                int avail = this.ClientSize.Width - this.Padding.Horizontal;
                if (avail < 1) avail = (this.Parent != null ? this.Parent.ClientSize.Width : 400) - this.Padding.Horizontal;
                if (avail < 1) avail = 400;

                // GetPreferredSize at the real width includes any row wrapping of the buttons.
                int buttonsH = _buttons.GetPreferredSize(new Size(avail, 0)).Height;
                if (buttonsH < 28) buttonsH = 34; // guard against a not-yet-measured strip

                int chrome = _header.Height + _tierBadge.Height + _previewLabel.Height;
                const int border = 2; // FixedSingle: 1px top + 1px bottom
                int target = this.Padding.Top + this.Padding.Bottom + chrome + _detailsHeight + buttonsH + border;

                // Don't let the docked panel grow tall enough to bury the transcript above it.
                if (this.Parent != null)
                {
                    int cap = this.Parent.ClientSize.Height - 64;
                    if (cap > 120 && target > cap) target = cap;
                }

                if (this.Height != target) this.Height = target;
            }
            finally { _inLayoutToContent = false; }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // Width changes (parent resize) can re-wrap the buttons; keep the panel tall enough.
            if (this.Visible) LayoutToContent();
        }

        // Approximate wrapped height of the raw-JSON preview text at the current width. Best-effort:
        // the TextBox scrolls if we under-shoot, and the caller clamps the result anyway.
        private int MeasurePreviewContentHeight()
        {
            string text = _preview.Text != null ? _preview.Text : string.Empty;
            if (text.Length == 0) return MinDetailsHeight;
            int width = this.ClientSize.Width - this.Padding.Horizontal;
            if (width < 50 && this.Parent != null) width = this.Parent.ClientSize.Width - this.Padding.Horizontal;
            if (width < 50) width = 400;
            Size sz = TextRenderer.MeasureText(text, _preview.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak);
            return sz.Height + 8;
        }

        // If the panel is torn down (e.g. its tab is closed) while a call still awaits a decision,
        // resolve the pending request as Deny so the blocked tool-loop worker is released rather than
        // left waiting on the signal forever.
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Action<ApprovalChoice> cb = _onChoose;
                _onChoose = null;
                if (cb != null)
                {
                    try { cb(ApprovalChoice.Deny); }
                    catch { }
                }

                // A pending continuation prompt resolves to "stop" so the blocked worker is released
                // (wraps up) rather than left waiting forever.
                Action<bool> cc = _onContinue;
                _onContinue = null;
                if (cc != null)
                {
                    try { cc(false); }
                    catch { }
                }
            }
            base.Dispose(disposing);
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

        private void AddContinuationButton(string text, bool cont, bool defaultFocus)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(4, 4, 4, 4);
            b.Click += delegate
            {
                Action<bool> cb = _onContinue;
                HidePanel();
                if (cb != null) cb(cont);
            };
            _buttons.Controls.Add(b);
            if (defaultFocus) { try { b.Select(); } catch { } }
        }

        // Reads a workspace-relative file for diff context. Mirrors the files sandbox: relative paths
        // only, must resolve inside the workspace root. Returns null on any failure (→ bare diff).
        private static string ReadWorkspaceFile(string workdir, string relPath)
        {
            if (string.IsNullOrEmpty(workdir) || string.IsNullOrEmpty(relPath)) return null;
            try
            {
                if (Path.IsPathRooted(relPath)) return null;
                string root = Path.GetFullPath(workdir);
                string full = Path.GetFullPath(Path.Combine(root, relPath));
                string rootSep = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
                if (!string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
                    !full.StartsWith(rootSep, StringComparison.OrdinalIgnoreCase)) return null;
                if (!File.Exists(full)) return null;
                return File.ReadAllText(full);
            }
            catch { return null; }
        }

        // One URL per line from the web__extract "urls" array argument.
        private static string JoinUrlArgs(Newtonsoft.Json.Linq.JObject args)
        {
            try
            {
                var arr = args["urls"] as Newtonsoft.Json.Linq.JArray;
                if (arr == null) return string.Empty;
                var sb = new System.Text.StringBuilder();
                foreach (var u in arr)
                {
                    string s = (string)u;
                    if (string.IsNullOrEmpty(s)) continue;
                    if (sb.Length > 0) sb.Append("\r\n");
                    sb.Append(s);
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        // A readable "git <subcommand> ..." line for the approval preview of the extended git tools.
        // Returns "" for tools handled elsewhere (commit/push) or unknown ones.
        private static string GitOpSummary(ApprovalRequest req)
        {
            Newtonsoft.Json.Linq.JObject a = req.Arguments != null ? req.Arguments : new Newtonsoft.Json.Linq.JObject();
            var sb = new System.Text.StringBuilder("git ");
            switch (req.FunctionName)
            {
                case "git__fetch":
                    sb.Append("fetch"); if (Bv(a, "prune")) sb.Append(" --prune"); Append(sb, Sv(a, "remote")); break;
                case "git__pull":
                    sb.Append("pull"); if (Bv(a, "rebase")) sb.Append(" --rebase"); Append(sb, Sv(a, "remote")); Append(sb, Sv(a, "branch")); break;
                case "git__checkout":
                    sb.Append("checkout"); if (Bv(a, "create")) sb.Append(" -b"); Append(sb, Sv(a, "ref")); Append(sb, Sv(a, "start_point")); break;
                case "git__restore":
                    sb.Append("restore"); if (Bv(a, "staged")) sb.Append(" --staged");
                    if (Sv(a, "source").Length > 0) sb.Append(" --source ").Append(Sv(a, "source"));
                    Append(sb, PathsOf(a, "paths")); break;
                case "git__branch":
                {
                    string act = Sv(a, "action"); if (act.Length == 0) act = "list";
                    sb.Append("branch ").Append(act); Append(sb, Sv(a, "name"));
                    if (Sv(a, "new_name").Length > 0) sb.Append(" -> ").Append(Sv(a, "new_name")); break;
                }
                case "git__merge":
                    sb.Append("merge"); if (Bv(a, "no_ff")) sb.Append(" --no-ff"); Append(sb, Sv(a, "branch")); break;
                case "git__rebase":
                {
                    string act = Sv(a, "action"); if (act.Length == 0) act = "start";
                    if (act == "start") { sb.Append("rebase"); Append(sb, Sv(a, "onto")); }
                    else sb.Append("rebase --").Append(act);
                    break;
                }
                case "git__cherry_pick":
                    sb.Append("cherry-pick"); if (Bv(a, "no_commit")) sb.Append(" -n"); Append(sb, Sv(a, "commit")); break;
                case "git__add":
                    sb.Append("add"); if (Bv(a, "all")) sb.Append(" -A"); else Append(sb, PathsOf(a, "paths")); break;
                case "git__reset":
                {
                    string paths = PathsOf(a, "paths");
                    if (paths.Length > 0) { sb.Append("reset"); Append(sb, Sv(a, "target")); sb.Append(" -- ").Append(paths); }
                    else { string m = Sv(a, "mode"); if (m.Length == 0) m = "mixed"; sb.Append("reset --").Append(m.ToLowerInvariant()); Append(sb, Sv(a, "target")); }
                    break;
                }
                case "git__rm":
                    sb.Append("rm"); if (Bv(a, "cached")) sb.Append(" --cached"); if (Bv(a, "recursive")) sb.Append(" -r"); Append(sb, PathsOf(a, "paths")); break;
                case "git__stash":
                {
                    string act = Sv(a, "action"); if (act.Length == 0) act = "push";
                    sb.Append("stash ").Append(act);
                    if (Sv(a, "message").Length > 0) sb.Append(" -m ").Append(Sv(a, "message"));
                    break;
                }
                default: return string.Empty;
            }
            return sb.ToString();
        }

        private static void Append(System.Text.StringBuilder sb, string v) { if (!string.IsNullOrEmpty(v)) sb.Append(' ').Append(v); }
        private static string Sv(Newtonsoft.Json.Linq.JObject a, string n) { var v = a.Value<string>(n); return v ?? string.Empty; }
        private static bool Bv(Newtonsoft.Json.Linq.JObject a, string n) { var t = a[n]; try { return t != null && (bool)t; } catch { return false; } }
        private static string PathsOf(Newtonsoft.Json.Linq.JObject a, string n)
        {
            var arr = a[n] as Newtonsoft.Json.Linq.JArray;
            if (arr == null) { string s = a.Value<string>(n); return s ?? string.Empty; }
            var sb = new System.Text.StringBuilder();
            foreach (var p in arr) { string s = (string)p; if (string.IsNullOrEmpty(s)) continue; if (sb.Length > 0) sb.Append(' '); sb.Append(s); }
            return sb.ToString();
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
