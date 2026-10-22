using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Cxt1024Bridge
{
    internal sealed partial class BridgeApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _automaticItem;
        private readonly ToolStripMenuItem[] _layerItems = new ToolStripMenuItem[8];
        private readonly Control _invoker;
        private readonly System.Windows.Forms.Timer _foregroundTimer;
        private readonly LayerManager _layers;
        private readonly LightingController _lighting;
        private readonly ActionDispatcher _dispatcher;
        private readonly ChatGptAutomation _chatGpt;
        private readonly HybridChatGptThreadCatalog _threadCatalog;
        private readonly ChatGptTaskSlotRegistry _taskSlots;
        private readonly ActionEngine _actions;
        private readonly TriggerProcessor _triggers;
        private readonly KeyboardHook _hook;
        private readonly HookInputPump _inputPump;
        private readonly ChatGptSixSlotStatusMonitor _statusMonitor;
        private readonly TerminalSixSessionStatusMonitor _terminalStatusMonitor;
        private readonly NativeMethods.WinEventProc _foregroundChangedCallback;
        private IntPtr _foregroundEventHook;
        private Icon _dynamicIcon;
        private volatile bool _exiting;

        internal BridgeApplicationContext()
        {
            _invoker = new Control();
            _invoker.CreateControl();
            _layers = new LayerManager();
            _lighting = new LightingController();
            _dispatcher = new ActionDispatcher();
            _threadCatalog = new HybridChatGptThreadCatalog();
            _taskSlots = new ChatGptTaskSlotRegistry(_threadCatalog.ReadRecent);
            _chatGpt = new ChatGptAutomation(_taskSlots);
            _actions = new ActionEngine(_dispatcher, _layers, _chatGpt);
            _triggers = new TriggerProcessor(_layers, _actions);
            _inputPump = new HookInputPump(_triggers, NativeMethods.GetForegroundWindow, () => _layers.ContextGeneration);
            _hook = new KeyboardHook(_inputPump.Receive);

            _menu = new ContextMenuStrip();
            _automaticItem = new ToolStripMenuItem("自动切层");
            _automaticItem.Click += delegate { _layers.ReturnToAutomatic(); };
            _menu.Items.Add(_automaticItem);
            _menu.Items.Add(new ToolStripSeparator());
            for (int displayLayer = 1; displayLayer <= 8; displayLayer++)
            {
                int capturedLayer = BridgeDefinitions.ToInternalLayer(displayLayer);
                LayerDefinition definition = BridgeDefinitions.GetLayer(capturedLayer);
                var item = new ToolStripMenuItem("L" + definition.Number + "  " + definition.Name);
                item.Click += delegate { _layers.SetManualLayer(capturedLayer); };
                _layerItems[capturedLayer] = item;
                _menu.Items.Add(item);
            }
            _menu.Items.Add(new ToolStripSeparator());
            var backupItem = new ToolStripMenuItem("只读备份当前键盘");
            backupItem.Click += delegate { CreateBackupAsync(); };
            _menu.Items.Add(backupItem);
            var logItem = new ToolStripMenuItem("打开日志");
            logItem.Click += delegate { OpenLog(); };
            _menu.Items.Add(logItem);
            _menu.Items.Add(new ToolStripSeparator());
            var exitItem = new ToolStripMenuItem("退出 CXT 12E4 Bridge");
            exitItem.Click += delegate { ExitBridge(true); };
            _menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon();
            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += delegate { _layers.ReturnToAutomatic(); };

            _layers.LayerChanged += LayersChanged;
            ForegroundInfo initial = ForegroundResolver.Capture();
            _layers.ObserveForeground(initial);
            UpdateTray(_layers.CurrentLayer, _layers.IsManual, initial);
            _lighting.SetLayer(_layers.CurrentLayer);

            _foregroundChangedCallback = delegate(
                IntPtr eventHook,
                uint eventType,
                IntPtr window,
                int objectId,
                int childId,
                uint eventThread,
                uint eventTime)
            {
                if (!_exiting && window != IntPtr.Zero)
                    ObserveDeviceForeground(window);
            };
            _foregroundEventHook = NativeMethods.SetWinEventHook(
                NativeMethods.EventSystemForeground,
                NativeMethods.EventSystemForeground,
                IntPtr.Zero,
                _foregroundChangedCallback,
                0,
                0,
                NativeMethods.WineventOutofcontext);
            if (_foregroundEventHook == IntPtr.Zero)
                Logger.Warn("Foreground WinEvent hook was unavailable; 220ms polling remains active as fallback.");

            _foregroundTimer = new System.Windows.Forms.Timer();
            _foregroundTimer.Interval = 220;
            _foregroundTimer.Tick += delegate { ObserveDeviceForeground(IntPtr.Zero); UpdateControllerStatus(); };
            _foregroundTimer.Start();

            _statusMonitor = new ChatGptSixSlotStatusMonitor(_taskSlots, CacheTaskStates);
            _terminalStatusMonitor = new TerminalSixSessionStatusMonitor(CacheTerminalStates);
            _actions.TaskSlotOpened += _statusMonitor.AcknowledgeSlot;
            _actions.TerminalSessionOpened += _terminalStatusMonitor.AcknowledgeSession;
            InitializeDeviceSettings();
            _hook.Install();
            _controller.Start();
            Logger.Info("Bridge started. " + initial);
        }

        private void LayersChanged(object sender, LayerChangedEventArgs args)
        {
            _lighting.SetLayer(args.Layer);
            Logger.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Layer changed to L{0} ({1}); manual={2}; foreground={3}",
                BridgeDefinitions.GetLayer(args.Layer).Number,
                BridgeDefinitions.GetLayer(args.Layer).Name,
                args.Manual,
                args.Foreground));
            if (_invoker.IsDisposed)
                return;
            _invoker.BeginInvoke((Action)delegate { UpdateTray(args.Layer, args.Manual, args.Foreground); RefreshControllerContext(); });
        }

        private void UpdateTray(int layer, bool manual, ForegroundInfo foreground)
        {
            LayerDefinition definition = BridgeDefinitions.GetLayer(layer);
            _automaticItem.Checked = !manual;
            for (int i = 0; i < _layerItems.Length; i++)
                _layerItems[i].Checked = i == layer;
            string process = foreground == null || string.IsNullOrEmpty(foreground.ProcessName)
                ? "未知程序"
                : foreground.ProcessName;
            string text = string.Format(
                CultureInfo.InvariantCulture,
                "CXT 12E4 L{0} {1} {2} · {3}",
                definition.Number,
                definition.Name,
                manual ? "[手动]" : "[自动]",
                process);
            _notifyIcon.Text = text.Length <= 63 ? text : text.Substring(0, 63);

            Icon next = CreateLayerIcon(definition, definition.Number);
            Icon previous = _dynamicIcon;
            _dynamicIcon = next;
            _notifyIcon.Icon = next;
            if (previous != null)
                previous.Dispose();
        }

        internal static Icon CreateLayerIcon(LayerDefinition layer, int number)
        {
            using (Bitmap bitmap = CreateLayerIconBitmap(layer, number))
            {
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                        return (Icon)temporary.Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }
        }

        internal static Bitmap CreateLayerIconBitmap(LayerDefinition layer, int number)
        {
            if (layer == null)
                throw new ArgumentNullException("layer");
            if (number < 1 || number > 8)
                throw new ArgumentOutOfRangeException("number");

            Color background = Color.FromArgb(layer.Red, layer.Green, layer.Blue);
            Color foreground = GetLayerIconTextColor(background);
            var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var text = new SolidBrush(foreground))
            using (var font = new Font("Arial", 25, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                graphics.Clear(background);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.FormatFlags = StringFormatFlags.NoClip;
                graphics.DrawString(
                    number.ToString(CultureInfo.InvariantCulture),
                    font,
                    text,
                    new RectangleF(-1, -2, 34, 35),
                    format);
            }
            return bitmap;
        }

        internal static Color GetLayerIconTextColor(Color background)
        {
            double red = RelativeLuminanceComponent(background.R / 255.0);
            double green = RelativeLuminanceComponent(background.G / 255.0);
            double blue = RelativeLuminanceComponent(background.B / 255.0);
            double luminance = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
            return luminance > 0.179 ? Color.Black : Color.White;
        }

        private static double RelativeLuminanceComponent(double component)
        {
            return component <= 0.03928
                ? component / 12.92
                : Math.Pow((component + 0.055) / 1.055, 2.4);
        }

        private void CreateBackupAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    HidDeviceInfo device = HidEnumerator.FindCxt1024Raw();
                    if (device == null)
                        throw new InvalidOperationException("没有找到 CXT 12E4 原始 HID 接口。");
                    string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups");
                    string path = Path.Combine(directory, "cxt1024-tray-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                    BackupService.WriteJson(path, BackupService.Probe(device, true));
                    ShowBalloon("备份完成", path, ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    Logger.Error("Tray backup failed", ex);
                    ShowBalloon("备份失败", ex.Message, ToolTipIcon.Error);
                }
            });
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            if (_invoker.IsDisposed)
                return;
            _invoker.BeginInvoke((Action)delegate
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = text;
                _notifyIcon.BalloonTipIcon = icon;
                _notifyIcon.ShowBalloonTip(3500);
            });
        }

        private static void OpenLog()
        {
            string directory = Path.GetDirectoryName(Logger.LogPath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            if (!File.Exists(Logger.LogPath))
                File.WriteAllText(Logger.LogPath, string.Empty);
            Process.Start(new ProcessStartInfo(Logger.LogPath) { UseShellExecute = true });
        }

        internal static bool PrepareResidentExit(bool explicitUserExit, Func<bool> stopWatchdog)
        {
            // --exit is used by deployment/restart tools and intentionally
            // leaves the watchdog running. Only the tray's explicit user quit
            // stops the supervisor as well.
            return !explicitUserExit || stopWatchdog();
        }

        private void ExitBridge(bool explicitUserExit)
        {
            if (_exiting)
                return;
            try
            {
                if (!PrepareResidentExit(explicitUserExit, BridgeWatchdog.StopForUserExit))
                {
                    ShowBalloon("暂未退出 Bridge", "守护进程尚未停止，Bridge 保持运行。请稍后重试退出。", ToolTipIcon.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to stop the watchdog for explicit Bridge exit", ex);
                ShowBalloon("暂未退出 Bridge", "无法停止守护进程，Bridge 保持运行。请查看日志后重试。", ToolTipIcon.Error);
                return;
            }
            _exiting = true;
            ExitThread();
        }

        internal void RequestExit()
        {
            if (_invoker.IsDisposed)
                return;
            _invoker.BeginInvoke((Action)delegate { ExitBridge(false); });
        }

        protected override void ExitThreadCore()
        {
            if (!_exiting)
                _exiting = true;
            if (_foregroundEventHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundEventHook);
                _foregroundEventHook = IntPtr.Zero;
            }
            _foregroundTimer.Stop();
            if (_controller != null) _controller.Dispose();
            if (_profileSettings != null && !_profileSettings.IsDisposed) _profileSettings.Close();
            if (_chooser != null && !_chooser.IsDisposed) _chooser.Close();
            _hook.Dispose();
            _inputPump.Dispose();
            _triggers.Dispose();
            // Stop and drain already-linearized actions while their status
            // monitors and event subscriptions are still alive. No queued tmux
            // switch may run after the monitors or lighting controller are gone.
            _dispatcher.Dispose();
            _actions.TaskSlotOpened -= _statusMonitor.AcknowledgeSlot;
            _actions.TerminalSessionOpened -= _terminalStatusMonitor.AcknowledgeSession;
            _statusMonitor.Dispose();
            _terminalStatusMonitor.Dispose();
            _threadCatalog.Dispose();
            _lighting.SetLayer(0);
            Thread.Sleep(100);
            _lighting.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            if (_dynamicIcon != null)
                _dynamicIcon.Dispose();
            _menu.Dispose();
            _invoker.Dispose();
            Logger.Info("Bridge stopped.");
            base.ExitThreadCore();
        }
    }
}
