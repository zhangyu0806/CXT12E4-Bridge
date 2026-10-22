using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Cxt1024Bridge
{
    internal static class BridgeWatchdog
    {
        private const string WatchdogMutexName = "Local\\Cxt1024Bridge-Watchdog-5754-C401";
        private const string WatchdogExitEventName = "Local\\Cxt1024Bridge-Watchdog-Exit";
        private const int WatchdogIntervalMilliseconds = 30000;

        internal static int Run(string[] args, bool once)
        {
            string bridgeExecutable = args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
                ? Path.GetFullPath(args[1])
                : Path.GetFullPath(Application.ExecutablePath);
            if (!File.Exists(bridgeExecutable))
                throw new FileNotFoundException("Bridge executable is missing.", bridgeExecutable);

            if (once)
            {
                EnsureBridgeRunning(bridgeExecutable);
                return 0;
            }

            bool created;
            using (var mutex = new Mutex(true, WatchdogMutexName, out created))
            using (var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WatchdogExitEventName))
            {
                if (!created)
                {
                    Logger.Warn("A second bridge watchdog instance was rejected.");
                    return 4;
                }

                Logger.Info("Bridge watchdog started. target=" + bridgeExecutable);
                do
                {
                    try
                    {
                        EnsureBridgeRunning(bridgeExecutable);
                    }
                    catch (Exception ex)
                    {
                        // A transient Dropbox/file-system or process-start failure must
                        // not kill the watchdog. Keep the supervisor alive and retry on
                        // the next interval so the Bridge can recover automatically.
                        Logger.Error("Bridge watchdog health check failed; will retry.", ex);
                    }
                } while (!exitEvent.WaitOne(WatchdogIntervalMilliseconds));
                Logger.Info("Bridge watchdog stopped.");
            }

            return 0;
        }

        internal static int ExitRunningWatchdog()
        {
            try
            {
                using (EventWaitHandle exitEvent = EventWaitHandle.OpenExisting(WatchdogExitEventName))
                    exitEvent.Set();
                Console.WriteLine("Exit signal sent to the running bridge watchdog.");
                return 0;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Console.WriteLine("No running bridge watchdog instance was found.");
                return 3;
            }
        }

        internal static bool StopForUserExit()
        {
            Mutex watchdog;
            try
            {
                watchdog = Mutex.OpenExisting(WatchdogMutexName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return true;
            }

            using (watchdog)
            {
                // Keep the resident alive until the supervisor has actually
                // stopped. A signal alone can race an in-flight health check
                // and allow it to restart the resident during teardown.
                ExitRunningWatchdog();
                bool acquired = false;
                try
                {
                    try { acquired = watchdog.WaitOne(3000); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                        Logger.Warn("Explicit Bridge exit cancelled because the watchdog did not stop within 3000ms.");
                    return acquired;
                }
                finally
                {
                    if (acquired)
                        watchdog.ReleaseMutex();
                }
            }
        }

        private static void EnsureBridgeRunning(string bridgeExecutable)
        {
            if (IsResidentBridgeRunning(bridgeExecutable))
                return;

            var startInfo = new ProcessStartInfo(bridgeExecutable, "--run");
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            string directory = Path.GetDirectoryName(bridgeExecutable);
            if (!string.IsNullOrEmpty(directory))
                startInfo.WorkingDirectory = directory;

            Process process = Process.Start(startInfo);
            string pid = process == null ? "unknown" : process.Id.ToString(CultureInfo.InvariantCulture);
            if (process != null)
                process.Dispose();
            Logger.Info("Bridge watchdog started resident Bridge. pid=" + pid + " target=" + bridgeExecutable);
        }

        private static bool IsResidentBridgeRunning(string bridgeExecutable)
        {
            string expected = Path.GetFullPath(bridgeExecutable);
            int currentProcessId = Process.GetCurrentProcess().Id;
            string[] processNames = { "Cxt12E4Bridge", "Cxt1024Bridge" };
            foreach (string processName in processNames)
            {
                Process[] processes = Process.GetProcessesByName(processName);
                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.Id == currentProcessId)
                            continue;
                        string actual = process.MainModule == null ? null : process.MainModule.FileName;
                        if (!string.IsNullOrWhiteSpace(actual) &&
                            string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            return false;
        }
    }
}
