using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Cxt1024Bridge
{
    // Only reads rollout files and writes Bridge's own ignore store. No external UI automation.
    internal sealed class WindowsQuestionManager : Form
    {
        private readonly CodexQuestionService _service;
        private readonly ListView _list = new ListView();
        private readonly TextBox _detail = new TextBox();
        private readonly Label _status = new Label();
        private readonly Button _action = new Button(), _refresh = new Button();
        private bool _busy;
        internal WindowsQuestionManager(CodexQuestionService service)
        {
            _service = service; Text = "Bridge · 忽略此提问"; Size = new Size(850, 600); MinimumSize = new Size(740, 500);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(new Label { Text = "在 ChatGPT 点过“跳过”后，可在这里忽略对应提问。仅调整本机 Bridge 灯光，不会回答问题或继续任务；可随时恢复。", Dock = DockStyle.Fill }, 0, 0);
            _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.HideSelection = false; _list.Dock = DockStyle.Fill;
            _list.Columns.Add("任务", 245); _list.Columns.Add("具体提问", 400); _list.Columns.Add("提示状态", 100);
            _list.SelectedIndexChanged += delegate { UpdateSelection(); }; layout.Controls.Add(_list, 0, 1);
            _detail.Multiline = true; _detail.ReadOnly = true; _detail.ScrollBars = ScrollBars.Vertical; _detail.Dock = DockStyle.Fill;
            layout.Controls.Add(_detail, 0, 2); _status.Dock = DockStyle.Fill; _status.ForeColor = SystemColors.GrayText; layout.Controls.Add(_status, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            _action.Text = "忽略此提问"; _action.AutoSize = true; _action.Enabled = false; _action.Click += delegate { Toggle(); };
            _refresh.Text = "刷新"; _refresh.AutoSize = true; _refresh.Click += delegate { Reload(""); };
            var close = new Button { Text = "关闭", AutoSize = true }; close.Click += delegate { Close(); }; CancelButton = close;
            buttons.Controls.Add(_action); buttons.Controls.Add(_refresh); buttons.Controls.Add(close); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
            Shown += delegate { Reload(""); };
        }
        private CodexQuestionRow Selected { get { return _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as CodexQuestionRow : null; } }
        private void UpdateSelection()
        {
            var row = Selected; _action.Enabled = !_busy && row != null;
            _action.Text = row != null && row.Question.Ignored ? "恢复等待提示" : "忽略此提问";
            _detail.Text = row == null ? "选择一条提问查看完整内容。" : "键 " + row.Key + " · " + row.TaskTitle + "\r\n\r\n" + row.Question.Title;
        }
        private void SetBusy(bool value) { _busy = value; _refresh.Enabled = !value; UpdateSelection(); }
        private async void Reload(string message)
        {
            if (_busy || IsDisposed) return;
            string selected = Selected == null ? null : Selected.Identity;
            SetBusy(true); _status.Text = "正在读取提问…";
            try
            {
                List<CodexQuestionRow> rows = await Task.Run(() => _service.Read());
                if (IsDisposed) return;
                _list.BeginUpdate(); _list.Items.Clear();
                foreach (var row in rows)
                {
                    var item = new ListViewItem("键 " + row.Key + " · " + row.TaskTitle.Replace('\n', ' ').Replace('\r', ' ')) { Tag = row };
                    item.SubItems.Add(row.Question.Title.Replace('\n', ' ').Replace('\r', ' ')); item.SubItems.Add(row.Question.Ignored ? "已忽略" : "等待输入");
                    _list.Items.Add(item); if (row.Identity == selected) item.Selected = true;
                }
                _list.EndUpdate();
                _status.Text = message.Length != 0 ? message : rows.Count == 0 ? "当前六个任务没有可管理的异步提问。权限请求和同步等待不能在这里忽略。" : "选择具体问题后操作；已回答或已进入新一轮的问题会自动退出列表。";
            }
            catch (Exception ex) { if (!IsDisposed) { _list.Items.Clear(); _status.Text = ex.Message; } }
            finally { if (!IsDisposed) SetBusy(false); }
        }
        private async void Toggle()
        {
            var selected = Selected;
            if (_busy || selected == null) return;
            bool ignore = !selected.Question.Ignored; SetBusy(true); string message;
            try
            {
                await Task.Run(() => _service.Set(selected, ignore));
                message = ignore ? "已忽略这道题。若还有其他问题，黄灯会继续保留。" : "已恢复这道题的等待提示。";
            }
            catch (Exception ex) { message = ex.Message; }
            if (!IsDisposed) { SetBusy(false); Reload(message); }
        }
    }
}
