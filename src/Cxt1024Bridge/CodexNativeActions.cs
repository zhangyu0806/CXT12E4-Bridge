using System;
using System.Diagnostics;
using System.IO;

namespace Cxt1024Bridge
{
    internal sealed partial class ChatGptAutomation
    {
        internal bool NewTask(Func<bool> contextIsCurrent)
        {
            string executable;
            Func<bool> guard;
            if (!TryBindCodexInput(contextIsCurrent, out executable, out guard))
                return false;
            // Installed 26.924 command: newProjectlessTask -> activeProject:null.
            // Ctrl+N and sidebar geometry are NOT equivalent to this command.
            return DispatchStandaloneNewTask(guard, delegate
            {
                InputSender.Chord(Vk.O, Vk.LeftControl, Vk.LeftAlt);
            }, delegate(string message) { Logger.Info("Standalone new task: " + message); });
        }

        internal static bool DispatchStandaloneNewTask(Func<bool> guard, Action send, Action<string> trace)
        {
            if (guard == null || !guard())
            {
                trace("cancelled: original Codex window context changed");
                return false;
            }
            send();
            trace("Ctrl+Alt+O dispatched (newProjectlessTask); creation is not yet confirmed");
            return true;
        }

        internal bool OpenInBuiltInBrowser(string url)
        {
            return OpenInBuiltInBrowser(url, null);
        }

        internal bool OpenInBuiltInBrowser(string url, Func<bool> contextIsCurrent)
        {
            string executable;
            Func<bool> guard;
            if (!TryBindCodexInput(contextIsCurrent, out executable, out guard))
                return false;
            // Windows packaged Codex accepts a bare HTTP(S) argv entry and
            // forwards it to toggle-browser-panel through second-instance IPC.
            // Dispatch to this exact running executable, not a URL association.
            return DispatchBuiltInBrowserRequest(executable, url, guard,
                delegate(ProcessStartInfo start)
                {
                    using (Process launched = Process.Start(start))
                    {
                        if (launched == null)
                            throw new InvalidOperationException("No Codex launch process returned.");
                    }
                }, delegate(string message) { Logger.Info("Browser native argv: " + message); });
        }

        internal static bool IsPackagedCodexExecutable(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable)) return false;
            try
            {
                string full = Path.GetFullPath(executable);
                string app = Path.GetDirectoryName(full);
                string package = Path.GetDirectoryName(app);
                string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                return (string.Equals(Path.GetFileName(full), "ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileName(full), "Codex.exe", StringComparison.OrdinalIgnoreCase)) &&
                    string.Equals(Path.GetFileName(app), "app", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetDirectoryName(package), windowsApps, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(package).StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(package).EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        private static bool TryBindCodexInput(Func<bool> context, out string executable, out Func<bool> guard)
        {
            executable = null;
            guard = null;
            IntPtr window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero || (context != null && !context())) return false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            try
            {
                using (Process process = Process.GetProcessById(unchecked((int)processId)))
                    executable = process.MainModule.FileName;
                if (!IsPackagedCodexExecutable(executable) || !File.Exists(executable))
                {
                    Logger.Warn("Codex native action cancelled: foreground is not the supported packaged Codex executable.");
                    return false;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (ArgumentException) { return false; }
            guard = delegate
            {
                if ((context != null && !context()) || NativeMethods.GetForegroundWindow() != window)
                    return false;
                uint currentProcess;
                NativeMethods.GetWindowThreadProcessId(window, out currentProcess);
                return currentProcess == processId;
            };
            return guard();
        }

        internal static ProcessStartInfo BuildBuiltInBrowserRequest(string executable, string url)
        {
            if (!IsPackagedCodexExecutable(executable))
                throw new ArgumentException("Expected the running packaged Codex executable.", "executable");
            Uri parsed;
            if (string.IsNullOrWhiteSpace(url) || url != url.Trim() ||
                url.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0 ||
                !Uri.TryCreate(url, UriKind.Absolute, out parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo))
                throw new ArgumentException("Browser URL must be HTTP(S) without credentials or command-line delimiters.", "url");
            return new ProcessStartInfo(executable, "\"" + parsed.AbsoluteUri + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            };
        }

        internal static bool DispatchBuiltInBrowserRequest(string executable, string url, Func<bool> guard,
            Action<ProcessStartInfo> launch, Action<string> trace)
        {
            ProcessStartInfo start = BuildBuiltInBrowserRequest(executable, url);
            if (guard == null || !guard())
            {
                trace("cancelled: original Codex window context changed");
                return false;
            }
            try
            {
                launch(start);
                trace("HTTP URL argument dispatched to foreground Codex executable; page loading is asynchronous, not confirmed");
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                trace("launch failed; Win32 error=" + ex.NativeErrorCode);
                return false;
            }
            catch (InvalidOperationException)
            {
                trace("launch failed; invalid operation");
                return false;
            }
        }
    }
}
