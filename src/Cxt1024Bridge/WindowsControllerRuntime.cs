using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Cxt1024Bridge
{
    internal sealed class WindowsControllerRuntime : WindowsControllerState, IDisposable
    {
        private readonly WindowsControllerXInput input = new WindowsControllerXInput();
        private readonly HashSet<string> sleepReasons = new HashSet<string>();
        private Timer timer;
        private Control marshal;
        private int slot = -1;
        private double previousTick, nextScan, lastError;
        private long lastOutputFailures;
        private bool disposed;

        internal WindowsControllerRuntime(WindowsProfileStore profiles)
            : base(profiles, new WindowsControllerEngine(WindowsControllerNative.Post,
                  WindowsControllerNative.Position, WindowsControllerNative.Displays,
                  delegate { return WindowsControllerClock.Now; }, WindowsControllerNative.DoubleClickInterval),
                  delegate { return WindowsControllerClock.Now; }) { }

        internal void Start()
        {
            if (timer != null || disposed) return;
            marshal = new Control(); IntPtr unused = marshal.Handle;
            SystemEvents.PowerModeChanged += PowerChanged;
            SystemEvents.SessionSwitch += SessionChanged;
            Configure(); previousTick = Clock();
            timer = new Timer { Interval = 16 };
            timer.Tick += Poll;
            timer.Start(); Poll(this, EventArgs.Empty);
        }

        private void Poll(object sender, EventArgs args)
        {
            double now = Clock(), delta = now - previousTick; previousTick = now;
            try
            {
                if (sleepReasons.Count != 0) { Tick(delta); return; }
                WindowsControllerReport report;
                if (slot >= 0)
                {
                    if (input.TryRead(slot, out report)) ApplyReport(report);
                    else { slot = -1; ConnectionChanged(false, "Xbox Controller"); nextScan = now + 0.5; }
                }
                else if (now >= nextScan)
                {
                    nextScan = now + 0.5;
                    // A stable first-connected slot is the only owner. Another
                    // controller cannot simultaneously drive the same cursor.
                    for (int candidate = 0; candidate < 4; candidate++)
                    {
                        if (!input.TryRead(candidate, out report)) continue;
                        slot = candidate;
                        ConnectionChanged(true, "Xbox Controller · " + (slot + 1));
                        ApplyReport(report); break;
                    }
                }
                Tick(delta);
                if (Engine.OutputFailures != lastOutputFailures)
                {
                    lastOutputFailures = Engine.OutputFailures;
                    if (now - lastError >= 5)
                    {
                        lastError = now;
                        OnNotice("Windows 未接收手柄模拟输入；请检查当前是否在安全桌面，或目标应用是否以更高权限运行。");
                    }
                }
            }
            catch (Exception exception)
            {
                CancelAll();
                if (now - lastError >= 5) { lastError = now; OnNotice("Xbox 输入已安全释放：" + exception.Message); }
            }
        }

        private void Post(Action action)
        {
            Control target = marshal;
            if (disposed || target == null || target.IsDisposed) return;
            try { target.BeginInvoke((MethodInvoker)delegate { if (!disposed) action(); }); }
            catch (InvalidOperationException) { }
        }

        private void PowerChanged(object sender, PowerModeChangedEventArgs args)
        {
            if (args.Mode == PowerModes.Suspend) Post(delegate { SuspendReason("power", true); });
            else if (args.Mode == PowerModes.Resume) Post(delegate { SuspendReason("power", false); });
        }

        private void SessionChanged(object sender, SessionSwitchEventArgs args)
        {
            switch (args.Reason)
            {
                case SessionSwitchReason.SessionLock: Post(delegate { SuspendReason("lock", true); }); break;
                case SessionSwitchReason.SessionUnlock: Post(delegate { SuspendReason("lock", false); }); break;
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteDisconnect: Post(delegate { SuspendReason("session", true); }); break;
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.RemoteConnect: Post(delegate { SuspendReason("session", false); }); break;
            }
        }

        private void SuspendReason(string reason, bool suspend)
        {
            if (suspend) sleepReasons.Add(reason); else sleepReasons.Remove(reason);
            SetSleeping(sleepReasons.Count != 0); previousTick = Clock();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
            SystemEvents.PowerModeChanged -= PowerChanged;
            SystemEvents.SessionSwitch -= SessionChanged;
            CancelAll(); Engine.Stop();
            if (marshal != null) { marshal.Dispose(); marshal = null; }
        }
    }

    internal sealed class WindowsControllerXInput
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Gamepad
        {
            internal ushort Buttons;
            internal byte LeftTrigger, RightTrigger;
            internal short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct State { internal uint Packet; internal Gamepad Gamepad; }
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.Winapi)]
        private static extern uint GetState14(uint index, out State state);
        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState", CallingConvention = CallingConvention.Winapi)]
        private static extern uint GetState91(uint index, out State state);
        private int api = 14;
        internal bool TryRead(int slot, out WindowsControllerReport report)
        {
            report = new WindowsControllerReport();
            if (slot < 0 || slot > 3 || api == 0) return false;
            State state = new State(); uint error = 1167;
            if (api == 14)
            {
                try { error = GetState14((uint)slot, out state); }
                catch (DllNotFoundException) { api = 91; }
                catch (EntryPointNotFoundException) { api = 91; }
            }
            if (api == 91)
            {
                try { error = GetState91((uint)slot, out state); }
                catch (DllNotFoundException) { api = 0; }
                catch (EntryPointNotFoundException) { api = 0; }
            }
            if (api == 0 || error != 0) return false;
            Gamepad pad = state.Gamepad;
            report = new WindowsControllerReport
            {
                Buttons = pad.Buttons, LT = pad.LeftTrigger / 255.0, RT = pad.RightTrigger / 255.0,
                LX = WindowsControllerReport.NormalizeAxis(pad.ThumbLX), LY = WindowsControllerReport.NormalizeAxis(pad.ThumbLY),
                RX = WindowsControllerReport.NormalizeAxis(pad.ThumbRX), RY = WindowsControllerReport.NormalizeAxis(pad.ThumbRY)
            };
            return true;
        }
    }

    internal static class WindowsControllerNative
    {
        // LLKHF_INJECTED is already rejected by KeyboardHook. This tag also
        // identifies the controller mouse stream to cooperating input tools.
        private static readonly UIntPtr EventTag = new UIntPtr(0x43585447u);
        [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput
        { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] internal MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputUnion Union; }
        private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rectangle, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
        [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
        private static ControllerRect[] cachedDisplays;
        private static double displayCacheTime;
        internal static double DoubleClickInterval { get { return GetDoubleClickTime() / 1000.0; } }
        internal static ControllerPoint Position()
        {
            Point point;
            if (!GetPhysicalCursorPos(out point)) return new ControllerPoint();
            return new ControllerPoint(point.X, point.Y);
        }
        internal static ControllerRect[] Displays()
        {
            double now = WindowsControllerClock.Now;
            if (cachedDisplays != null && now - displayCacheTime < 0.5) return cachedDisplays;
            var result = new List<ControllerRect>(); IntPtr old = IntPtr.Zero;
            // Scope per-monitor awareness to this native query; do not alter
            // WinForms layout DPI or mix logical Screen.Bounds with physical
            // GetPhysicalCursorPos coordinates on mixed-DPI monitors.
            try { old = SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { }
            try
            {
                MonitorCallback callback = delegate(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data)
                { result.Add(new ControllerRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)); return true; };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero); GC.KeepAlive(callback);
            }
            finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
            if (result.Count == 0) return cachedDisplays ?? new ControllerRect[0];
            cachedDisplays = result.ToArray(); displayCacheTime = now; return cachedDisplays;
        }
        internal static bool Post(WindowsControllerOutput value)
        {
            var values = new List<Input>();
            if (value.Kind == "move")
            {
                ControllerRect[] displays = Displays(); if (displays.Length == 0) return false;
                double left = displays[0].X, top = displays[0].Y, right = left + displays[0].Width, bottom = top + displays[0].Height;
                foreach (ControllerRect rect in displays)
                { left = Math.Min(left, rect.X); top = Math.Min(top, rect.Y); right = Math.Max(right, rect.X + rect.Width); bottom = Math.Max(bottom, rect.Y + rect.Height); }
                values.Add(Mouse(0x0001 | 0x4000 | 0x8000, 0,
                    (int)WindowsControllerEngine.Clamp((value.Point.X - left + 0.5) * 65536 / (right - left), 0, 65535),
                    (int)WindowsControllerEngine.Clamp((value.Point.Y - top + 0.5) * 65536 / (bottom - top), 0, 65535)));
            }
            else if (value.Kind == "button")
            {
                uint down = value.Button == "left" ? 0x0002u : value.Button == "right" ? 0x0008u : 0x0020u;
                // Windows derives double-clicks from real down/up timing and
                // pointer distance; no global click settings are changed.
                values.Add(Mouse(value.Down ? down : down << 1, 0, 0, 0));
            }
            else if (value.Kind == "scroll")
            {
                if (value.Vertical != 0) values.Add(Mouse(0x0800, unchecked((uint)value.Vertical), 0, 0));
                if (value.Horizontal != 0) values.Add(Mouse(0x1000, unchecked((uint)value.Horizontal), 0, 0));
            }
            return values.Count == 0 || SendInput((uint)values.Count, values.ToArray(), Marshal.SizeOf(typeof(Input))) == (uint)values.Count;
        }
        private static Input Mouse(uint flags, uint data, int x, int y)
        {
            return new Input { Type = 0, Union = new InputUnion { Mouse = new MouseInput { Flags = flags, Data = data, X = x, Y = y, ExtraInfo = EventTag } } };
        }
    }
}
