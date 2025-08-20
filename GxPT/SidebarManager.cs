﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GxPT
{
    internal sealed class SidebarManager
    {
        // Sidebar animation settings
        private const int SidebarMinWidth = 8;
        private const int SidebarMaxWidth = 240;
        private const int SidebarAnimIntervalMs = 10;
        private const int SidebarAnimDurationMs = 100;

        private readonly MainForm _mainForm;
        private readonly SplitContainer _splitContainer;
        private readonly ToolStripMenuItem _miConversationHistory;

        // Animation state
        private bool _sidebarExpanded;
        private bool _sidebarAnimating;
        private int _sidebarTargetWidth;
        private Timer _sidebarTimer;
        private Stopwatch _sidebarAnimWatch = new Stopwatch();
        private int _sidebarStartWidth;

        // UI components
        private ListView _lvConversations;
        private Panel _sidebarArrowPanel;
        private ImageList _lvRowHeightImages;
        private ContextMenuStrip _conversationContextMenu;
        private TextBox _renameTextBox;
        private Panel _renameHostPanel;
        private ListViewItem _renamingItem;

        // Open conversations tracking
        private readonly Dictionary<string, TabPage> _openConversationsById = new Dictionary<string, TabPage>();

        public event Action SidebarToggled;

        public SidebarManager(MainForm mainForm, SplitContainer splitContainer, ToolStripMenuItem miConversationHistory)
        {
            _mainForm = mainForm;
            _splitContainer = splitContainer;
            _miConversationHistory = miConversationHistory;

            InitializeSidebar();
            InitializeTimer();
            WireEvents();
        }

        public bool IsExpanded
        {
            get { return _sidebarExpanded; }
        }

        private void InitializeSidebar()
        {
            if (_splitContainer != null)
            {
                _splitContainer.FixedPanel = FixedPanel.Panel1;
                _splitContainer.IsSplitterFixed = true;
                _splitContainer.Panel1MinSize = SidebarMinWidth;
                _splitContainer.Panel2MinSize = 0;
                _splitContainer.SplitterWidth = 1;
                _splitContainer.SplitterDistance = SidebarMinWidth;
                _sidebarExpanded = false;

                EnsureSidebarList();
                EnsureSidebarArrowStrip();

                _splitContainer.Panel1.TabStop = true;
                _splitContainer.Panel1.PreviewKeyDown += Panel1_PreviewKeyDown;
            }
        }

        private void InitializeTimer()
        {
            _sidebarTimer = new Timer();
            _sidebarTimer.Interval = SidebarAnimIntervalMs;
            _sidebarTimer.Tick += SidebarTimer_Tick;
        }

        private void WireEvents()
        {
            if (_splitContainer != null && _splitContainer.Panel1 != null)
            {
                _splitContainer.Panel1.Resize += (s, e) =>
                {
                    if (_sidebarArrowPanel != null)
                        _sidebarArrowPanel.Invalidate();
                    LayoutSidebarChildren();
                };
            }

            if (_miConversationHistory != null)
            {
                _miConversationHistory.CheckOnClick = false;
                _miConversationHistory.Click += (s, e) => ToggleSidebar();
                UpdateConversationHistoryCheckedState();
            }
        }

        public void ToggleSidebar()
        {
            if (_splitContainer == null || _sidebarAnimating) return;

            _sidebarStartWidth = _splitContainer.SplitterDistance;
            _sidebarTargetWidth = _sidebarExpanded ? SidebarMinWidth : SidebarMaxWidth;
            _sidebarAnimating = true;

            try
            {
                _sidebarAnimWatch.Reset();
                _sidebarAnimWatch.Start();
            }
            catch { }

            _sidebarTimer.Start();
        }

        private void SidebarTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (_splitContainer == null)
                {
                    _sidebarTimer.Stop();
                    _sidebarAnimating = false;
                    return;
                }

                if (!_sidebarAnimWatch.IsRunning)
                    _sidebarAnimWatch.Start();

                long elapsed = _sidebarAnimWatch.ElapsedMilliseconds;
                double t = Math.Max(0.0, Math.Min(1.0, (double)elapsed / SidebarAnimDurationMs));
                double eased = EaseInOutCubic(t);

                int start = _sidebarStartWidth;
                int end = _sidebarTargetWidth;
                int next = (int)Math.Round(start + (end - start) * eased);

                int cur = _splitContainer.SplitterDistance;
                if (next != cur)
                {
                    _splitContainer.SuspendLayout();
                    try { _splitContainer.SplitterDistance = next; }
                    finally { _splitContainer.ResumeLayout(); }

                    if (_sidebarArrowPanel != null)
                    {
                        int h = _sidebarArrowPanel.ClientSize.Height;
                        var rect = new Rectangle(0, Math.Max(0, h / 2 - 20), _sidebarArrowPanel.Width, 40);
                        _sidebarArrowPanel.Invalidate(rect);
                    }
                    LayoutSidebarChildren();
                }

                if (t >= 1.0)
                {
                    try
                    {
                        _splitContainer.SuspendLayout();
                        if (_splitContainer.SplitterDistance != _sidebarTargetWidth)
                            _splitContainer.SplitterDistance = _sidebarTargetWidth;
                    }
                    finally { _splitContainer.ResumeLayout(); }

                    _sidebarTimer.Stop();
                    _sidebarAnimWatch.Stop();
                    _sidebarAnimating = false;
                    _sidebarExpanded = (_sidebarTargetWidth >= SidebarMaxWidth);

                    if (_sidebarArrowPanel != null)
                        _sidebarArrowPanel.Invalidate();
                    LayoutSidebarChildren();
                    UpdateConversationHistoryCheckedState();

                    if (SidebarToggled != null) SidebarToggled();
                }
            }
            catch
            {
                try { _sidebarTimer.Stop(); }
                catch { }
                try { _sidebarAnimWatch.Stop(); }
                catch { }
                _sidebarAnimating = false;
            }
        }

        private static double EaseInOutCubic(double t)
        {
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        private void Panel1_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                ToggleSidebar();
                e.IsInputKey = true;
            }
        }

        private void Panel1_ClickToggle(object sender, EventArgs e)
        {
            try
            {
                if (_sidebarArrowPanel != null && _sidebarExpanded)
                {
                    var me = e as MouseEventArgs;
                    if (me != null)
                    {
                        int half = _sidebarArrowPanel.Width / 2;
                        if (me.X < half) return;
                    }
                }
            }
            catch { }
            ToggleSidebar();
        }

        private void EnsureSidebarArrowStrip()
        {
            try
            {
                if (_sidebarArrowPanel != null) return;

                _sidebarArrowPanel = new Panel();
                _sidebarArrowPanel.Width = 14;
                _sidebarArrowPanel.Dock = DockStyle.Right;
                _sidebarArrowPanel.Margin = new Padding(0);
                _sidebarArrowPanel.Padding = new Padding(0);
                _sidebarArrowPanel.Cursor = Cursors.Hand;
                _sidebarArrowPanel.BackColor = _splitContainer.Panel1.BackColor;
                _sidebarArrowPanel.Paint += Panel1_PaintArrow;
                _sidebarArrowPanel.Click += Panel1_ClickToggle;
                _sidebarArrowPanel.PreviewKeyDown += Panel1_PreviewKeyDown;
                _sidebarArrowPanel.TabStop = true;
                _splitContainer.Panel1.Controls.Add(_sidebarArrowPanel);
                _sidebarArrowPanel.BringToFront();
                LayoutSidebarChildren();
            }
            catch { }
        }

        private void Panel1_PaintArrow(object sender, PaintEventArgs e)
        {
            try
            {
                var p = _sidebarArrowPanel ?? (_splitContainer != null ? _splitContainer.Panel1 : null);
                if (p == null) return;

                int w = p.ClientSize.Width;
                int h = p.ClientSize.Height;
                if (w <= 0 || h <= 0) return;

                bool pointRight;
                if (_sidebarAnimating)
                {
                    pointRight = _sidebarTargetWidth > _splitContainer.SplitterDistance;
                }
                else
                {
                    pointRight = !_sidebarExpanded;
                }

                int arrowH = Math.Max(8, Math.Min(12, h / 20));
                int arrowW = Math.Max(5, arrowH / 2 + 2);
                int cy = h / 2;
                int paddingRight = 1;
                int cxRight = Math.Max(arrowW + 1, Math.Min(w - paddingRight, w));
                int cxLeft = Math.Max(arrowW + 1, Math.Min(w - paddingRight, w));

                using (var sb = new SolidBrush(Color.DimGray))
                {
                    var oldMode = e.Graphics.SmoothingMode;
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    Point[] tri;
                    if (pointRight)
                    {
                        tri = new[]
                        {
                            new Point(cxRight - arrowW, cy - arrowH/2),
                            new Point(cxRight - arrowW, cy + arrowH/2),
                            new Point(cxRight,            cy)
                        };
                    }
                    else
                    {
                        int cx = cxLeft - 1;
                        tri = new[]
                        {
                            new Point(cx,            cy - arrowH/2),
                            new Point(cx,            cy + arrowH/2),
                            new Point(cx - arrowW,   cy)
                        };
                    }
                    e.Graphics.FillPolygon(sb, tri);
                    e.Graphics.SmoothingMode = oldMode;
                }
            }
            catch { }
        }

        private void EnsureSidebarList()
        {
            try
            {
                if (_lvConversations != null) return;

                _lvConversations = new ListView();
                _lvConversations.View = View.Details;
                _lvConversations.FullRowSelect = true;
                _lvConversations.HideSelection = false;
                _lvConversations.HeaderStyle = ColumnHeaderStyle.None;
                _lvConversations.BorderStyle = BorderStyle.None;
                _lvConversations.Dock = DockStyle.Left;
                _lvConversations.Columns.Add("Conversation", 200, HorizontalAlignment.Left);
                _lvConversations.MultiSelect = false;
                _lvConversations.ItemActivate += LvConversations_ItemActivate;
                _lvConversations.MouseUp += LvConversations_MouseUp;
                _lvConversations.Click += LvConversations_Click;

                // Create context menu but don't assign it directly
                _conversationContextMenu = new ContextMenuStrip();
                var miOpen = new ToolStripMenuItem("Open");
                var miRename = new ToolStripMenuItem("Rename");
                var miDelete = new ToolStripMenuItem("Delete");
                var deleteImage = ResourceHelper.GetAssemblyImage("ExplorerDelete.png");
                miOpen.Click += (s, e) => TryOpenSelectedConversation();
                miRename.Click += (s, e) => StartRenameSelectedConversation();
                miDelete.Click += (s, e) => DeleteSelectedConversation();
                miDelete.Image = deleteImage;
                _conversationContextMenu.Items.Add(miOpen);
                _conversationContextMenu.Items.Add(miRename);
                _conversationContextMenu.Items.Add(new ToolStripSeparator());
                _conversationContextMenu.Items.Add(miDelete);

                // No longer need to store in Tag

                _lvConversations.BackColor = _splitContainer.Panel1.BackColor;
                _lvConversations.OwnerDraw = false;

                try
                {
                    if (_lvRowHeightImages == null)
                    {
                        _lvRowHeightImages = new ImageList();
                        int rowHeight = Math.Max(_lvConversations.Font.Height + 8, 22);
                        _lvRowHeightImages.ImageSize = new Size(1, rowHeight);
                    }
                    _lvConversations.SmallImageList = _lvRowHeightImages;
                }
                catch { }

                _lvConversations.Resize += (s, e) => ResizeSidebarColumn();
                _splitContainer.Panel1.Controls.Add(_lvConversations);

                RefreshSidebarList();
                LayoutSidebarChildren();
            }
            catch { }
        }

        public void RefreshSidebarList()
        {
            try
            {
                if (_lvConversations == null) return;
                var items = ConversationStore.ListAll();
                _lvConversations.BeginUpdate();
                try
                {
                    _lvConversations.Items.Clear();
                    foreach (var it in items)
                    {
                        string text = string.IsNullOrEmpty(it.Name) ? "New Conversation" : it.Name;
                        var lvi = new ListViewItem(text);
                        lvi.Tag = it;
                        _lvConversations.Items.Add(lvi);
                    }
                    ResizeSidebarColumn();
                }
                finally
                {
                    _lvConversations.EndUpdate();
                }
            }
            catch { }
        }

        private void LayoutSidebarChildren()
        {
            try
            {
                if (_splitContainer == null || _lvConversations == null) return;
                int arrowW = (_sidebarArrowPanel != null ? _sidebarArrowPanel.Width : 0);
                int panelW = _splitContainer.Panel1.ClientSize.Width;
                int targetW = Math.Max(0, panelW - arrowW);
                if (_lvConversations.Dock != DockStyle.Left) _lvConversations.Dock = DockStyle.Left;
                if (_lvConversations.Width != targetW) _lvConversations.Width = targetW;
            }
            catch { }
        }

        private void LvConversations_ItemActivate(object sender, EventArgs e)
        {
            TryOpenSelectedConversation();
        }

        private void LvConversations_Click(object sender, EventArgs e)
        {
            // If we're renaming and user clicks elsewhere, finish the rename
            if (_renamingItem != null)
            {
                var me = e as MouseEventArgs;
                if (me != null)
                {
                    var hitTest = _lvConversations.HitTest(me.Location);
                    // If clicking on a different item or empty space, finish rename
                    if (hitTest.Item != _renamingItem)
                    {
                        FinishRename(true);
                    }
                }
            }
        }

        private void LvConversations_MouseUp(object sender, MouseEventArgs e)
        {
            // Only show context menu for right-clicks that hit an actual item
            if (e.Button == MouseButtons.Right)
            {
                try
                {
                    var hitTest = _lvConversations.HitTest(e.Location);
                    if (hitTest.Item != null)
                    {
                        // Select the item that was right-clicked
                        hitTest.Item.Selected = true;

                        // Show the context menu
                        if (_conversationContextMenu != null)
                        {
                            _conversationContextMenu.Show(_lvConversations, e.Location);
                        }
                    }
                }
                catch { }
            }
        }

        private void TryOpenSelectedConversation()
        {
            try
            {
                if (_lvConversations == null || _lvConversations.SelectedItems.Count == 0) return;
                var lvi = _lvConversations.SelectedItems[0];
                var info = lvi.Tag as ConversationStore.ConversationListItem;
                if (info == null) return;

                TabPage page;
                if (!string.IsNullOrEmpty(info.Id) && _openConversationsById.TryGetValue(info.Id, out page))
                {
                    _mainForm.SelectTab(page);
                    return;
                }

                var convo = ConversationStore.Load(_mainForm.GetClient(), info.Path);
                if (convo == null) return;

                _mainForm.OpenConversation(convo);
            }
            catch { }
        }

        private void DeleteSelectedConversation()
        {
            try
            {
                if (_lvConversations == null || _lvConversations.SelectedItems.Count == 0) return;
                var lvi = _lvConversations.SelectedItems[0];
                var info = lvi.Tag as ConversationStore.ConversationListItem;
                if (info == null) return;

                TabPage openPage;
                if (!string.IsNullOrEmpty(info.Id) && _openConversationsById.TryGetValue(info.Id, out openPage))
                {
                    _mainForm.CloseTab(openPage);
                }

                ConversationStore.DeletePath(info.Path);
                RefreshSidebarList();
            }
            catch { }
        }

        private void StartRenameSelectedConversation()
        {
            try
            {
                if (_lvConversations == null || _lvConversations.SelectedItems.Count == 0) return;
                var lvi = _lvConversations.SelectedItems[0];
                var info = lvi.Tag as ConversationStore.ConversationListItem;
                if (info == null) return;

                // Don't allow multiple renames at once
                if (_renamingItem != null) return;

                _renamingItem = lvi;

                // Create textbox for inline editing
                _renameTextBox = new TextBox();
                _renameTextBox.Text = lvi.Text;
                _renameTextBox.BorderStyle = BorderStyle.None;
                // Allow explicit height by using multiline (single-line behavior preserved by AcceptsReturn=false)
                _renameTextBox.Multiline = true;
                _renameTextBox.AcceptsReturn = false;
                _renameTextBox.AutoSize = false;
                _renameTextBox.Margin = new Padding(0);
                _renameTextBox.BackColor = _lvConversations.BackColor;
                _renameTextBox.ForeColor = _lvConversations.ForeColor;

                // Apply the same font size from settings that the ListView uses
                try
                {
                    double fs = AppSettings.GetDouble("font_size", 0);
                    if (fs > 0)
                    {
                        float size = (float)Math.Max(6, Math.Min(48, fs));
                        _renameTextBox.Font = new Font(_lvConversations.Font.FontFamily, size, _lvConversations.Font.Style);
                    }
                    else
                    {
                        _renameTextBox.Font = _lvConversations.Font;
                    }
                }
                catch
                {
                    _renameTextBox.Font = _lvConversations.Font;
                }

                // Position a host panel to cover the entire row, then place the textbox inside aligned to the label area
                Rectangle rowRect;
                Rectangle labelRect;
                try
                {
                    rowRect = _lvConversations.GetItemRect(lvi.Index, ItemBoundsPortion.Entire);
                }
                catch { rowRect = lvi.Bounds; }
                try
                {
                    labelRect = _lvConversations.GetItemRect(lvi.Index, ItemBoundsPortion.Label);
                }
                catch { labelRect = lvi.Bounds; }

                int left = Math.Max(0, labelRect.X);
                int panelTop = Math.Max(0, rowRect.Y);
                int panelHeight = Math.Max(1, rowRect.Height);

                // Create/position host panel to cover the row
                _renameHostPanel = new Panel();
                _renameHostPanel.Margin = new Padding(0);
                _renameHostPanel.Padding = new Padding(0);
                _renameHostPanel.BackColor = _lvConversations.BackColor;
                _renameHostPanel.Bounds = new Rectangle(0, panelTop, _lvConversations.ClientSize.Width, panelHeight);

                // Measure single line text height for vertical centering
                int textHeight;
                try { textHeight = TextRenderer.MeasureText("Ag", _renameTextBox.Font).Height; }
                catch { textHeight = _renameTextBox.Font.Height + 2; }
                int tbHeight = Math.Min(panelHeight, Math.Max(_renameTextBox.Font.Height + 2, textHeight));
                int tbTop = Math.Max(0, (panelHeight - tbHeight) / 2);
                int tbWidth = Math.Max(20, _lvConversations.ClientSize.Width - left);
                _renameTextBox.Bounds = new Rectangle(left, tbTop, tbWidth, tbHeight);

                // Wire up events
                _renameTextBox.KeyDown += RenameTextBox_KeyDown;
                _renameTextBox.LostFocus += RenameTextBox_LostFocus;

                // Add to host panel, then to ListView and focus
                _renameHostPanel.Controls.Add(_renameTextBox);
                _lvConversations.Controls.Add(_renameHostPanel);
                _renameHostPanel.BringToFront();
                _renameTextBox.BringToFront();
                _renameTextBox.SelectAll();
                _renameTextBox.Focus();
            }
            catch { }
        }

        private void RenameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                FinishRename(true);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                FinishRename(false);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void RenameTextBox_LostFocus(object sender, EventArgs e)
        {
            FinishRename(true);
        }

        private void FinishRename(bool saveChanges)
        {
            try
            {
                if (_renameTextBox == null || _renamingItem == null) return;

                string newName = saveChanges ? _renameTextBox.Text.Trim() : null;
                string originalName = _renamingItem.Text;

                // Remove the textbox and its host panel
                try
                {
                    if (_renameTextBox != null)
                    {
                        if (_renameTextBox.Parent != null) _renameTextBox.Parent.Controls.Remove(_renameTextBox);
                        _renameTextBox.Dispose();
                    }
                }
                catch { }
                _renameTextBox = null;
                try
                {
                    if (_renameHostPanel != null)
                    {
                        _lvConversations.Controls.Remove(_renameHostPanel);
                        _renameHostPanel.Dispose();
                    }
                }
                catch { }
                _renameHostPanel = null;

                // Only update if we're saving changes, the new name is valid, and it's actually different
                if (saveChanges && !string.IsNullOrEmpty(newName) && newName != originalName)
                {
                    var info = _renamingItem.Tag as ConversationStore.ConversationListItem;
                    if (info != null)
                    {
                        // Load the conversation, update the name, and save it
                        var conversation = ConversationStore.Load(_mainForm.GetClient(), info.Path);
                        if (conversation != null)
                        {
                            conversation.Name = newName;
                            ConversationStore.Save(conversation);

                            // Update any open tab with this conversation
                            TabPage openPage;
                            if (!string.IsNullOrEmpty(info.Id) && _openConversationsById.TryGetValue(info.Id, out openPage))
                            {
                                openPage.Text = newName;
                                _mainForm.UpdateWindowTitle();
                            }

                            // When saving changes (Enter pressed), allow the list to resort by refreshing
                            _renamingItem = null;
                            RefreshSidebarList();
                            return;
                        }
                    }
                }

                // If we didn't save changes (Escape), no changes were made, or failed to save,
                // just clear the rename state without refreshing the list (preserves position and timestamp)
                _renamingItem = null;
            }
            catch { }
        }

        private void ResizeSidebarColumn()
        {
            try
            {
                if (_lvConversations == null || _lvConversations.Columns.Count == 0) return;
                // Make the column fill the ListView's client width to avoid right-side gaps
                int target = Math.Max(20, _lvConversations.ClientSize.Width);
                _lvConversations.Columns[0].Width = target;
            }
            catch { }
        }

        private void UpdateConversationHistoryCheckedState()
        {
            try
            {
                if (_miConversationHistory != null)
                {
                    _miConversationHistory.Checked = _sidebarExpanded;
                }
            }
            catch { }
        }

        public void TrackOpenConversation(string conversationId, TabPage page)
        {
            try
            {
                if (!string.IsNullOrEmpty(conversationId))
                    _openConversationsById[conversationId] = page;
            }
            catch { }
        }

        public void UntrackOpenConversation(TabPage page)
        {
            try
            {
                var toRemove = _openConversationsById.Where(kv => object.ReferenceEquals(kv.Value, page))
                    .Select(kv => kv.Key).ToList();
                foreach (var k in toRemove)
                    _openConversationsById.Remove(k);
            }
            catch { }
        }

        public void ApplyFontSetting()
        {
            try
            {
                if (_lvConversations == null) return;
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));

                try { _lvConversations.Font = new Font(_lvConversations.Font.FontFamily, size, _lvConversations.Font.Style); }
                catch { }

                if (_lvRowHeightImages == null)
                    _lvRowHeightImages = new ImageList();

                int rowHeight = Math.Max(_lvConversations.Font.Height + 8, 22);
                _lvRowHeightImages.ImageSize = new Size(1, rowHeight);

                try
                {
                    var current = _lvConversations.SmallImageList;
                    _lvConversations.SmallImageList = null;
                    _lvConversations.SmallImageList = _lvRowHeightImages;
                }
                catch { }

                try { ResizeSidebarColumn(); }
                catch { }
                try { _lvConversations.Invalidate(); _lvConversations.Update(); }
                catch { }
            }
            catch { }
        }
    }
}
