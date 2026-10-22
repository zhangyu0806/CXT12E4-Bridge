using System;
using System.Globalization;
using System.Threading;

namespace Cxt1024Bridge
{
    /// <summary>
    /// Opens or switches to a pinned taskbar application by its one-based slot.
    /// Windows provides direct Win+number shortcuts only for slots 1 through 10;
    /// slot 10 is Win+0. Slot 11 therefore uses taskbar keyboard navigation.
    /// </summary>
    internal static class TaskbarLauncher
    {
        internal const int MaximumSlot = 11;

        internal static void LaunchSlot(int slot)
        {
            if (slot < 1 || slot > MaximumSlot)
                throw new ArgumentOutOfRangeException("slot", "Taskbar slot must be between 1 and 11.");

            int shortcutKey = ShortcutVirtualKey(slot);
            if (shortcutKey != 0)
            {
                InputSender.Chord(shortcutKey, Vk.LeftWindows);
                return;
            }

            Logger.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Opening taskbar slot {0} with Win+T keyboard navigation.",
                slot));

            // Win+T places keyboard focus on the taskbar application buttons.
            // Home then makes the selection deterministic by returning to the
            // first application slot before moving right to the requested one.
            InputSender.Chord(Vk.T, Vk.LeftWindows);
            Thread.Sleep(120);
            InputSender.Tap(Vk.Home);
            Thread.Sleep(40);

            int moves = NavigationRightMoves(slot);
            for (int i = 0; i < moves; i++)
            {
                InputSender.Tap(Vk.Right);
                Thread.Sleep(12);
            }

            Thread.Sleep(30);
            InputSender.Tap(Vk.Enter);
        }

        /// <summary>
        /// Returns the native Win+number virtual key for slots 1 through 10.
        /// Zero means that Windows has no native numbered shortcut for the slot.
        /// </summary>
        internal static int ShortcutVirtualKey(int slot)
        {
            if (slot >= 1 && slot <= 9)
                return Vk.Zero + slot;
            if (slot == 10)
                return Vk.Zero;
            return 0;
        }

        internal static int NavigationRightMoves(int slot)
        {
            return slot >= 1 && slot <= MaximumSlot ? slot - 1 : -1;
        }
    }
}
