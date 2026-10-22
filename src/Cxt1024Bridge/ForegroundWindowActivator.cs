using System;
using System.Threading;

namespace Cxt1024Bridge
{
    internal static class ForegroundWindowActivator
    {
        internal static bool Activate(IntPtr window)
        {
            if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
                return false;

            NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
            if (TrySetAndVerify(window))
                return true;

            uint currentThread = NativeMethods.GetCurrentThreadId();
            IntPtr previousForeground = NativeMethods.GetForegroundWindow();
            uint ignoredProcess;
            uint foregroundThread = previousForeground == IntPtr.Zero
                ? 0
                : NativeMethods.GetWindowThreadProcessId(previousForeground, out ignoredProcess);
            uint targetThread = NativeMethods.GetWindowThreadProcessId(window, out ignoredProcess);
            bool attachedForeground = false;
            bool attachedTarget = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != currentThread)
                    attachedForeground = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
                if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
                    attachedTarget = NativeMethods.AttachThreadInput(currentThread, targetThread, true);

                NativeMethods.BringWindowToTop(window);
                NativeMethods.SetForegroundWindow(window);
                NativeMethods.SetFocus(window);
            }
            finally
            {
                if (attachedTarget)
                    NativeMethods.AttachThreadInput(currentThread, targetThread, false);
                if (attachedForeground)
                    NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }

            Thread.Sleep(25);
            if (NativeMethods.GetForegroundWindow() == window)
                return true;

            // Windows' foreground lock commonly permits activation immediately
            // after an Alt input transition. The injected key is private to this
            // activation attempt; the bridge hook ignores injected events.
            InputSender.Tap(Vk.LeftAlt);
            Thread.Sleep(25);
            NativeMethods.BringWindowToTop(window);
            return TrySetAndVerify(window);
        }

        private static bool TrySetAndVerify(IntPtr window)
        {
            NativeMethods.SetForegroundWindow(window);
            Thread.Sleep(20);
            return NativeMethods.GetForegroundWindow() == window;
        }
    }
}
