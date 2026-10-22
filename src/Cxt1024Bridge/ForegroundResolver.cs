using System;
using System.Diagnostics;
using System.Text;

namespace Cxt1024Bridge
{
    internal sealed class ForegroundInfo
    {
        internal IntPtr Window;
        internal int ProcessId;
        internal string ProcessName;
        internal string ExecutableName;
        internal string Title;
        internal int AutomaticLayer;

        public override string ToString()
        {
            return string.Format(
                "HWND=0x{0:X} PID={1} Process={2} Title={3}",
                Window.ToInt64(),
                ProcessId,
                ExecutableName,
                Title);
        }
    }

    internal static class ForegroundResolver
    {
        internal static ForegroundInfo Capture()
        {
            return Capture(NativeMethods.GetForegroundWindow());
        }

        internal static ForegroundInfo Capture(IntPtr window)
        {
            var result = new ForegroundInfo
            {
                Window = window,
                ProcessName = string.Empty,
                ExecutableName = string.Empty,
                Title = GetTitle(window),
                AutomaticLayer = 0
            };

            if (window == IntPtr.Zero)
                return result;

            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            result.ProcessId = unchecked((int)processId);
            try
            {
                using (Process process = Process.GetProcessById(result.ProcessId))
                {
                    result.ProcessName = process.ProcessName ?? string.Empty;
                    result.ExecutableName = result.ProcessName + ".exe";
                }
            }
            catch
            {
            }
            if (string.Equals(result.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                string hostedProcess = FindHostedProcessName(window, result.ProcessId);
                if (!string.IsNullOrEmpty(hostedProcess))
                {
                    result.ProcessName = hostedProcess;
                    result.ExecutableName = hostedProcess + ".exe";
                }
            }
            result.AutomaticLayer = ResolveLayer(result.ProcessName, result.Title);
            return result;
        }

        internal static int ResolveLayer(string processName, string title)
        {
            string process = (processName ?? string.Empty).Trim().ToLowerInvariant();
            string windowTitle = (title ?? string.Empty).Trim().ToLowerInvariant();
            if (Matches(process, "chatgpt", "openai.codex"))
                return 1;
            if (Matches(process, "windowsterminal", "wt"))
                return 2;
            if (Matches(process, "notion"))
                return 3;
            if (Matches(process, "obsidian"))
                return 4;
            if (IsBlenderProcess(process))
                return 5;
            if (Matches(process, "weixin", "wechat", "wechatappex", "telegram", "telegramdesktop", "qq", "qqnt"))
                return 7;
            if (Matches(
                process,
                "msedge",
                "chrome",
                "vivaldi",
                "brave",
                "arc",
                "browser pro+",
                "browserpro",
                "o-browser",
                "obrowser",
                "flashbrowser",
                "phoenixsearch"))
                return 0;
            if (process == "applicationframehost" &&
                (windowTitle.Contains("flash browser") || windowTitle.Contains("browser pro")))
                return 0;
            if (Matches(process, "totalcmd64", "totalcmd"))
                return 6;
            return 0;
        }

        internal static bool IsBlenderProcess(string processName)
        {
            // Deliberately exclude launchers, crash handlers and window-title matches.
            return string.Equals((processName ?? string.Empty).Trim(), "blender", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Matches(string value, params string[] candidates)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                if (string.Equals(value, candidates[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string GetTitle(IntPtr window)
        {
            if (window == IntPtr.Zero)
                return string.Empty;
            var text = new StringBuilder(1024);
            NativeMethods.GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        private static string FindHostedProcessName(IntPtr parent, int hostProcessId)
        {
            string result = null;
            NativeMethods.EnumWindowsProc callback = delegate(IntPtr child, IntPtr parameter)
            {
                uint processId;
                NativeMethods.GetWindowThreadProcessId(child, out processId);
                if (processId == 0 || processId == hostProcessId)
                    return true;
                try
                {
                    using (Process process = Process.GetProcessById(unchecked((int)processId)))
                    {
                        string name = process.ProcessName;
                        if (!string.IsNullOrEmpty(name) &&
                            !string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                        {
                            result = name;
                            return false;
                        }
                    }
                }
                catch
                {
                }
                return true;
            };
            NativeMethods.EnumChildWindows(parent, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return result;
        }
    }
}
