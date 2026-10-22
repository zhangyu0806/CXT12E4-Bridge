using System;

namespace Cxt1024Bridge
{
    [Flags]
    internal enum DesktopShortcutModifiers
    {
        None = 0,
        Control = 1,
        Shift = 2,
        Alt = 4
    }

    // A description, never an input action. Shared instances remain immutable
    // and contain no foreground-window state or caller-mutable collections.
    internal sealed class DesktopShortcut
    {
        private readonly ushort _key;
        private readonly DesktopShortcutModifiers _modifiers;
        private readonly bool _isCopyQ;
        private readonly string _name;

        internal DesktopShortcut(ushort key, DesktopShortcutModifiers modifiers, bool isCopyQ, string name)
        {
            _key = key;
            _modifiers = modifiers;
            _isCopyQ = isCopyQ;
            _name = name;
        }

        internal ushort Key { get { return _key; } }
        internal DesktopShortcutModifiers Modifiers { get { return _modifiers; } }
        internal bool IsCopyQ { get { return _isCopyQ; } }
        internal string Name { get { return _name; } }
    }

    internal static class DesktopLayerShortcuts
    {
        // These are numpad virtual keys, not navigation keys or top-row digits.
        private const ushort Numpad1 = 0x61;
        private const ushort Numpad2 = 0x62;
        private const ushort Numpad4 = 0x64;
        private const ushort Numpad6 = 0x66;
        private const ushort Numpad7 = 0x67;
        private const ushort Numpad8 = 0x68;
        private const ushort NumpadDecimal = 0x6E;
        private const DesktopShortcutModifiers Ctrl = DesktopShortcutModifiers.Control;
        private const DesktopShortcutModifiers Shift = DesktopShortcutModifiers.Shift;
        private const DesktopShortcutModifiers Alt = DesktopShortcutModifiers.Alt;

        // User-facing L8 retains its stable internal index 0. Every entry is
        // fixed: selecting an application must not change the shortcut table.
        private static readonly DesktopShortcut[] UniversalKeys =
        {
            Shortcut(Vk.C, Ctrl | Alt, "Screenshot"),
            Shortcut(Vk.V, Ctrl | Alt, "Pin screenshot"),
            Shortcut(Vk.Z, Ctrl, "Undo"),
            Shortcut(Vk.Y, Ctrl, "Redo"),
            Shortcut(Vk.X, Ctrl, "Cut"),
            Shortcut(Vk.C, Ctrl, "Copy"),
            Shortcut(Vk.V, Ctrl, "Paste"),
            Shortcut(Vk.V, Ctrl | Shift, "Paste plain text"),
            Shortcut(Vk.T, Ctrl, "New tab"),
            Shortcut(Vk.T, Ctrl | Shift, "Reopen closed tab"),
            Shortcut(Vk.F, Ctrl, "Find"),
            new DesktopShortcut(0, DesktopShortcutModifiers.None, true, "CopyQ")
        };

        private static readonly DesktopShortcut[] BlenderKeys =
        {
            Shortcut(Vk.Tab, "Toggle edit mode"),
            Shortcut(Vk.E, "Extrude"),
            Shortcut(Vk.G, "Move"),
            Shortcut(Vk.R, "Rotate"),
            Shortcut(Vk.S, "Scale"),
            Shortcut(Vk.I, "Inset"),
            Shortcut(Vk.B, Ctrl, "Bevel"),
            Shortcut(Vk.D, Shift, "Duplicate"),
            Shortcut(Vk.Z, Ctrl, "Undo"),
            Shortcut(Vk.Z, Ctrl | Shift, "Redo"),
            Shortcut(Vk.A, Shift, "Add"),
            Shortcut(Vk.F3, "Search")
        };

        // Columns are explicitly selected by EncoderColumn, independently of
        // the enum's numeric values: counterclockwise, press, clockwise.
        private static readonly DesktopShortcut[,] UniversalEncoders =
        {
            { Shortcut(Vk.Left, Alt, "Back"), Shortcut(Vk.L, Ctrl, "Address bar"), Shortcut(Vk.Right, Alt, "Forward") },
            { Shortcut(Vk.Tab, Ctrl | Shift, "Previous tab"), Shortcut(Vk.W, Ctrl, "Close tab"), Shortcut(Vk.Tab, Ctrl, "Next tab") },
            { Shortcut(Vk.OemMinus, Ctrl, "Zoom out"), Shortcut(Vk.Zero, Ctrl, "Reset zoom"), Shortcut(Vk.OemPlus, Ctrl, "Zoom in") },
            { null, Shortcut(Vk.F5, "Refresh"), null }
        };

        private static readonly DesktopShortcut[,] BlenderEncoders =
        {
            { Shortcut(Numpad4, "Orbit left"), Shortcut(Numpad1, "Front view"), Shortcut(Numpad6, "Orbit right") },
            { Shortcut(Numpad8, "Orbit up"), Shortcut(Numpad7, "Top view"), Shortcut(Numpad2, "Orbit down") },
            { Shortcut(Vk.Left, "Previous frame"), Shortcut(Vk.Space, "Play or pause"), Shortcut(Vk.Right, "Next frame") },
            { null, Shortcut(NumpadDecimal, "Frame selected"), null }
        };

        internal static DesktopShortcut ForKey(int layer, int key)
        {
            if (key < 1 || key > 12)
                return null;
            if (layer == 0)
                return UniversalKeys[key - 1];
            if (layer == 5)
                return BlenderKeys[key - 1];
            return null;
        }

        internal static DesktopShortcut ForEncoder(int layer, int encoder, EncoderGesture gesture)
        {
            if (encoder < 1 || encoder > 4)
                return null;
            int column = EncoderColumn(gesture);
            if (column < 0)
                return null;
            if (layer == 0)
                return UniversalEncoders[encoder - 1, column];
            if (layer == 5)
                return BlenderEncoders[encoder - 1, column];
            return null;
        }

        private static int EncoderColumn(EncoderGesture gesture)
        {
            switch (gesture)
            {
                case EncoderGesture.CounterClockwise: return 0;
                case EncoderGesture.Press: return 1;
                case EncoderGesture.Clockwise: return 2;
                default: return -1;
            }
        }

        private static DesktopShortcut Shortcut(ushort key, string name)
        {
            return Shortcut(key, DesktopShortcutModifiers.None, name);
        }

        private static DesktopShortcut Shortcut(ushort key, DesktopShortcutModifiers modifiers, string name)
        {
            return new DesktopShortcut(key, modifiers, false, name);
        }
    }
}
