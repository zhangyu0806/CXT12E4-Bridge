using System;
using System.Collections.Generic;

namespace Cxt1024Bridge
{
    internal sealed class WindowsControllerContext
    {
        internal int Layer, ProcessId;
        internal IntPtr Window;
        internal long Generation;
        internal string ProcessName = string.Empty;
        internal WindowsControllerContext Clone()
        { return new WindowsControllerContext { Layer = Layer, ProcessId = ProcessId, Window = Window, Generation = Generation, ProcessName = ProcessName }; }
        internal bool Matches(WindowsControllerContext other)
        { return other != null && Layer == other.Layer && Window == other.Window && Generation == other.Generation && ProcessId == other.ProcessId; }
    }

    internal struct WindowsControllerReport
    {
        internal ushort Buttons;
        internal double LX, LY, RX, RY, LT, RT;
        internal static double NormalizeAxis(short value) { return value < 0 ? value / 32768.0 : value / 32767.0; }
        internal bool Neutral(double deadZone)
        {
            // BACK/View and Guide are deliberately absent; Bridge does not own
            // their system gestures. The XInput reserved bitfield is ignored.
            return (Buttons & 0xF3DF) == 0 && LT < 0.2 && RT < 0.2 &&
                WindowsControllerEngine.Length(LX, LY) <= deadZone && WindowsControllerEngine.Length(RX, RY) <= deadZone;
        }
    }

    internal sealed class WindowsControllerInputState
    {
        internal bool AwaitingNeutral { get; private set; }
        private bool leftTrigger, rightTrigger;
        private readonly HashSet<string> previous = new HashSet<string>();
        internal WindowsControllerInputState() { AwaitingNeutral = true; }
        internal void Reset() { AwaitingNeutral = true; leftTrigger = rightTrigger = false; previous.Clear(); }
        internal bool Feed(WindowsControllerReport report, double deadZone, Action<string, bool> button)
        {
            if (AwaitingNeutral)
            {
                if (!report.Neutral(deadZone)) return false;
                AwaitingNeutral = false;
            }
            leftTrigger = leftTrigger ? report.LT > 0.4 : report.LT >= 0.55;
            rightTrigger = rightTrigger ? report.RT > 0.4 : report.RT >= 0.55;
            string[] names = { "lt", "rt", "up", "down", "left", "right", "menu", "l3", "r3", "lb", "rb", "a", "b", "x", "y" };
            ushort[] bits = { 0, 0, 0x1, 0x2, 0x4, 0x8, 0x10, 0x40, 0x80, 0x100, 0x200, 0x1000, 0x2000, 0x4000, 0x8000 };
            for (int i = 0; i < names.Length; i++)
            {
                bool down = i == 0 ? leftTrigger : i == 1 ? rightTrigger : (report.Buttons & bits[i]) != 0;
                bool old = previous.Contains(names[i]);
                if (down == old) continue;
                if (down) previous.Add(names[i]); else previous.Remove(names[i]);
                button(names[i], down);
            }
            return true;
        }
    }

    // All mutation is on the UI thread. Native polling is an adapter around
    // this pure state machine; slow application automation stays in the
    // existing action queue and never in the analog-motion tick.
    internal class WindowsControllerState
    {
        private sealed class Press
        {
            internal string Action;
            internal WindowsControllerContext Context;
            internal double Began, NextRepeat;
            internal bool Fired, Cancelled;
        }
        protected readonly WindowsProfileStore Profiles;
        internal readonly WindowsControllerEngine Engine;
        protected readonly Func<double> Clock;
        private readonly Dictionary<string, Press> presses = new Dictionary<string, Press>();
        private readonly HashSet<string> held = new HashSet<string>(), blockedHeld = new HashSet<string>();
        private readonly WindowsControllerInputState input = new WindowsControllerInputState();
        private WindowsControllerReport axes;
        private WindowsProfileDocument configuration;
        private readonly Dictionary<int, Dictionary<string, string>> mappings = new Dictionary<int, Dictionary<string, string>>();
        private bool sleeping, panelVisible, lastEffective, requireLayerNeutral = true, requireMouseNeutral = true, requireAllNeutral = true;
        private string lastLeftMode, lastRightMode;
        private double lastOrbit;
        internal Func<WindowsControllerContext> Context = delegate { return new WindowsControllerContext(); };
        internal Action BeforeInput = delegate { };
        internal Action<string, WindowsControllerContext, double> OnAction = delegate { };
        internal Action OnChooser = delegate { };
        internal Action OnHelp = delegate { };
        internal Action<string> OnNotice = delegate { };
        internal Func<string, bool, bool> OnPanelInput = delegate { return false; };
        internal bool Connected { get; private set; }
        internal string ControllerName { get; private set; }
        internal long Reports { get; private set; }
        internal string LastInput { get; private set; }
        internal double LastInputTime { get; private set; }
        internal bool Enabled { get { return configuration.XboxEnabled; } }
        internal bool ApplicationPaused
        {
            get
            {
                string current = NormalizeProcess(Context().ProcessName);
                foreach (string name in configuration.PausedProcesses)
                    if (current.Length > 0 && string.Equals(current, NormalizeProcess(name), StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }
        internal bool Effective { get { return Connected && Enabled && !sleeping && !ApplicationPaused && !panelVisible; } }
        internal string Status
        {
            get { return !Connected ? "Xbox：未连接" : sleeping ? "Xbox：休眠／锁屏暂停" : ApplicationPaused ? "Xbox：此应用已暂停" : !Enabled ? "Xbox：已暂停" : panelVisible ? "Xbox：选择面板" : "Xbox：已连接 · Menu 长按暂停"; }
        }
        internal WindowsControllerState(WindowsProfileStore profiles, WindowsControllerEngine engine, Func<double> clock)
        { Profiles = profiles; Engine = engine; Clock = clock; ControllerName = "未连接"; LastInput = "等待输入"; Configure(); }

        internal static string NormalizeProcess(string value)
        {
            string result = (value ?? string.Empty).Trim();
            if (result.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) result = result.Substring(0, result.Length - 4);
            return result;
        }

        internal void Configure()
        {
            CancelAll(); WindowsProfileDocument d = Profiles.Document; configuration = d;
            mappings.Clear();
            for (int layer = 0; layer < 8; layer++)
            {
                var map = new Dictionary<string, string>();
                foreach (ProfileControl control in WindowsLayerProfiles.Controls("xbox"))
                    map[control.Id] = WindowsProfileStore.Resolve(d, "xbox", control.Id, layer);
                mappings[layer] = map;
            }
            Engine.DeadZone = d.DeadZone; Engine.CursorSpeed = d.CursorSpeed; Engine.ScrollSpeed = d.ScrollSpeed;
            Engine.PrecisionScale = d.PrecisionScale; Engine.InvertScroll = d.InvertScroll;
            lastLeftMode = AxisMode("leftstick"); lastRightMode = AxisMode("rightstick"); SyncEnabled();
        }

        internal void ConnectionChanged(bool connected, string name)
        {
            CancelAll(); Connected = connected; ControllerName = name ?? "Xbox Controller";
            held.Clear(); blockedHeld.Clear(); input.Reset(); SyncEnabled(); OnNotice(Status);
        }

        internal void SetSleeping(bool value)
        {
            if (sleeping == value) return;
            sleeping = value; CancelAll(); held.Clear(); blockedHeld.Clear(); input.Reset(); SyncEnabled(); OnNotice(Status);
        }

        internal void ApplyReport(WindowsControllerReport report)
        {
            if (!Connected || sleeping) return;
            // Refresh context before a physical event and before analog mode
            // selection. The dispatcher independently rechecks HWND/generation.
            BeforeInput(); Reports++;
            SyncEnabled();
            axes = report;
            bool physicalNeutral = report.Neutral(configuration.DeadZone);
            if (!physicalNeutral)
            {
                LastInputTime = Clock();
                LastInput = string.Format("LX {0:0.00}, LY {1:0.00}; RX {2:0.00}, RY {3:0.00}; LT {4:0.00}, RT {5:0.00}; 0x{6:X4}",
                    report.LX, report.LY, report.RX, report.RY, report.LT, report.RT, report.Buttons);
            }
            // Permit the neutral sample before its following button edges.
            if (!input.AwaitingNeutral || physicalNeutral) UpdateAxes(true);
            input.Feed(report, configuration.DeadZone, Button);
        }

        internal void CancelAll()
        {
            presses.Clear(); foreach (string name in held) blockedHeld.Add(name);
            axes = new WindowsControllerReport(); Engine.Suspend(); requireLayerNeutral = requireMouseNeutral = requireAllNeutral = true;
        }

        internal void ContextChanged()
        {
            foreach (Press press in presses.Values)
                if (!press.Action.StartsWith("mouse.", StringComparison.Ordinal)) press.Cancelled = true;
            string left = AxisMode("leftstick"), right = AxisMode("rightstick");
            if (left != lastLeftMode || right != lastRightMode) requireMouseNeutral = true;
            lastLeftMode = left; lastRightMode = right; requireLayerNeutral = true;
            SyncEnabled(); UpdateAxes(false);
        }

        internal void SetPanelVisible(bool visible) { panelVisible = visible; CancelAll(); SyncEnabled(); }

        internal void Toggle()
        {
            WindowsProfileDocument next = Profiles.Document; next.XboxEnabled = !next.XboxEnabled;
            try { Profiles.Replace(next); Configure(); OnNotice(Status); }
            catch (Exception exception) { OnNotice("Xbox 暂停设置未保存：" + exception.Message); }
        }

        private void SyncEnabled()
        {
            bool value = Effective;
            if (value == lastEffective) return;
            CancelAll(); Engine.SetEnabled(value); lastEffective = value;
        }

        internal void Button(string name, bool down)
        {
            double now = Clock();
            if (down) { if (!held.Add(name)) return; }
            else { held.Remove(name); blockedHeld.Remove(name); }
            if (name == "lt" || name == "rt")
            { if (down && blockedHeld.Count != 0) blockedHeld.Add(name); return; }
            Press press;
            if (!down && presses.TryGetValue(name, out press))
            {
                presses.Remove(name);
                if (press.Action.StartsWith("mouse.", StringComparison.Ordinal))
                {
                    bool another = false;
                    foreach (Press other in presses.Values) if (other.Action == press.Action) { another = true; break; }
                    if (!another) Engine.MouseButton(press.Action.Substring(6), false);
                }
                else if (name == "menu" && !press.Fired && !press.Cancelled && press.Context.Matches(Context())) OnChooser();
                return;
            }
            // A panel must not keep consuming a game's controller after the
            // foreground changes to an excluded process. Original releases
            // above still balance mouse ownership regardless of focus.
            if (!Connected || sleeping || ApplicationPaused) return;
            if (OnPanelInput(name, down)) return;
            if (!down) return;
            if (blockedHeld.Count != 0) { blockedHeld.Add(name); return; }
            if (name == "menu")
            {
                // Leave Menu to a paused game, but allow global resume outside
                // application exclusions while ordinary mappings are paused.
                if (!Connected || sleeping || ApplicationPaused) return;
                presses[name] = new Press { Action = "bridge.menu", Context = Context().Clone(), Began = now, NextRepeat = now + 0.85 }; return;
            }
            SyncEnabled(); if (!Effective) return;
            WindowsControllerContext context = Context().Clone();
            string control = held.Contains("rt") ? "rt." + name : name;
            string action = Action(control, context.Layer);
            presses[name] = new Press { Action = action, Context = context, Began = now, NextRepeat = now + 0.4 };
            if (action.StartsWith("mouse.", StringComparison.Ordinal)) Engine.MouseButton(action.Substring(6), true);
            else Fire(action, context, now);
        }

        private void Fire(string action, WindowsControllerContext context, double now)
        {
            if (action == "none") return;
            if (action == "bridge.help") OnHelp();
            else if (action == "bridge.chooser") OnChooser();
            else OnAction(action, context, now);
        }

        internal void Tick(double delta)
        {
            SyncEnabled(); double now = Clock();
            foreach (string name in new List<string>(presses.Keys))
            {
                Press press;
                if (!presses.TryGetValue(name, out press) || press.Cancelled) continue;
                if (!press.Action.StartsWith("mouse.", StringComparison.Ordinal) && !press.Context.Matches(Context())) { press.Cancelled = true; continue; }
                if (name == "menu" && !press.Fired && now - press.Began >= 0.85)
                {
                    press.Fired = true; Toggle();
                    // Configure can clear ownership; retain a consumed release.
                    press.Cancelled = true; presses[name] = press; held.Add(name);
                }
                else if (Effective && now >= press.NextRepeat && IsRepeatable(press.Action))
                { press.NextRepeat = now + 0.09; Fire(press.Action, press.Context, now); }
            }
            Engine.Tick(delta);
            if (!Effective || requireLayerNeutral || now - lastOrbit < 0.14) return;
            WindowsControllerContext context = Context().Clone(); if (context.Layer != 5) return;
            double x = 0, y = 0;
            if (AxisMode("leftstick") == "stick.blender") { x += axes.LX; y += axes.LY; }
            if (AxisMode("rightstick") == "stick.blender") { x += axes.RX; y += axes.RY; }
            if (Math.Max(Math.Abs(x), Math.Abs(y)) <= 0.45) return;
            lastOrbit = now;
            OnAction(Math.Abs(x) > Math.Abs(y) ? "encoder.1." + (x > 0 ? "2" : "0") : "encoder.2." + (y > 0 ? "0" : "2"), context, now);
        }

        private static bool IsRepeatable(string action)
        { return action == "key.up" || action == "key.down" || action == "key.left" || action == "key.right" || action == "key.pageup" || action == "key.pagedown"; }

        private string Action(string control, int layer)
        {
            Dictionary<string, string> map; string action;
            return mappings.TryGetValue(layer, out map) && map.TryGetValue(control, out action) ? action : "none";
        }
        private string AxisMode(string name) { return Action(name, Context().Layer); }

        private void UpdateAxes(bool physicalSample)
        {
            if (physicalSample && axes.Neutral(configuration.DeadZone)) requireAllNeutral = false;
            if (requireAllNeutral) { Engine.SetAxes(0, 0, 0, 0, false, false); return; }
            bool neutral = WindowsControllerEngine.Length(axes.LX, axes.LY) <= configuration.DeadZone &&
                WindowsControllerEngine.Length(axes.RX, axes.RY) <= configuration.DeadZone;
            if (physicalSample && neutral) requireLayerNeutral = requireMouseNeutral = false;
            double mx = 0, my = 0, sx = 0, sy = 0;
            string left = AxisMode("leftstick"), right = AxisMode("rightstick");
            if (!requireMouseNeutral && left == "stick.mouse") { mx += axes.LX; my += axes.LY; }
            if (!requireMouseNeutral && right == "stick.mouse") { mx += axes.RX; my += axes.RY; }
            if (!requireLayerNeutral && left == "stick.scroll") { sx += axes.LX; sy += axes.LY; }
            if (!requireLayerNeutral && right == "stick.scroll") { sx += axes.RX; sy += axes.RY; }
            Engine.SetAxes(mx, my, sx, sy, axes.LT > 0.2, physicalSample && neutral);
        }
    }
}
