using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Cxt1024Bridge
{
    // Edits a private draft. Only Save/Import through the store can change the running bridge.
    // Focus restoration belongs to the bridge's owner via FormClosed, never to this form.
    internal sealed class WindowsProfileSettings : Form
    {
        private readonly WindowsProfileStore _store;
        private WindowsProfileDocument _draft;
        private string _device;
        private int _layer;
        private bool _loading;
        private readonly ComboBox _devicePicker = Picker();
        private readonly ComboBox _layerPicker = Picker();
        private readonly TableLayoutPanel _mapping = new TableLayoutPanel();
        private readonly Dictionary<string, ComboBox> _bindings = new Dictionary<string, ComboBox>();
        private readonly CheckBox _enabled = new CheckBox { Text = "启用 Xbox 桌面控制", AutoSize = true };
        private readonly CheckBox _invert = new CheckBox { Text = "反转摇杆滚动方向", AutoSize = true };
        private readonly NumericUpDown _deadZone = Number(.05m, .4m, .01m, 2);
        private readonly NumericUpDown _speed = Number(200, 4000, 100, 0);
        private readonly NumericUpDown _scroll = Number(50, 2000, 50, 0);
        private readonly NumericUpDown _precision = Number(.1m, .7m, .05m, 2);
        private readonly TextBox _paused = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true };
        private readonly Label _status = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Padding = new Padding(5) };
        private readonly Label _notice = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4) };
        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TabPage _parameters = new TabPage("Xbox 参数与暂停应用");

        internal WindowsProfileSettings(WindowsProfileStore store, string initialDevice, int initialLayer)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store; _draft = store.Document;
            _device = initialDevice == "xbox" ? "xbox" : "keyboard";
            _layer = initialLayer >= 0 && initialLayer <= 7 ? initialLayer : 0;
            Text = "CXT12E4 Bridge · 分层按键设置";
            Font = new Font("Microsoft YaHei UI", 9F); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1080, 780); MinimumSize = new Size(930, 680);
            StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = true;
            BuildLayout(); PopulateParameters(); RenderMappings();
            _status.Text = store.LoadError ?? "选择设备与应用层修改映射，保存后生效。配置保存在：" + store.Path;
        }
        private void BuildLayout()
        {
            var shell = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            Controls.Add(shell);
            var header = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            header.Controls.Add(new Label { Text = "设备", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
            _devicePicker.Width = 170; _devicePicker.Items.AddRange(new object[] { "12E4 键盘", "Xbox 手柄" });
            _devicePicker.SelectedIndex = _device == "keyboard" ? 0 : 1; header.Controls.Add(_devicePicker);
            header.Controls.Add(new Label { Text = "  应用层", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
            _layerPicker.Width = 285;
            for (int i = 1; i <= 8; i++) _layerPicker.Items.Add(WindowsLayerProfiles.LayerName(i == 8 ? 0 : i));
            _layerPicker.SelectedIndex = (_layer == 0 ? 8 : _layer) - 1; header.Controls.Add(_layerPicker);
            shell.Controls.Add(header, 0, 0);
            var bindingsTab = new TabPage("本层按键");
            _mapping.Dock = DockStyle.Top; _mapping.AutoSize = true; _mapping.ColumnCount = 4; _mapping.Padding = new Padding(6);
            _mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138)); _mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138)); _mapping.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var scrollArea = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scrollArea.Controls.Add(_mapping); bindingsTab.Controls.Add(scrollArea);
            _tabs.TabPages.Add(bindingsTab); _tabs.TabPages.Add(_parameters); BuildParameters(); shell.Controls.Add(_tabs, 0, 1);
            shell.Controls.Add(_notice, 0, 2); shell.Controls.Add(_status, 0, 3);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
            footer.Controls.Add(Button("本层恢复默认", ResetLayer)); footer.Controls.Add(Button("导入配置…", Import)); footer.Controls.Add(Button("导出配置…", Export));
            footer.Controls.Add(Button("保存并关闭", Save));
            var close = Button("取消", delegate { Close(); }); close.DialogResult = DialogResult.Cancel; CancelButton = close; footer.Controls.Add(close);
            shell.Controls.Add(footer, 0, 4);
            _devicePicker.SelectedIndexChanged += delegate { ChangeSelection(); };
            _layerPicker.SelectedIndexChanged += delegate { ChangeSelection(); };
        }
        private void BuildParameters()
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 9 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 225)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(_enabled, 0, 0); table.SetColumnSpan(_enabled, 2);
            AddParameter(table, 1, "摇杆死区（0.05–0.40）", _deadZone);
            AddParameter(table, 2, "鼠标速度（像素 / 秒）", _speed);
            AddParameter(table, 3, "滚轮单位 / 秒（120 = 1 格）", _scroll);
            AddParameter(table, 4, "LT 精细移动倍率", _precision);
            table.Controls.Add(_invert, 0, 5); table.SetColumnSpan(_invert, 2);
            table.Controls.Add(new Label { Text = "前台处于以下进程时暂停手柄", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 6); table.SetColumnSpan(table.GetControlFromPosition(0, 6), 2);
            table.Controls.Add(_paused, 0, 7); table.SetColumnSpan(_paused, 2);
            var help = new Label { Text = "每行一个进程名，例如 steam.exe 或 MyGame。大小写不敏感，.exe 可省略；不是游戏窗口自动识别。\r\n暂停名单中的应用会忽略全部手柄操作。请离开该应用或从托盘菜单恢复；Xbox / View / Share 保留给系统。", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
            table.Controls.Add(help, 0, 8); table.SetColumnSpan(help, 2); _parameters.Controls.Add(table);
        }
        private static void AddParameter(TableLayoutPanel table, int row, string title, Control control)
        {
            table.Controls.Add(new Label { Text = title, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
            table.Controls.Add(control, 1, row);
        }
        private void PopulateParameters()
        {
            _enabled.Checked = _draft.XboxEnabled; _deadZone.Value = (decimal)_draft.DeadZone;
            _speed.Value = (decimal)_draft.CursorSpeed; _scroll.Value = (decimal)_draft.ScrollSpeed; _precision.Value = (decimal)_draft.PrecisionScale;
            _invert.Checked = _draft.InvertScroll; _paused.Text = string.Join(Environment.NewLine, _draft.PausedProcesses.ToArray());
        }
        private void CaptureParameters()
        {
            _draft.XboxEnabled = _enabled.Checked; _draft.DeadZone = (double)_deadZone.Value; _draft.CursorSpeed = (double)_speed.Value;
            _draft.ScrollSpeed = (double)_scroll.Value; _draft.PrecisionScale = (double)_precision.Value; _draft.InvertScroll = _invert.Checked;
            var entries = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in _paused.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            { string entry = line.Trim(); if (entry.Length > 0 && seen.Add(entry)) entries.Add(entry); }
            _draft.PausedProcesses = entries;
        }
        private void CaptureMappings()
        {
            if (_loading) return;
            foreach (var item in _bindings)
            {
                var choice = item.Value.SelectedItem as ProfileChoice;
                if (choice != null) WindowsProfileStore.Put(_draft, _device, item.Key, _layer, choice.Id);
            }
        }
        private void ChangeSelection()
        {
            if (_loading) return;
            CaptureMappings(); _device = _devicePicker.SelectedIndex == 0 ? "keyboard" : "xbox";
            _layer = _layerPicker.SelectedIndex == 7 ? 0 : _layerPicker.SelectedIndex + 1;
            RenderMappings();
        }
        private void RenderMappings()
        {
            _loading = true; _mapping.SuspendLayout();
            try
            {
                _bindings.Clear();
                while (_mapping.Controls.Count > 0) { Control old = _mapping.Controls[0]; _mapping.Controls.Remove(old); old.Dispose(); }
                _mapping.RowStyles.Clear();
                List<ProfileControl> controls = WindowsLayerProfiles.Controls(_device); int half = (controls.Count + 1) / 2;
                _mapping.RowCount = half;
                for (int row = 0; row < half; row++) _mapping.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                for (int i = 0; i < controls.Count; i++)
                {
                    ProfileControl item = controls[i]; int col = (i / half) * 2, row = i % half;
                    if (_device == "xbox")
                    {
                        if (i < 2) { col = i * 2; row = 0; }
                        else if (i < 14) { col = 0; row = i - 1; }
                        else { col = 2; row = i - 13; }
                    }
                    _mapping.Controls.Add(new Label { Text = item.Title, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, col, row);
                    var picker = Picker(); picker.Dock = DockStyle.Fill; picker.DropDownWidth = 455; picker.IntegralHeight = false; picker.MaxDropDownItems = 18;
                    string current = WindowsProfileStore.Resolve(_draft, _device, item.Id, _layer);
                    foreach (ProfileChoice choice in WindowsLayerProfiles.Choices(_device, item.Id, _layer)) { picker.Items.Add(choice); if (choice.Id == current) picker.SelectedItem = choice; }
                    _bindings[item.Id] = picker; _mapping.Controls.Add(picker, col + 1, row);
                }
                _notice.Text = _device == "keyboard"
                    ? "12E4：这里只改变短按和旋钮动作。全局任务 / tmux 长按、K12 应用组合保持固定。修改的是 Bridge 映射，不会写入键盘固件。"
                    : "Xbox 默认：X 左键（按住拖拽），Y 右键，A 回车确认，B Esc；RT+L3 执行本层快捷，L3 打开设置。LT 精细移动；Menu 短按选任务 / 已有会话，长按暂停 / 恢复。\r\n首行为两个摇杆，其余左右两列分别为基础动作、RT 组合。终端层 RT+X 进入 copy-mode，RT+Y 暂存 / 恢复输入。";
            }
            finally { _mapping.ResumeLayout(); _loading = false; }
        }
        private void ResetLayer()
        {
            Dictionary<string, Dictionary<string, string>> layers;
            if (_draft.Bindings.TryGetValue(_device, out layers)) layers.Remove(_layer.ToString(CultureInfo.InvariantCulture));
            RenderMappings(); _status.Text = "已恢复当前设备、当前层的默认映射；点击“保存并关闭”生效。";
        }
        private void Save()
        {
            Try(delegate { CaptureMappings(); CaptureParameters(); _store.Replace(_draft); DialogResult = DialogResult.OK; Close(); });
        }
        private void Import()
        {
            using (var dialog = new OpenFileDialog { Title = "导入 Windows Bridge 配置", Filter = "Windows Bridge 配置 (*.json)|*.json", CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) Try(delegate { WindowsProfileDocument next = WindowsProfileStore.Read(dialog.FileName); _draft = next; PopulateParameters(); RenderMappings(); _status.Text = "已读取配置草稿；点击“保存并关闭”后生效。"; });
        }
        private void Export()
        {
            using (var dialog = new SaveFileDialog { Title = "导出 Windows Bridge 配置", Filter = "Windows Bridge 配置 (*.json)|*.json", FileName = "cxt12e4-windows-profiles.json", DefaultExt = "json", AddExtension = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) Try(delegate {
                    if (string.Equals(Path.GetFullPath(dialog.FileName), _store.Path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请导出到其他位置；应用当前配置请使用保存按钮。");
                    CaptureMappings(); CaptureParameters(); WindowsProfileStore.WriteExport(dialog.FileName, _draft); _status.Text = "已导出配置草稿：" + dialog.FileName;
                });
        }
        private void Try(Action action)
        {
            try { action(); }
            catch (Exception e) { _status.Text = "未完成：" + e.Message; MessageBox.Show(this, e.Message, "配置未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
        private static ComboBox Picker() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; }
        private static NumericUpDown Number(decimal min, decimal max, decimal step, int decimals) { return new NumericUpDown { Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals, Width = 180, ThousandsSeparator = true }; }
        private static Button Button(string title, Action click)
        {
            var button = new Button { Text = title, AutoSize = true, Height = 31, Padding = new Padding(6, 0, 6, 0) };
            button.Click += delegate { click(); }; return button;
        }
    }
}
