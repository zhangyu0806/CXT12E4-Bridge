using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Cxt1024Bridge
{
    internal static class WindowsControllerClock
    {
        internal static double Now { get { return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency; } }
    }

    internal struct ControllerPoint
    {
        internal double X, Y;
        internal ControllerPoint(double x, double y) { X = x; Y = y; }
    }

    internal struct ControllerRect
    {
        internal double X, Y, Width, Height;
        internal ControllerRect(double x, double y, double width, double height)
        { X = x; Y = y; Width = width; Height = height; }
    }

    internal sealed class WindowsControllerOutput
    {
        internal string Kind, Button, Drag;
        internal bool Down;
        internal ControllerPoint Point;
        internal int ClickCount, Vertical, Horizontal;
    }

    // Pure output engine. Its clock, pointer, displays and sink are injectable;
    // the tests never load user32 or synthesize desktop events.
    internal sealed class WindowsControllerEngine
    {
        internal double DeadZone = 0.16, CursorSpeed = 1500, ScrollSpeed = 750, PrecisionScale = 0.25;
        internal bool InvertScroll;
        internal bool Enabled { get; private set; }
        internal bool AwaitingNeutral { get; private set; }
        internal long OutputFailures { get; private set; }
        private readonly Func<WindowsControllerOutput, bool> output;
        private readonly Func<ControllerPoint> location;
        private readonly Func<ControllerRect[]> displays;
        private readonly Func<double> clock;
        private readonly double doubleClickInterval;
        private readonly HashSet<string> held = new HashSet<string>();
        private readonly HashSet<string> pendingUp = new HashSet<string>();
        private readonly Dictionary<string, Click> clicks = new Dictionary<string, Click>();
        private readonly Dictionary<string, int> clickCounts = new Dictionary<string, int>();
        private double lx, ly, rx, ry, pointerFractionX, pointerFractionY, wheelX, wheelY;
        private bool precision;
        private sealed class Click { internal double Time; internal ControllerPoint Point; internal int Count; }

        internal WindowsControllerEngine(Func<WindowsControllerOutput, bool> output,
            Func<ControllerPoint> location, Func<ControllerRect[]> displays, Func<double> clock, double doubleClickInterval)
        {
            this.output = output; this.location = location; this.displays = displays;
            this.clock = clock; this.doubleClickInterval = doubleClickInterval; AwaitingNeutral = true;
        }

        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static double Length(double x, double y) { return Math.Sqrt(x * x + y * y); }
        internal static double Clamp(double value, double minimum, double maximum)
        { return Math.Max(minimum, Math.Min(maximum, value)); }

        internal static ControllerPoint Vector(double x, double y, double deadZone, double exponent)
        {
            if (!Finite(x) || !Finite(y)) return new ControllerPoint();
            double length = Length(x, y), dead = Clamp(deadZone, 0, 0.9);
            if (length <= dead) return new ControllerPoint();
            double magnitude = Math.Pow((Math.Min(1, length) - dead) / (1 - dead), Math.Max(0.1, exponent));
            return new ControllerPoint(x / length * magnitude, y / length * magnitude);
        }

        internal static ControllerPoint Constrained(ControllerPoint point, ControllerRect[] screens)
        {
            ControllerPoint closest = point;
            double distance = double.MaxValue;
            if (screens == null) return point;
            foreach (ControllerRect rect in screens)
            {
                if (rect.Width < 1 || rect.Height < 1) continue;
                if (point.X >= rect.X && point.X < rect.X + rect.Width && point.Y >= rect.Y && point.Y < rect.Y + rect.Height)
                    return point;
                var candidate = new ControllerPoint(Clamp(point.X, rect.X, rect.X + rect.Width - 1), Clamp(point.Y, rect.Y, rect.Y + rect.Height - 1));
                double next = Length(point.X - candidate.X, point.Y - candidate.Y);
                if (next < distance) { closest = candidate; distance = next; }
            }
            return closest;
        }

        internal void SetEnabled(bool enabled)
        {
            if (Enabled == enabled) return;
            Suspend(); Enabled = enabled;
        }

        internal void SetAxes(double leftX, double leftY, double rightX, double rightY, bool precise, bool physicallyNeutral)
        {
            if (!Finite(leftX) || !Finite(leftY) || !Finite(rightX) || !Finite(rightY)) { Suspend(); return; }
            lx = Clamp(leftX, -1, 1); ly = Clamp(leftY, -1, 1); rx = Clamp(rightX, -1, 1); ry = Clamp(rightY, -1, 1);
            precision = precise;
            // Synthetic zeros from cancelling/remapping cannot satisfy this gate.
            if (Enabled && physicallyNeutral && pendingUp.Count == 0) AwaitingNeutral = false;
        }

        internal void MouseButton(string name, bool down)
        {
            if (name != "left" && name != "right" && name != "middle") return;
            if (!down)
            {
                if (!held.Contains(name)) return;
                pendingUp.Add(name); FlushReleases(); return;
            }
            if (!Enabled || AwaitingNeutral || held.Contains(name) || pendingUp.Count != 0) return;
            ControllerPoint point = location(); double now = clock(); int count = 1;
            Click previous;
            if (clicks.TryGetValue(name, out previous) && now >= previous.Time && now - previous.Time <= doubleClickInterval &&
                Length(point.X - previous.Point.X, point.Y - previous.Point.Y) <= 5)
                count = previous.Count >= 3 ? 1 : previous.Count + 1;
            if (!Emit(new WindowsControllerOutput { Kind = "button", Button = name, Down = true, Point = point, ClickCount = count })) return;
            held.Add(name); clickCounts[name] = count;
            clicks[name] = new Click { Time = now, Point = point, Count = count };
        }

        private bool Emit(WindowsControllerOutput value)
        {
            try { if (output(value)) return true; } catch { }
            OutputFailures++; return false;
        }

        private void FlushReleases()
        {
            foreach (string name in new List<string>(pendingUp))
            {
                int count; if (!clickCounts.TryGetValue(name, out count)) count = 1;
                if (!Emit(new WindowsControllerOutput { Kind = "button", Button = name, Down = false, Point = location(), ClickCount = count })) continue;
                pendingUp.Remove(name); held.Remove(name); clickCounts.Remove(name);
            }
        }

        internal void Suspend()
        {
            foreach (string name in held) pendingUp.Add(name);
            FlushReleases(); clicks.Clear();
            lx = ly = rx = ry = pointerFractionX = pointerFractionY = wheelX = wheelY = 0;
            precision = false; AwaitingNeutral = true;
        }

        internal void Tick(double delta)
        {
            // Releases are retried even while paused; a failed SendInput cannot
            // silently abandon an earlier successfully injected mouse-down.
            FlushReleases();
            if (!Enabled || AwaitingNeutral || pendingUp.Count != 0 || !Finite(delta) || delta <= 0) return;
            double dt = Math.Min(delta, 1.0 / 30.0), scale = precision ? Clamp(PrecisionScale, 0.02, 1) : 1;
            ControllerPoint cursor = Vector(lx, ly, DeadZone, 1.7);
            if (cursor.X != 0 || cursor.Y != 0)
            {
                ControllerPoint old = location(); double speed = Clamp(CursorSpeed, 0, 5000) * scale * dt;
                ControllerPoint desired = new ControllerPoint(old.X + cursor.X * speed + pointerFractionX, old.Y - cursor.Y * speed + pointerFractionY);
                ControllerPoint constrained = Constrained(desired, displays());
                ControllerPoint next = Constrained(new ControllerPoint(Math.Round(constrained.X), Math.Round(constrained.Y)), displays());
                pointerFractionX = constrained.X - next.X; pointerFractionY = constrained.Y - next.Y;
                if (next.X != old.X || next.Y != old.Y)
                {
                    string drag = held.Contains("left") ? "left" : held.Contains("right") ? "right" : held.Contains("middle") ? "middle" : null;
                    if (!Emit(new WindowsControllerOutput { Kind = "move", Point = next, Drag = drag }))
                        pointerFractionX = pointerFractionY = 0;
                }
            }
            else pointerFractionX = pointerFractionY = 0;
            ControllerPoint scroll = Vector(rx, ry, DeadZone, 1.5);
            if (scroll.X == 0 && scroll.Y == 0) { wheelX = wheelY = 0; return; }
            // Windows uses wheel units (120 per notch), not macOS pixel scroll.
            double scrollFactor = Clamp(ScrollSpeed, 0, 5000) * scale * dt * (InvertScroll ? -1 : 1);
            wheelX += scroll.X * scrollFactor; wheelY += scroll.Y * scrollFactor;
            int horizontal = (int)wheelX, vertical = (int)wheelY;
            wheelX -= horizontal; wheelY -= vertical;
            if (horizontal != 0 || vertical != 0) Emit(new WindowsControllerOutput { Kind = "scroll", Vertical = vertical, Horizontal = horizontal });
        }

        internal void Stop() { SetEnabled(false); Suspend(); FlushReleases(); }
    }
}
