using System;
using System.Collections.Generic;
using System.IO;

namespace Cxt1024Bridge
{
    internal static class WindowsControllerTests
    {
        private sealed class Fixture : IDisposable
        {
            internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "cxt-controller-test-" + Guid.NewGuid().ToString("N"));
            internal WindowsProfileStore Profiles;
            internal WindowsControllerEngine Engine;
            internal WindowsControllerState State;
            internal WindowsControllerContext Context;
            internal readonly List<WindowsControllerOutput> Outputs = new List<WindowsControllerOutput>();
            internal readonly List<string> Actions = new List<string>();
            internal ControllerPoint Point = new ControllerPoint(300, 300);
            internal double Now;
            internal int Chosen, Helped;
            internal bool RejectRelease;
            internal Fixture()
            {
                Profiles = new WindowsProfileStore(Path.Combine(DirectoryPath, "profiles.json"));
                Engine = new WindowsControllerEngine(delegate(WindowsControllerOutput value)
                {
                    if (RejectRelease && value.Kind == "button" && !value.Down) return false;
                    Outputs.Add(value); if (value.Kind == "move") Point = value.Point; return true;
                }, delegate { return Point; }, delegate { return new[] { new ControllerRect(0, 0, 1920, 1080), new ControllerRect(-1280, -200, 1280, 1024) }; }, delegate { return Now; }, 0.5);
                State = new WindowsControllerState(Profiles, Engine, delegate { return Now; });
                Context = new WindowsControllerContext { Window = new IntPtr(11), Layer = 1, Generation = 1, ProcessId = 123, ProcessName = "OpenAI.Codex" };
                State.Context = delegate { return Context; };
                State.OnAction = delegate(string action, WindowsControllerContext context, double time) { Actions.Add(action); };
                State.OnChooser = delegate { Chosen++; };
                State.OnHelp = delegate { Helped++; };
                Profiles.Changed += State.Configure;
                Reset();
            }
            internal void Reset()
            {
                State.ConnectionChanged(false, "Test Xbox");
                State.SetPanelVisible(false); State.SetSleeping(false);
                Profiles.Replace(new WindowsProfileDocument());
                Context.Layer = 1; Context.Window = new IntPtr(11); Context.Generation++; Context.ProcessName = "OpenAI.Codex";
                State.ContextChanged(); State.ConnectionChanged(true, "Test Xbox"); Sample();
                Outputs.Clear(); Actions.Clear(); Chosen = Helped = 0; Now += 2;
                State.OnPanelInput = delegate { return false; }; State.OnChooser = delegate { Chosen++; };
            }
            internal void Sample(ushort buttons = 0, double lx = 0, double ly = 0, double rx = 0, double ry = 0, double lt = 0, double rt = 0)
            { State.ApplyReport(new WindowsControllerReport { Buttons = buttons, LX = lx, LY = ly, RX = rx, RY = ry, LT = lt, RT = rt }); }
            internal void Advance(double delta) { Now += delta; State.Tick(delta); }
            internal int Count(string kind) { int count = 0; foreach (WindowsControllerOutput value in Outputs) if (value.Kind == kind) count++; return count; }
            internal bool Buttons(params string[] expected)
            {
                var values = new List<string>();
                foreach (WindowsControllerOutput value in Outputs) if (value.Kind == "button") values.Add(value.Button + ":" + value.Down);
                return string.Join(",", values.ToArray()) == string.Join(",", expected);
            }
            public void Dispose()
            { State.CancelAll(); Engine.Stop(); if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
        }

        internal static int Run()
        {
            int checks = 0;
            Action<bool, string> expect = delegate(bool value, string message)
            { checks++; if (!value) throw new InvalidOperationException("Windows controller: " + message); };
            expect(WindowsControllerReport.NormalizeAxis(short.MinValue) == -1 && WindowsControllerReport.NormalizeAxis(short.MaxValue) == 1, "signed axes include both exact endpoints");
            expect(new WindowsControllerReport { Buttons = 0x20 | 0x400 }.Neutral(0.16), "View and Guide do not become Bridge controls or block neutral");
            expect(!new WindowsControllerReport { Buttons = 0x1000 }.Neutral(0.16), "A held prevents initial neutral");
            var vector = WindowsControllerEngine.Vector(1, 1, 0.16, 1.7);
            expect(Math.Abs(WindowsControllerEngine.Length(vector.X, vector.Y) - 1) < 0.00001, "diagonals have no speed advantage");
            expect(WindowsControllerEngine.Vector(0.1, 0.1, 0.16, 1.7).X == 0, "radial dead zone removes drift");
            expect(WindowsControllerEngine.Vector(double.NaN, 1, 0.16, 1.7).X == 0, "invalid axis cannot produce movement");
            var rects = new[] { new ControllerRect(0, 0, 1920, 1080), new ControllerRect(-1280, -200, 1280, 1024) };
            var point = WindowsControllerEngine.Constrained(new ControllerPoint(-400, -100), rects);
            expect(point.X == -400 && point.Y == -100, "negative-coordinate monitor remains usable");
            point = WindowsControllerEngine.Constrained(new ControllerPoint(4000, 200), rects);
            expect(point.X == 1919, "outer monitor edge clamps to last pixel");

            var input = new WindowsControllerInputState(); var edges = new List<string>();
            Action<string, bool> edge = delegate(string name, bool down) { edges.Add(name + ":" + down); };
            expect(!input.Feed(new WindowsControllerReport { Buttons = 0x1000 }, 0.16, edge) && edges.Count == 0, "reconnect with held A cannot click");
            expect(input.Feed(new WindowsControllerReport(), 0.16, edge), "actual neutral arms input");
            input.Feed(new WindowsControllerReport { RT = 0.56 }, 0.16, edge);
            input.Feed(new WindowsControllerReport { RT = 0.50 }, 0.16, edge);
            input.Feed(new WindowsControllerReport { RT = 0.41 }, 0.16, edge);
            expect(edges.Count == 1 && edges[0] == "rt:True", "trigger hysteresis suppresses threshold chatter");
            input.Feed(new WindowsControllerReport { RT = 0.39 }, 0.16, edge);
            expect(edges.Count == 2 && edges[1] == "rt:False", "trigger releases at lower threshold");
            input.Reset(); input.Feed(new WindowsControllerReport { LX = 1 }, 0.16, edge);
            expect(input.AwaitingNeutral, "reconnect with deflected stick stays gated");

            using (var f = new Fixture())
            {
                for (int layer = 0; layer < 8; layer++)
                {
                    f.Reset(); f.Context.Layer = layer; f.Context.Generation++; f.State.ContextChanged(); f.Sample();
                    f.Sample(0x1000); f.Advance(1); f.Sample(); f.Sample(0x2000); f.Advance(1); f.Sample();
                    expect(f.Actions.Count == 2 && f.Actions[0] == "key.enter" && f.Actions[1] == "key.escape" && f.Outputs.Count == 0,
                        "A confirms once and B escapes once without mouse events in every layer");
                    f.Sample(0x4000, lx: 1); f.Advance(1.0 / 60); f.Sample(); f.Sample(0x8000); f.Sample();
                    expect(f.Buttons("left:True", "left:False", "right:True", "right:False") && f.Count("move") == 1,
                        "X drags and Y right-clicks consistently in every layer");
                    int count = f.Actions.Count; f.Sample(rt: 1); f.Sample(0x40, rt: 1); f.Sample(0x40); f.Sample();
                    expect(f.Actions.Count == count + 1 && f.Actions[count] == WindowsLayerProfiles.Defaults("xbox", layer)["rt.l3"] && f.Helped == 0,
                        "RT L3 retains original layer action with no trailing help on release");
                    f.Sample(0x40); f.Sample();
                    expect(f.Helped == 1, "unmodified L3 still opens layer settings");
                }
                f.Reset();
                f.Sample(rt: 1); f.Sample(0x1000, rt: 1); f.Sample(0x1000); f.Sample();
                expect(f.Actions.Count == 1 && f.Actions[0] == "edit.copy" && f.Outputs.Count == 0, "RT then A copies without later Enter");
                f.Reset(); f.Sample(rt: 1); f.Sample(0x4000, rt: 1); f.Sample(0x4000); f.Sample();
                expect(f.Actions.Count == 1 && f.Actions[0] == "edit.undo" && f.Outputs.Count == 0, "RT then X undoes without a trailing click");
                f.Reset(); f.Sample(0x4000); f.Sample(0x4000, rt: 1); f.Sample(rt: 1); f.Sample();
                expect(f.Buttons("left:True", "left:False") && f.Actions.Count == 0, "X then RT retains original drag ownership");
                f.Reset(); f.Profiles.Set("xbox", "a", 1, "mouse.left"); f.Sample();
                f.Sample(0x4000); f.Sample(0x5000); f.Sample(0x1000);
                expect(f.Buttons("left:True"), "shared mouse action has multiple physical owners");
                f.Sample(); expect(f.Buttons("left:True", "left:False"), "last physical owner releases mouse");
                f.Reset(); f.Sample(0x4000, lx: 1); f.Advance(1.0 / 60);
                f.Context.Window = new IntPtr(12); f.Context.Generation++; f.State.ContextChanged(); f.Advance(1.0 / 60);
                expect(f.Count("move") == 2 && f.Outputs[f.Outputs.Count - 1].Drag == "left", "same mouse mode supports cross-window drag");
                f.Sample(); expect(f.Buttons("left:True", "left:False"), "focus switch cannot strand held mouse");
                f.Reset(); f.Sample(0x1); f.Context.Generation++; f.State.ContextChanged(); f.Advance(1);
                expect(f.Actions.Count == 1 && f.Actions[0] == "key.up", "context change cancels repeat");
                f.Sample();
                f.Reset(); f.Profiles.Set("xbox", "leftstick", 2, "stick.scroll"); f.Sample(); f.Sample(lx: 1); f.Advance(1.0 / 60);
                double oldX = f.Point.X; int oldOutputs = f.Outputs.Count;
                f.Context.Layer = 2; f.Context.Generation++; f.State.ContextChanged(); f.Sample(lx: 1); f.Advance(1.0 / 60);
                expect(f.Point.X == oldX && f.Outputs.Count == oldOutputs, "axis mode change waits for physical center");
                f.Sample(); f.Sample(lx: 1); f.Advance(1.0 / 60);
                expect(f.Outputs[f.Outputs.Count - 1].Kind == "scroll", "new scroll mode activates after center");
                f.Context.Layer = 1; f.Context.Generation++; f.State.ContextChanged(); f.Sample(lx: 1); f.Advance(1.0 / 60);
                expect(f.Point.X == oldX, "scroll-to-mouse transition cannot jump");
                f.Sample(); f.Sample(lx: 1); f.Advance(1.0 / 60);
                expect(f.Point.X > oldX, "mouse mode resumes after actual center");
                f.Reset(); f.Sample(0x10); f.State.ConnectionChanged(false, "Test Xbox"); f.State.Button("menu", false);
                expect(f.Chosen == 0, "disconnect never turns Menu hold into short press");
                f.Reset(); f.Sample(0x10); f.Advance(0.86); f.Sample();
                expect(!f.State.Enabled && f.Chosen == 0, "Menu hold pauses with no trailing chooser");
                f.Sample(0x10); f.Advance(0.86); f.Sample();
                expect(f.State.Enabled && f.Chosen == 0, "Menu hold resumes outside excluded applications");
                f.Reset(); f.Sample(0x10); f.Sample();
                expect(f.Chosen == 1, "Menu tap opens chooser exactly once");
                f.Reset(); bool panel = false; int selected = 0;
                f.State.OnChooser = delegate { panel = true; f.State.SetPanelVisible(true); };
                f.State.OnPanelInput = delegate(string name, bool down)
                {
                    if (!panel) return false;
                    if (name == "a" && down) { selected++; panel = false; f.State.SetPanelVisible(false); }
                    return true;
                };
                f.Sample(0x10); f.Sample(); f.Sample(0x1000, lx: 1); f.Advance(1.0 / 60); f.Sample();
                expect(selected == 1 && f.Outputs.Count == 0 && f.Actions.Count == 0, "chooser consumes confirm and suppresses Enter and analog leakage");
                f.Sample(0x4000); f.Sample();
                expect(f.Buttons("left:True", "left:False"), "fresh click works after panel-consumed release");
                f.Reset(); panel = true; f.State.SetPanelVisible(true);
                f.State.OnPanelInput = delegate(string name, bool down)
                {
                    if (!panel) return false;
                    if (name == "b" && down) { panel = false; f.State.SetPanelVisible(false); }
                    return true;
                };
                f.Sample(0x6000, lx: 1); f.Advance(1.0 / 60); f.Sample();
                expect(!panel && f.Actions.Count == 0 && f.Outputs.Count == 0, "chooser consumes B cancel and simultaneous X instead of leaking Esc or drag");
                f.Sample(0x2000); f.Sample();
                expect(f.Actions.Count == 1 && f.Actions[0] == "key.escape" && f.Outputs.Count == 0, "fresh B after panel cancellation sends Esc");
                f.Reset(); f.Sample(rt: 1); f.State.Configure(); f.Sample(0x1000, rt: 1); f.Sample(0x1000); f.Sample(0x3000); f.Sample(0x1000);
                expect(f.Outputs.Count == 0 && f.Actions.Count == 0, "config change quarantines held modifier and later buttons");
                f.Sample(); f.Sample(0x4000); f.Sample();
                expect(f.Buttons("left:True", "left:False"), "all releases permit fresh configured input");
                f.Reset(); f.Sample(0x4000); f.State.SetSleeping(true);
                expect(f.Buttons("left:True", "left:False"), "lock and sleep release injected drag");
                f.State.SetSleeping(false); f.Outputs.Clear(); f.Sample(0x4000, lx: 1); f.Advance(1);
                expect(f.Outputs.Count == 0, "wake with held controls cannot synthesize output");
                f.Sample(); f.Sample(0x4000); f.Sample();
                expect(f.Buttons("left:True", "left:False"), "wake resumes only after all controls neutral");
                f.Reset(); f.Context.ProcessName = "STEAM.EXE"; f.Context.Generation++; f.State.ContextChanged();
                f.Sample(0x10); f.Advance(1); f.Sample(0x1000, lx: 1); f.Advance(1); f.Sample();
                expect(f.State.ApplicationPaused && f.State.Enabled && f.Chosen == 0 && f.Actions.Count == 0 && f.Outputs.Count == 0, "real foreground process exclusion also preserves game Menu");
                int pausedPanelEvents = 0;
                f.State.OnPanelInput = delegate { pausedPanelEvents++; return true; };
                f.State.SetPanelVisible(true); f.Sample(0x1000); f.Sample();
                expect(pausedPanelEvents == 0, "foreground game exclusion also blocks background chooser input");
                f.Reset(); f.Sample(0x4000); f.RejectRelease = true; f.Sample();
                expect(f.Buttons("left:True"), "failed native release is retained for retry");
                f.State.Configure(); f.RejectRelease = false; f.Advance(1.0 / 60);
                expect(f.Buttons("left:True", "left:False"), "paused engine retries pending mouse release");
                f.Reset(); f.Sample(0x4000); f.Sample(); f.Now += 0.1; f.Sample(0x4000);
                expect(f.Outputs[f.Outputs.Count - 1].ClickCount == 2, "double-click timing is retained");
                f.Sample(); f.Reset(); oldX = f.Point.X; f.Sample(lx: 1, lt: 0.3); f.Advance(10);
                expect(f.Point.X - oldX <= 13, "precision and long timer stall do not replay movement backlog");
                f.Reset(); oldX = f.Point.X; f.Sample(lx: 0.25, lt: 0.3);
                for (int i = 0; i < 40; i++) f.Advance(1.0 / 60);
                expect(f.Point.X > oldX, "fractional precision movement accumulates instead of sticking");
                f.Reset(); f.Sample(ry: 1); f.Advance(1.0 / 60);
                expect(f.Outputs[f.Outputs.Count - 1].Vertical > 0, "up stick scrolls upward on Windows");
                f.Reset(); f.Sample(rx: 1); f.Advance(1.0 / 60);
                expect(f.Outputs[f.Outputs.Count - 1].Horizontal > 0, "right stick scrolls right on Windows");
                f.Reset(); f.Point = new ControllerPoint(1919, 300); f.Sample(lx: 0.6); f.Advance(0.002);
                expect(f.Point.X <= 1919, "fractional rounding cannot escape a monitor boundary");
                f.Reset(); f.Context.Layer = 5; f.Context.Generation++; f.State.ContextChanged(); f.Sample(); f.Sample(rx: 1); f.Advance(0.15);
                expect(f.Actions.Count == 1 && f.Actions[0] == "encoder.1.2", "Blender orbit uses bounded existing layer action");
                f.Advance(0.01); expect(f.Actions.Count == 1, "Blender orbit does not flood action queue");
                f.Reset(); f.Sample(0x20 | 0x400); f.Advance(1); f.Sample();
                expect(f.Actions.Count == 0 && f.Outputs.Count == 0 && f.Chosen == 0, "View and Guide never dispatch Bridge actions");
                f.Reset(); f.State.Configure(); f.Context.Generation++; f.State.ContextChanged(); f.Sample(lx: 1); f.Advance(1);
                expect(f.Outputs.Count == 0, "synthetic cancel zeros cannot unlock physical-neutral gate");
            }
            Console.WriteLine("PASS " + checks + " Windows controller checks"); return checks;
        }
    }
}
