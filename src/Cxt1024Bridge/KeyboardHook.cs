using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Cxt1024Bridge
{
    internal enum EncoderGesture
    {
        CounterClockwise,
        Press,
        Clockwise
    }

    internal sealed class BridgeTrigger
    {
        internal bool IsEncoder;
        internal int Number;
        internal EncoderGesture EncoderGesture;
        internal bool IsDown;
        internal long Timestamp;
        // Button edges received through Raw Input publish their physical state
        // before business processing.  The immutable token keeps the later
        // consumer edge tied to that exact physical press.
        internal long PhysicalPressToken;
        // Raw private packets are decoded at receipt time by HookInputPump.
        // Zero means this is an already-decoded business trigger.
        internal int PrivateVirtualKey;
        internal bool PrivateRightControlDown;
        internal bool HasReceiptContext;
        internal IntPtr ReceiptForeground;
        internal long ReceiptContextGeneration;
    }

    internal sealed class KeyboardHook : IDisposable
    {
        private const int VkF13 = 0x7C;
        private const int VkF24 = 0x87;
        private readonly NativeMethods.LowLevelKeyboardProc _callback;
        private readonly Action<BridgeTrigger> _sink;
        private IntPtr _hook;
        private Thread _thread;
        private Control _threadControl;
        private readonly ManualResetEvent _started = new ManualResetEvent(false);
        private Exception _startupError;
        private readonly object _gate = new object();
        private bool _disposed;
        private int _renewals;
        internal int RenewalCount { get { return Volatile.Read(ref _renewals); } }

        internal KeyboardHook()
            : this(null)
        {
        }

        internal KeyboardHook(Action<BridgeTrigger> sink)
        {
            _sink = sink;
            _callback = HookCallback;
        }

        internal void Install()
        {
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException("KeyboardHook");
                if (_thread != null)
                    return;
                _thread = new Thread(HookThreadMain);
                _thread.IsBackground = true;
                _thread.Name = "CXT12E4 suppression hook";
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            if (!_started.WaitOne(5000))
                throw new TimeoutException("Timed out starting the CXT 12E4 suppression hook thread.");
            if (_startupError != null)
                throw new InvalidOperationException("Unable to start the CXT 12E4 suppression hook.", _startupError);
        }

        private void HookThreadMain()
        {
            var control = new Control();
            IntPtr module = IntPtr.Zero;
            System.Windows.Forms.Timer renewal = null;
            try
            {
                control.CreateControl();
                lock (_gate)
                    _threadControl = control;
                using (Process process = Process.GetCurrentProcess())
                {
                    if (process.MainModule != null)
                        module = NativeMethods.GetModuleHandle(process.MainModule.ModuleName);
                }
                _hook = NativeMethods.SetWindowsHookEx(
                    NativeMethods.WhKeyboardLl,
                    _callback,
                    module,
                    0);
                if (_hook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install the CXT 12E4 keyboard suppression hook");
                // Windows can silently remove a timed-out hook. Reinstall on
                // its own message thread, installing first to avoid an input gap.
                renewal = new System.Windows.Forms.Timer();
                renewal.Interval = 10000;
                renewal.Tick += delegate
                {
                    IntPtr next = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _callback, module, 0);
                    if (next == IntPtr.Zero)
                    {
                        Logger.Warn("Input hook renewal failed; keeping previous hook and retrying.");
                        return;
                    }
                    IntPtr previous = _hook;
                    _hook = next;
                    NativeMethods.UnhookWindowsHookEx(previous);
                    if (Interlocked.Increment(ref _renewals) == 1)
                        Logger.Info("Input hook renewal verified; automatic renewal interval=10000ms.");
                };
                renewal.Start();
                _started.Set();
                Application.Run();
            }
            catch (Exception ex)
            {
                _startupError = ex;
                _started.Set();
            }
            finally
            {
                if (renewal != null) renewal.Dispose();
                if (_hook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
                lock (_gate)
                    _threadControl = null;
                control.Dispose();
            }
        }

        private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0)
                return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

            try
            {
                var data = (NativeMethods.Kbdllhookstruct)Marshal.PtrToStructure(
                    lParam,
                    typeof(NativeMethods.Kbdllhookstruct));
                if ((data.Flags & NativeMethods.LlkhfInjected) != 0)
                    return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

                int virtualKey = unchecked((int)data.VirtualKeyCode);
                bool privateKey = virtualKey >= VkF13 && virtualKey <= VkF24;
                bool marker = virtualKey == Vk.RightControl ||
                    (virtualKey == Vk.Control && (data.Flags & 1) != 0);
                if (!privateKey && !marker)
                    return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

                int message = wParam.ToInt32();
                bool isDown = message == NativeMethods.WmKeyDown || message == NativeMethods.WmSysKeyDown;
                bool isUp = message == NativeMethods.WmKeyUp || message == NativeMethods.WmSysKeyUp;
                if (!isDown && !isUp)
                    return privateKey ? new IntPtr(1) : NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);

                Action<BridgeTrigger> sink = _sink;
                if (sink != null)
                {
                    sink(new BridgeTrigger
                    {
                        PrivateVirtualKey = marker ? Vk.RightControl : virtualKey,
                        PrivateRightControlDown = marker ? isDown :
                            (NativeMethods.GetAsyncKeyState(Vk.RightControl) & 0x8000) != 0,
                        IsDown = isDown,
                        Timestamp = data.Time
                    });
                }

                // F13-F24 are private bridge triggers and never reach the foreground application.
                // The hook has no device identity: RightCtrl always passes
                // through, including the user's main keyboard RightCtrl.
                return privateKey ? new IntPtr(1) : NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
            }
            catch (Exception ex)
            {
                Logger.Error("Keyboard hook callback failed", ex);
                return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
            }
        }

        // The PCB exposes rotary indexes in physical order K4, K3, K1, K2.
        // Push switches already use K1..K4 order, so normalize rotations only.
        internal static int NormalizeEncoderNumber(int encodedNumber, EncoderGesture gesture)
        {
            if (gesture == EncoderGesture.Press)
                return encodedNumber;
            switch (encodedNumber)
            {
                case 1: return 4;
                case 2: return 3;
                case 3: return 1;
                case 4: return 2;
                default: return encodedNumber;
            }
        }

        public void Dispose()
        {
            Thread thread;
            Control control;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                thread = _thread;
                control = _threadControl;
            }
            if (control != null && control.IsHandleCreated && !control.IsDisposed)
            {
                try
                {
                    control.BeginInvoke((Action)delegate { Application.ExitThread(); });
                }
                catch
                {
                }
            }
            if (thread != null && thread != Thread.CurrentThread && !thread.Join(5000))
                Logger.Warn("Suppression hook thread did not stop within five seconds.");
            _started.Dispose();
        }
    }
}
