using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cxt1024Bridge
{
    internal static class Vk
    {
        internal const int Back = 0x08;
        internal const int Tab = 0x09;
        internal const int Enter = 0x0D;
        internal const int Shift = 0x10;
        internal const int Control = 0x11;
        internal const int Alt = 0x12;
        internal const int Escape = 0x1B;
        internal const int Space = 0x20;
        internal const int PageUp = 0x21;
        internal const int PageDown = 0x22;
        internal const int End = 0x23;
        internal const int Home = 0x24;
        internal const int Left = 0x25;
        internal const int Up = 0x26;
        internal const int Right = 0x27;
        internal const int Down = 0x28;
        internal const int Delete = 0x2E;
        internal const int Zero = 0x30;
        internal const int One = 0x31;
        internal const int Two = 0x32;
        internal const int Three = 0x33;
        internal const int Four = 0x34;
        internal const int Five = 0x35;
        internal const int Six = 0x36;
        internal const int Seven = 0x37;
        internal const int Eight = 0x38;
        internal const int Nine = 0x39;
        internal const int A = 0x41;
        internal const int B = 0x42;
        internal const int C = 0x43;
        internal const int D = 0x44;
        internal const int E = 0x45;
        internal const int F = 0x46;
        internal const int G = 0x47;
        internal const int H = 0x48;
        internal const int I = 0x49;
        internal const int J = 0x4A;
        internal const int K = 0x4B;
        internal const int L = 0x4C;
        internal const int M = 0x4D;
        internal const int N = 0x4E;
        internal const int O = 0x4F;
        internal const int P = 0x50;
        internal const int Q = 0x51;
        internal const int R = 0x52;
        internal const int S = 0x53;
        internal const int T = 0x54;
        internal const int U = 0x55;
        internal const int V = 0x56;
        internal const int W = 0x57;
        internal const int X = 0x58;
        internal const int Y = 0x59;
        internal const int Z = 0x5A;
        internal const int LeftWindows = 0x5B;
        internal const int F1 = 0x70;
        internal const int F2 = 0x71;
        internal const int F3 = 0x72;
        internal const int F4 = 0x73;
        internal const int F5 = 0x74;
        internal const int F6 = 0x75;
        internal const int F7 = 0x76;
        internal const int F8 = 0x77;
        internal const int F9 = 0x78;
        internal const int F10 = 0x79;
        internal const int F11 = 0x7A;
        internal const int F12 = 0x7B;
        internal const int LeftShift = 0xA0;
        internal const int RightShift = 0xA1;
        internal const int LeftControl = 0xA2;
        internal const int RightControl = 0xA3;
        internal const int LeftAlt = 0xA4;
        internal const int OemSemicolon = 0xBA;
        internal const int OemPlus = 0xBB;
        internal const int OemComma = 0xBC;
        internal const int OemMinus = 0xBD;
        internal const int OemPeriod = 0xBE;
        internal const int OemSlash = 0xBF;
        internal const int OemOpenBrackets = 0xDB;
        internal const int OemPipe = 0xDC;
        internal const int OemCloseBrackets = 0xDD;
    }

    internal static class InputSender
    {
        internal static bool WaitForKeyRelease(int virtualKeyCode, int timeoutMilliseconds)
        {
            if (timeoutMilliseconds < 0)
                timeoutMilliseconds = 0;
            int started = Environment.TickCount;
            while ((NativeMethods.GetAsyncKeyState(virtualKeyCode) & 0x8000) != 0)
            {
                if (unchecked(Environment.TickCount - started) >= timeoutMilliseconds)
                    return false;
                Thread.Sleep(1);
            }
            return true;
        }

        internal static void Tap(int virtualKeyCode)
        {
            SendKeyboard(new[]
            {
                Keyboard(virtualKeyCode, false),
                Keyboard(virtualKeyCode, true)
            });
        }

        internal static void Chord(int virtualKeyCode, params int[] modifiers)
        {
            var inputs = new List<NativeMethods.Input>();
            if (modifiers != null)
            {
                for (int i = 0; i < modifiers.Length; i++)
                    inputs.Add(Keyboard(modifiers[i], false));
            }
            inputs.Add(Keyboard(virtualKeyCode, false));
            inputs.Add(Keyboard(virtualKeyCode, true));
            if (modifiers != null)
            {
                for (int i = modifiers.Length - 1; i >= 0; i--)
                    inputs.Add(Keyboard(modifiers[i], true));
            }
            SendKeyboard(inputs.ToArray());
        }

        internal static void ChordWithDwell(int virtualKeyCode, int dwellMilliseconds, params int[] modifiers)
        {
            if (dwellMilliseconds < 0)
                dwellMilliseconds = 0;

            if (modifiers != null)
            {
                for (int i = 0; i < modifiers.Length; i++)
                    SendKeyboard(new[] { Keyboard(modifiers[i], false) });
            }

            if (dwellMilliseconds > 0)
                Thread.Sleep(dwellMilliseconds);

            SendKeyboard(new[] { Keyboard(virtualKeyCode, false) });
            if (dwellMilliseconds > 0)
                Thread.Sleep(dwellMilliseconds);
            SendKeyboard(new[] { Keyboard(virtualKeyCode, true) });

            if (dwellMilliseconds > 0)
                Thread.Sleep(dwellMilliseconds);

            if (modifiers != null)
            {
                for (int i = modifiers.Length - 1; i >= 0; i--)
                    SendKeyboard(new[] { Keyboard(modifiers[i], true) });
            }
        }

        internal static void PrefixChord(int prefixKey, int followingKey, params int[] followingModifiers)
        {
            Chord(prefixKey, Vk.LeftControl);
            Thread.Sleep(35);
            Chord(followingKey, followingModifiers);
        }

        internal static void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            var inputs = new List<NativeMethods.Input>(text.Length * 2);
            foreach (char character in text)
            {
                inputs.Add(Unicode(character, false));
                inputs.Add(Unicode(character, true));
            }
            SendKeyboard(inputs.ToArray());
        }

        internal static void VerticalWheel(int delta)
        {
            SendMouse(delta, NativeMethods.MouseeventfWheel);
        }

        internal static void HorizontalWheel(int delta)
        {
            SendMouse(delta, NativeMethods.MouseeventfHwheel);
        }

        private static void SendMouse(int delta, uint flags)
        {
            var input = new NativeMethods.Input();
            input.Type = NativeMethods.InputMouse;
            input.Union.Mouse = new NativeMethods.MouseInput
            {
                MouseData = unchecked((uint)delta),
                Flags = flags
            };
            SendKeyboard(new[] { input });
        }

        private static NativeMethods.Input Keyboard(int virtualKeyCode, bool keyUp)
        {
            uint flags = keyUp ? NativeMethods.KeyeventfKeyup : 0;
            if (IsExtendedKey(virtualKeyCode))
                flags |= NativeMethods.KeyeventfExtendedKey;
            var input = new NativeMethods.Input();
            input.Type = NativeMethods.InputKeyboard;
            input.Union.Keyboard = new NativeMethods.KeyboardInput
            {
                VirtualKeyCode = (ushort)virtualKeyCode,
                Flags = flags
            };
            return input;
        }

        private static NativeMethods.Input Unicode(char character, bool keyUp)
        {
            var input = new NativeMethods.Input();
            input.Type = NativeMethods.InputKeyboard;
            input.Union.Keyboard = new NativeMethods.KeyboardInput
            {
                ScanCode = character,
                Flags = NativeMethods.KeyeventfUnicode | (keyUp ? NativeMethods.KeyeventfKeyup : 0)
            };
            return input;
        }

        private static bool IsExtendedKey(int virtualKeyCode)
        {
            switch (virtualKeyCode)
            {
                case Vk.RightControl:
                case Vk.Left:
                case Vk.Right:
                case Vk.Up:
                case Vk.Down:
                case Vk.Home:
                case Vk.End:
                case Vk.PageUp:
                case Vk.PageDown:
                case Vk.Delete:
                    return true;
                default:
                    return false;
            }
        }

        private static void SendKeyboard(NativeMethods.Input[] inputs)
        {
            if (inputs == null || inputs.Length == 0)
                return;
            uint sent = NativeMethods.SendInput(
                (uint)inputs.Length,
                inputs,
                Marshal.SizeOf(typeof(NativeMethods.Input)));
            if (sent != inputs.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput sent fewer events than expected");
        }
    }
}
