using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Cxt1024Bridge
{
    internal sealed class WindowsChooserRecord
    {
        internal string Kind, Id, Title;
        internal AiTaskState State;
        public override string ToString() { return Title; }
    }

    // Freeze identities when opening the panel; do not resolve a changed slot
    // number when the user confirms a row later.
    internal sealed class WindowsControllerChooser : Form
    {
        private readonly List<WindowsChooserRecord>[] _records;
        private readonly ComboBox _group = new ComboBox();
        private readonly ListBox _rows = new ListBox();
        private readonly Label _empty = new Label();
        private bool _selected;
        internal event Action<WindowsChooserRecord> Selected;

        internal WindowsControllerChooser(List<WindowsChooserRecord> tasks,
            List<WindowsChooserRecord> terminals, int layer)
        {
            _records = new[] { tasks, terminals };
            Text = "Bridge · 任务与会话"; ClientSize = new Size(590, 430);
            MinimumSize = new Size(490, 330); StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10); TopMost = true; KeyPreview = true;
            _group.DropDownStyle = ComboBoxStyle.DropDownList;
            _group.Items.AddRange(new object[] { "Codex 任务", "tmux 已有会话" });
            _group.SetBounds(18, 18, 554, 30); _group.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _group.SelectedIndexChanged += delegate { Reload(); };
            Controls.Add(_group);
            _rows.SetBounds(18, 64, 554, 310); _rows.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _rows.DrawMode = DrawMode.OwnerDrawFixed; _rows.ItemHeight = 48;
            _rows.DrawItem += DrawRow; _rows.DoubleClick += delegate { Confirm(); };
            Controls.Add(_rows);
            _empty.Text = "暂无可选项目；这里不会新建 tmux 会话";
            _empty.AutoSize = false; _empty.TextAlign = ContentAlignment.MiddleCenter;
            _empty.SetBounds(22, 170, 546, 64); _empty.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Controls.Add(_empty); _empty.BringToFront();
            var hint = new Label { Text = "↑ ↓ 选择    A / Enter 确认    B / Esc 返回    LB / RB 切组", AutoSize = false };
            hint.SetBounds(18, 390, 554, 26); hint.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(hint);
            _group.SelectedIndex = layer == 2 ? 1 : 0;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                string control = e.KeyCode == Keys.Enter ? "a" : e.KeyCode == Keys.Escape ? "b" :
                    e.KeyCode == Keys.Up ? "up" : e.KeyCode == Keys.Down ? "down" :
                    e.KeyCode == Keys.Left ? "lb" : e.KeyCode == Keys.Right ? "rb" : null;
                if (control != null) { HandleButton(control, true); e.Handled = true; e.SuppressKeyPress = true; }
            };
        }

        internal bool HandleButton(string button, bool down)
        {
            if (IsDisposed || !Visible) return false;
            if (!down || _selected) return true;
            switch (button)
            {
                case "a": Confirm(); break;
                case "b": case "menu": Close(); break;
                case "lb": case "left": _group.SelectedIndex = 0; break;
                case "rb": case "right": _group.SelectedIndex = 1; break;
                case "up": MoveSelection(-1); break;
                case "down": MoveSelection(1); break;
            }
            return true;
        }
        private void Reload()
        {
            _rows.Items.Clear();
            foreach (WindowsChooserRecord record in _records[Math.Max(0, _group.SelectedIndex)]) _rows.Items.Add(record);
            _empty.Visible = _rows.Items.Count == 0;
            if (_rows.Items.Count > 0) _rows.SelectedIndex = 0;
        }
        private void MoveSelection(int delta)
        {
            int count = _rows.Items.Count; if (count == 0) return;
            _rows.SelectedIndex = (Math.Max(0, _rows.SelectedIndex) + delta + count) % count;
        }
        private void Confirm()
        {
            var record = _rows.SelectedItem as WindowsChooserRecord;
            if (_selected || record == null) return;
            _selected = true;
            var handler = Selected; if (handler != null) handler(record);
        }
        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground(); if (e.Index < 0) return;
            var record = (WindowsChooserRecord)_rows.Items[e.Index];
            Color color = record.State == AiTaskState.Thinking ? Color.RoyalBlue :
                record.State == AiTaskState.Completed ? Color.ForestGreen :
                record.State == AiTaskState.WaitingForInput ? Color.DarkOrange :
                record.State == AiTaskState.Error ? Color.Firebrick : Color.Gray;
            using (var brush = new SolidBrush(color)) e.Graphics.FillRectangle(brush, e.Bounds.X + 12, e.Bounds.Y + 16, 14, 14);
            string state = record.State == AiTaskState.Thinking ? "运行中" : record.State == AiTaskState.Completed ? "新完成" :
                record.State == AiTaskState.WaitingForInput ? "等待输入" : record.State == AiTaskState.Error ? "出错" : "空闲 / 已查看";
            TextRenderer.DrawText(e.Graphics, record.Title + "  ·  " + state, Font,
                new Rectangle(e.Bounds.X + 38, e.Bounds.Y + 12, e.Bounds.Width - 46, 28), e.ForeColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            e.DrawFocusRectangle();
        }
    }
}
