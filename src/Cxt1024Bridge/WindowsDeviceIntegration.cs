using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace Cxt1024Bridge
{
    internal sealed partial class BridgeApplicationContext
    {
        private WindowsProfileStore _profiles;
        private WindowsControllerRuntime _controller;
        private WindowsProfileSettings _profileSettings;
        private IntPtr _settingsReturnWindow;
        private WindowsControllerChooser _chooser;
        private ToolStripMenuItem _controllerStatus;
        private long _controllerGeneration = -1;
        private int _controllerLayer = -1;
        private readonly object _chooserCacheGate = new object();
        private List<WindowsChooserRecord> _chooserTasks = new List<WindowsChooserRecord>();
        private List<WindowsChooserRecord> _chooserSessions = new List<WindowsChooserRecord>();
        private readonly int _ownProcessId = Process.GetCurrentProcess().Id;

        private void InitializeDeviceSettings()
        {
            _profiles = new WindowsProfileStore(Path.Combine(Path.GetDirectoryName(Logger.LogPath), "profiles.windows.json"));
            _actions.Profiles = _profiles;
            _controller = new WindowsControllerRuntime(_profiles);
            _controller.Context = CaptureControllerContext;
            _controller.BeforeInput = delegate { ObserveDeviceForeground(IntPtr.Zero); };
            _controller.OnAction = delegate(string id, WindowsControllerContext c, double received)
            { _actions.NamedAction(id, c.Layer, c.Window, c.Generation, received, "xbox"); };
            _controller.OnHelp = delegate { ShowProfileSettings("xbox"); };
            _controller.OnChooser = ShowControllerChooser;
            _controller.OnNotice = delegate(string message) { ShowBalloon("Xbox Bridge", message, ToolTipIcon.Info); };
            _controller.OnPanelInput = delegate(string button, bool down)
            {
                if (_profileSettings != null && !_profileSettings.IsDisposed && _profileSettings.ContainsFocus && button == "menu") return true;
                return _chooser != null && !_chooser.IsDisposed && _chooser.ContainsFocus && _chooser.HandleButton(button, down);
            };
            _profiles.Changed += ProfilesChanged;
            _actions.ProfileHelpRequested += delegate(string device, IntPtr window, long generation, double deadline)
            { PostContextUi(window, generation, deadline, delegate { ShowProfileSettings(device); }); };
            _actions.ProfileChooserRequested += delegate(IntPtr window, long generation, double deadline)
            { PostContextUi(window, generation, deadline, ShowControllerChooser); };

            _controllerStatus = new ToolStripMenuItem("Xbox：未连接") { Enabled = false };
            _menu.Items.Insert(0, _controllerStatus);
            _menu.Items.Insert(1, new ToolStripSeparator());
            var settings = new ToolStripMenuItem("分层按键设置…");
            settings.Click += delegate { ShowProfileSettings("xbox"); };
            var chooser = new ToolStripMenuItem("任务 / 会话选择…");
            chooser.Click += delegate { ShowControllerChooser(); };
            var pause = new ToolStripMenuItem("暂停 / 恢复 Xbox"); pause.Click += delegate { _controller.Toggle(); };
            var appPause = new ToolStripMenuItem("在当前应用暂停 / 恢复 Xbox"); appPause.Click += delegate { ToggleProcessPause(); };
            var guide = new ToolStripMenuItem("使用说明（Windows / Mac）");
            guide.Click += delegate { Process.Start(new ProcessStartInfo("https://cxt12e4.11166666.xyz/index.html?device=xbox") { UseShellExecute = true }); };
            var shortcuts = new ToolStripMenuItem("快捷键总表");
            shortcuts.Click += delegate { Process.Start(new ProcessStartInfo("https://cxt12e4.11166666.xyz/shortcuts.html") { UseShellExecute = true }); };
            int position = _menu.Items.Count - 2;
            foreach (ToolStripItem item in new ToolStripItem[] { settings, chooser, pause, appPause, guide, shortcuts }) _menu.Items.Insert(position++, item);
            if (!string.IsNullOrEmpty(_profiles.LoadError)) ShowBalloon("配置未加载", _profiles.LoadError, ToolTipIcon.Warning);
            RefreshControllerContext();
        }

        private WindowsControllerContext CaptureControllerContext()
        {
            ForegroundInfo f = _layers.CurrentForeground;
            return new WindowsControllerContext { Layer = _layers.CurrentLayer, Generation = _layers.ContextGeneration,
                Window = f == null ? IntPtr.Zero : f.Window, ProcessId = f == null ? 0 : f.ProcessId,
                ProcessName = f == null ? string.Empty : f.ProcessName };
        }
        private void ObserveDeviceForeground(IntPtr window)
        {
            if (_exiting) return;
            ForegroundInfo f = window == IntPtr.Zero ? ForegroundResolver.Capture() : ForegroundResolver.Capture(window);
            // Editing settings/using the chooser must retain the originating
            // app's layer. Actual shortcut delivery still verifies native HWND.
            if (f.ProcessId == _ownProcessId) return;
            _layers.ObserveForeground(f);
            if (_chooser != null && !_chooser.IsDisposed && _chooser.Visible && !_chooser.ContainsFocus) _chooser.Close();
            RefreshControllerContext();
        }
        private void RefreshControllerContext()
        {
            if (_controller == null || _exiting) return;
            if (_invoker.InvokeRequired) { _invoker.BeginInvoke((Action)RefreshControllerContext); return; }
            long generation = _layers.ContextGeneration; int layer = _layers.CurrentLayer;
            if (_controllerGeneration == generation && _controllerLayer == layer) return;
            _controllerGeneration = generation; _controllerLayer = layer; _controller.ContextChanged();
        }
        private void ProfilesChanged()
        {
            if (_exiting) return;
            if (_invoker.InvokeRequired) { _invoker.BeginInvoke((Action)ProfilesChanged); return; }
            _triggers.CancelBusinessInputs(); _layers.InvalidateInputContext();
            _controller.Configure(); RefreshControllerContext(); UpdateControllerStatus();
        }
        private void UpdateControllerStatus()
        {
            if (_controllerStatus != null && _controller != null) _controllerStatus.Text = _controller.Status;
        }
        private void PostContextUi(IntPtr window, long generation, double deadline, Action action)
        {
            if (_exiting || _invoker.IsDisposed) return;
            _invoker.BeginInvoke((Action)delegate
            {
                if (!_exiting && WindowsControllerClock.Now <= deadline && NativeMethods.GetForegroundWindow() == window && _layers.ContextGeneration == generation) action();
            });
        }
        private void ToggleProcessPause()
        {
            string process = CaptureControllerContext().ProcessName;
            if (string.IsNullOrEmpty(process)) return;
            WindowsProfileDocument next = _profiles.Document;
            int at = next.PausedProcesses.FindIndex(p => string.Equals(p, process, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p, process + ".exe", StringComparison.OrdinalIgnoreCase));
            if (at >= 0) next.PausedProcesses.RemoveAt(at); else next.PausedProcesses.Add(process);
            try { _profiles.Replace(next); }
            catch (Exception ex) { ShowBalloon("未保存暂停设置", ex.Message, ToolTipIcon.Error); }
        }
        private void ShowProfileSettings(string device)
        {
            if (_exiting) return;
            if (_chooser != null && !_chooser.IsDisposed) _chooser.Close();
            if (_profileSettings != null && !_profileSettings.IsDisposed) { _settingsReturnWindow = CaptureControllerContext().Window; _profileSettings.Activate(); return; }
            IntPtr returnWindow = CaptureControllerContext().Window;
            _triggers.CancelBusinessInputs(); _layers.InvalidateInputContext(); _controller.CancelAll();
            _settingsReturnWindow = returnWindow;
            _profileSettings = new WindowsProfileSettings(_profiles, device, _layers.CurrentLayer);
            _profileSettings.FormClosed += delegate { _profileSettings = null; if (!_exiting) { _layers.InvalidateInputContext(); RestoreDeviceWindow(_settingsReturnWindow); } };
            _profileSettings.Show();
        }
        private bool RestoreDeviceWindow(IntPtr window)
        {
            if (_exiting || window == IntPtr.Zero || !NativeMethods.IsWindow(window)) return false;
            ForegroundInfo current = ForegroundResolver.Capture();
            // A deliberate switch to another application wins over closing a
            // helper panel; do not steal that application's focus back.
            if (current.Window != IntPtr.Zero && current.Window != window && current.ProcessId != _ownProcessId) return false;
            if (current.Window != window && !ForegroundWindowActivator.Activate(window)) return false;
            ObserveDeviceForeground(window);
            return NativeMethods.GetForegroundWindow() == window;
        }
        private void ShowControllerChooser()
        {
            if (_exiting) return;
            if (_chooser != null && !_chooser.IsDisposed) { _chooser.Close(); return; }
            if (_profileSettings != null && !_profileSettings.IsDisposed) { _settingsReturnWindow = CaptureControllerContext().Window; _profileSettings.Activate(); return; }
            IntPtr returnWindow = CaptureControllerContext().Window;
            List<WindowsChooserRecord> tasks, sessions;
            lock (_chooserCacheGate) { tasks = new List<WindowsChooserRecord>(_chooserTasks); sessions = new List<WindowsChooserRecord>(_chooserSessions); }
            _triggers.CancelBusinessInputs(); _layers.InvalidateInputContext(); _controller.SetPanelVisible(true);
            var panel = new WindowsControllerChooser(tasks, sessions, _layers.CurrentLayer); _chooser = panel;
            panel.FormClosed += delegate
            {
                _chooser = null;
                if (_exiting) return;
                _layers.InvalidateInputContext(); _controller.SetPanelVisible(false); RestoreDeviceWindow(returnWindow);
            };
            panel.Selected += delegate(WindowsChooserRecord record)
            {
                panel.Close();
                if (!RestoreDeviceWindow(returnWindow)) { ShowBalloon("选择已取消", "原窗口焦点已改变，请重新选择。", ToolTipIcon.Info); return; }
                long generation = _layers.ContextGeneration;
                double received = WindowsControllerClock.Now;
                _dispatcher.Enqueue(delegate
                {
                    if (WindowsControllerClock.Now - received > 0.5 || NativeMethods.GetForegroundWindow() != returnWindow ||
                        _layers.ContextGeneration != generation) return;
                    try
                    {
                        if (record.Kind == "task")
                        {
                            if (_chatGpt.OpenThreadDeepLink(new ChatGptThreadSlot { Id = record.Id, Title = record.Title }, true))
                                _statusMonitor.AcknowledgeThread(record.Id);
                        }
                        else
                        {
                            TerminalSessionTarget target;
                            if (TmuxSessionClient.SwitchToIdentity(record.Id, out target))
                            {
                                _terminalStatusMonitor.AcknowledgeSession(target);
                                TerminalWindowActivator.FocusWindow(_layers.LastTerminalWindow);
                            }
                            else ShowBalloon("会话不可用", "该 tmux 会话已退出，或没有已连接的客户端。", ToolTipIcon.Info);
                        }
                    }
                    catch (Exception ex) { Logger.Error("Controller selection failed", ex); ShowBalloon("选择失败", ex.Message, ToolTipIcon.Warning); }
                });
            };
            panel.Show();
        }
        private void CacheTaskStates(IList<ChatGptTaskStatusSlot> states)
        {
            var records = new List<WindowsChooserRecord>();
            foreach (ChatGptTaskStatusSlot state in states)
                if (state != null && !string.IsNullOrEmpty(state.ThreadId)) records.Add(new WindowsChooserRecord
                    { Kind = "task", Id = state.ThreadId, Title = state.Title ?? "未命名任务", State = state.State });
            lock (_chooserCacheGate) _chooserTasks = records;
            _lighting.SetTaskStates(states);
        }
        private void CacheTerminalStates(IList<TerminalSessionStatusSlot> states)
        {
            var records = new List<WindowsChooserRecord>();
            foreach (TerminalSessionStatusSlot state in states)
                if (state != null && state.Present && !string.IsNullOrEmpty(state.SessionIdentity)) records.Add(new WindowsChooserRecord
                    { Kind = "terminal", Id = state.SessionIdentity, Title = state.SessionName, State = state.State });
            lock (_chooserCacheGate) _chooserSessions = records;
            _lighting.SetTerminalStates(states);
        }
    }
}
