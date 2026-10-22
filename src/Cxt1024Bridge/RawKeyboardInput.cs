using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Cxt1024Bridge
{
    internal sealed class RawInputDiagnosticEvent
    {
        internal string DeviceName;
        internal int VirtualKey;
        internal bool IsBreak;
        internal bool IsEncoder;
        internal string Detail;
        internal uint MessageTime;

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "t={0} vk=0x{1:X2} {2} {3} {4} source={5}",
                MessageTime,
                VirtualKey,
                IsBreak ? "break" : "make",
                IsEncoder ? "encoder" : "button",
                Detail ?? string.Empty,
                DeviceName ?? "<unknown>");
        }
    }

    // Device-specific business input for the CXT 12E4 keyboard interface.
    // The window thread publishes button edges to TriggerProcessor's physical
    // ledger before placing immutable business events on the FIFO.  A delayed
    // consumer therefore cannot hide an already-arrived key-up from a 650 ms
    // timer.
    internal sealed class RawKeyboardInput : IDisposable
    {
        private const int VkControl = 0x11;
        private const int VkRightControl = 0xA3;
        private const int VkF13 = 0x7C;
        private const int VkF24 = 0x87;
        private const int QueueCapacity = 256;

        private enum Lifecycle
        {
            Created,
            Starting,
            Running,
            Stopping,
            Stopped,
            Disposed
        }

        private sealed class QueuedInput
        {
            internal BridgeTrigger Trigger;
            internal long Generation;
            internal bool Cancel;
            internal string Reason;
            internal int Received;
        }

        private sealed class DeviceInputState
        {
            internal string Name;
            internal bool RightControlDown;
            internal readonly PrivateTriggerDecoder Decoder = new PrivateTriggerDecoder();
        }

        private sealed class MessageWindow : NativeWindow
        {
            private readonly RawKeyboardInput _owner;

            internal MessageWindow(RawKeyboardInput owner)
            {
                _owner = owner;
            }

            internal void Create()
            {
                var parameters = new CreateParams();
                parameters.Caption = "CXT12E4 Raw Input";
                parameters.Parent = new IntPtr(-3); // HWND_MESSAGE
                CreateHandle(parameters);
            }

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == NativeMethods.WmInput)
                {
                    uint messageTime = unchecked((uint)NativeMethods.GetMessageTime());
                    _owner.HandleRawInput(message.LParam, messageTime);
                    base.WndProc(ref message);
                    return;
                }
                if (message.Msg == NativeMethods.WmInputDeviceChange)
                {
                    _owner.HandleDeviceSetChanged((uint)message.WParam.ToInt64());
                    base.WndProc(ref message);
                    return;
                }
                if (message.Msg == NativeMethods.WmAppStopRawInput)
                {
                    Application.ExitThread();
                    return;
                }
                base.WndProc(ref message);
            }
        }

        private readonly TriggerProcessor _triggers;
        private readonly Action<RawInputDiagnosticEvent> _diagnostic;
        private readonly Func<IntPtr> _foreground;
        private readonly Func<long> _contextGeneration;
        private readonly object _lifecycleGate = new object();
        private readonly object _queueGate = new object();
        private readonly Queue<QueuedInput> _queue = new Queue<QueuedInput>();
        private readonly AutoResetEvent _queueSignal = new AutoResetEvent(false);
        private readonly ManualResetEvent _windowStarted = new ManualResetEvent(false);
        private readonly Dictionary<IntPtr, DeviceInputState> _selectedInterfaces = new Dictionary<IntPtr, DeviceInputState>();
        private Thread _windowThread;
        private Thread _consumerThread;
        private MessageWindow _window;
        private Exception _startupError;
        private Lifecycle _lifecycle = Lifecycle.Created;
        private bool _accepting;
        private bool _queueCompleted;
        private int _businessInFlight;
        private long _deviceGeneration;
        private string _selectedDeviceName;

        internal RawKeyboardInput(TriggerProcessor triggers, Action<RawInputDiagnosticEvent> diagnostic = null,
            Func<IntPtr> foreground = null, Func<long> contextGeneration = null)
        {
            _triggers = triggers;
            _diagnostic = diagnostic;
            _foreground = foreground ?? NativeMethods.GetForegroundWindow;
            _contextGeneration = contextGeneration ?? (() => 0);
            // Diagnostic-only callers do not dispatch business actions. A
            // business source must use the same generation as TriggerProcessor
            // so an away-and-back switch cannot revive a held gesture.
            if (triggers != null && contextGeneration == null)
                throw new ArgumentNullException("contextGeneration");
        }

        internal string SelectedDeviceName
        {
            get
            {
                lock (_lifecycleGate)
                    return _selectedDeviceName;
            }
        }

        internal bool IsDeviceAvailable
        {
            get
            {
                lock (_lifecycleGate)
                    return _accepting && _selectedInterfaces.Count == 2;
            }
        }

        internal void Start()
        {
            lock (_lifecycleGate)
            {
                if (_lifecycle != Lifecycle.Created)
                    throw new InvalidOperationException("Raw keyboard input has already been started.");
                _lifecycle = Lifecycle.Starting;
            }

            if (_triggers != null)
            {
                _consumerThread = new Thread(ConsumerThreadMain);
                _consumerThread.IsBackground = true;
                _consumerThread.Name = "CXT12E4 raw input consumer";
                _consumerThread.Start();
            }

            _windowThread = new Thread(WindowThreadMain);
            _windowThread.IsBackground = true;
            _windowThread.Name = "CXT12E4 raw input window";
            _windowThread.SetApartmentState(ApartmentState.STA);
            _windowThread.Start();

            if (!_windowStarted.WaitOne(5000))
                throw new TimeoutException("Timed out starting the CXT 12E4 Raw Input window.");
            if (_startupError != null)
                throw new InvalidOperationException("Unable to start CXT 12E4 Raw Input.", _startupError);

            lock (_lifecycleGate)
            {
                if (_lifecycle != Lifecycle.Starting)
                    throw new InvalidOperationException("Raw Input startup was interrupted.");
                _lifecycle = Lifecycle.Running;
                _accepting = _selectedInterfaces.Count == 2;
            }
            Logger.Info(_accepting
                ? "Raw Input business source ready: " + _selectedDeviceName
                : "Raw Input registered, but no unique CXT 12E4 keyboard interface is available; input is fail-closed.");
        }

        private void WindowThreadMain()
        {
            var window = new MessageWindow(this);
            try
            {
                window.Create();
                _window = window;
                Register(window.Handle);
                lock (_lifecycleGate)
                    RefreshSelectedDeviceUnderLifecycle();
                _windowStarted.Set();
                Application.Run();
            }
            catch (Exception ex)
            {
                _startupError = ex;
                _windowStarted.Set();
            }
            finally
            {
                try { Unregister(); }
                catch { }
                if (window.Handle != IntPtr.Zero)
                    window.DestroyHandle();
                _window = null;
            }
        }

        private static void Register(IntPtr target)
        {
            var devices = new[]
            {
                new NativeMethods.RawInputDevice
                {
                    UsagePage = 0x01,
                    Usage = 0x06,
                    Flags = NativeMethods.RidevInputSink | NativeMethods.RidevDevNotify,
                    Target = target
                }
            };
            uint size = unchecked((uint)Marshal.SizeOf(typeof(NativeMethods.RawInputDevice)));
            if (!NativeMethods.RegisterRawInputDevices(devices, 1, size))
                NativeMethods.ThrowLastWin32("RegisterRawInputDevices(CXT12E4)");
        }

        private static void Unregister()
        {
            var devices = new[]
            {
                new NativeMethods.RawInputDevice
                {
                    UsagePage = 0x01,
                    Usage = 0x06,
                    Flags = NativeMethods.RidevRemove,
                    Target = IntPtr.Zero
                }
            };
            uint size = unchecked((uint)Marshal.SizeOf(typeof(NativeMethods.RawInputDevice)));
            if (!NativeMethods.RegisterRawInputDevices(devices, 1, size))
                NativeMethods.ThrowLastWin32("RegisterRawInputDevices(RIDEV_REMOVE)");
        }

        private void ConsumerThreadMain()
        {
            while (true)
            {
                QueuedInput item = null;
                lock (_queueGate)
                {
                    if (_queue.Count > 0)
                        item = _queue.Dequeue();
                    else if (_queueCompleted)
                        return;
                }
                if (item == null)
                {
                    _queueSignal.WaitOne();
                    continue;
                }
                if (item.Cancel)
                {
                    // The WndProc already invalidated the faulted generation's
                    // physical ledger before this item entered the FIFO.  Do
                    // not invalidate again here: a new-generation down may have
                    // arrived while this consumer was catching up.
                    _triggers.CancelBusinessInputs();
                    Logger.Warn("Raw Input generation cancelled: " + item.Reason);
                    continue;
                }

                bool permitted = false;
                lock (_lifecycleGate)
                {
                    if (_lifecycle == Lifecycle.Running && _accepting && item.Generation == _deviceGeneration)
                    {
                        _businessInFlight++;
                        permitted = true;
                    }
                }
                if (!permitted)
                    continue;
                try
                {
                    PrivateInputContext context = CaptureContext();
                    if (HookInputPump.IsExpired(item.Received, Environment.TickCount) ||
                        (item.Trigger.HasReceiptContext &&
                        (item.Trigger.ReceiptForeground != context.Foreground ||
                         item.Trigger.ReceiptContextGeneration != context.Generation)))
                    {
                        _triggers.CancelBusinessInputs();
                        continue;
                    }
                    _triggers.ProcessPublished(item.Trigger);
                }
                catch (Exception ex)
                {
                    Logger.Error("Raw Input business consumer rejected an event", ex);
                    lock (_lifecycleGate)
                    {
                        if (_lifecycle == Lifecycle.Running && item.Generation == _deviceGeneration)
                            FaultGenerationUnderLifecycle("consumer_exception");
                    }
                }
                finally
                {
                    lock (_lifecycleGate)
                    {
                        _businessInFlight--;
                        if (_businessInFlight == 0)
                            Monitor.PulseAll(_lifecycleGate);
                    }
                }
            }
        }

        private void HandleRawInput(IntPtr rawInput, uint messageTime)
        {
            NativeMethods.RawInputHeader header;
            NativeMethods.RawKeyboard keyboard;
            if (!TryReadKeyboard(rawInput, out header, out keyboard))
                return;
            if (header.Type != NativeMethods.RimTypeKeyboard || header.Device == IntPtr.Zero)
                return;

            lock (_lifecycleGate)
            {
                if (_lifecycle != Lifecycle.Running || !_accepting)
                    return;
                DeviceInputState deviceState;
                if (!_selectedInterfaces.TryGetValue(header.Device, out deviceState))
                    return;
                HandleKeyboardUnderLifecycle(keyboard, messageTime, deviceState);
            }
        }

        private void HandleKeyboardUnderLifecycle(NativeMethods.RawKeyboard keyboard, uint messageTime, DeviceInputState deviceState)
        {
            if (keyboard.MakeCode == NativeMethods.KeyboardOverrunMakeCode)
            {
                FaultGenerationUnderLifecycle("keyboard_overrun");
                return;
            }

            int virtualKey = NormalizeVirtualKey(keyboard.VirtualKey, keyboard.Flags);
            bool isBreak = (keyboard.Flags & NativeMethods.RiKeyBreak) != 0;
            if (virtualKey == VkRightControl)
            {
                deviceState.RightControlDown = !isBreak;
                EmitDiagnostic(deviceState.Name, virtualKey, isBreak, false, "right-control", messageTime);
            }
            else if (virtualKey < VkF13 || virtualKey > VkF24)
                return;

            PrivateDecodeResult decoded = deviceState.Decoder.Process(virtualKey, !isBreak,
                deviceState.RightControlDown, messageTime, CaptureContext());
            if (decoded.Fault != null)
            {
                CancelDecoderGenerationUnderLifecycle(decoded.Fault);
                return;
            }
            if (decoded.Triggers.Count == 0 && virtualKey != VkRightControl)
                EmitDiagnostic(deviceState.Name, virtualKey, isBreak, deviceState.RightControlDown,
                    "private-edge pending/repeated/cancelled", messageTime);
            for (int i = 0; i < decoded.Triggers.Count; i++)
            {
                BridgeTrigger trigger = decoded.Triggers[i];
                EmitDiagnostic(deviceState.Name, virtualKey, isBreak, trigger.IsEncoder,
                    trigger.IsEncoder ? "K" + trigger.Number + " " + trigger.EncoderGesture + " completed" :
                    "key " + trigger.Number, messageTime);
                if (!trigger.IsEncoder && _triggers != null)
                    trigger.PhysicalPressToken = _triggers.PublishPhysicalButtonEdge(trigger.Number, trigger.IsDown);
                EnqueueBusinessUnderLifecycle(trigger);
            }
        }

        private PrivateInputContext CaptureContext()
        {
            long before;
            PrivateInputContext context;
            do
            {
                before = _contextGeneration();
                context = new PrivateInputContext { Foreground = _foreground(), Generation = _contextGeneration() };
            }
            while (before != context.Generation);
            return context;
        }

        private void CancelDecoderGenerationUnderLifecycle(string reason)
        {
            _deviceGeneration = unchecked(_deviceGeneration + 1);
            foreach (DeviceInputState state in _selectedInterfaces.Values)
                state.Decoder.CancelUntilReleased();
            if (_triggers != null) _triggers.InvalidatePhysicalLedger();
            lock (_queueGate)
            {
                _queue.Clear();
                if (_triggers != null) _queue.Enqueue(new QueuedInput { Cancel = true, Reason = reason });
            }
            _queueSignal.Set();
        }

        private void EnqueueBusinessUnderLifecycle(BridgeTrigger trigger)
        {
            if (_triggers == null)
                return;
            bool full;
            lock (_queueGate)
            {
                full = _queue.Count >= QueueCapacity;
                if (!full)
                    _queue.Enqueue(new QueuedInput { Trigger = trigger, Generation = _deviceGeneration, Received = Environment.TickCount });
            }
            if (full)
            {
                FaultGenerationUnderLifecycle("raw_input_queue_overflow");
                return;
            }
            _queueSignal.Set();
        }

        private void HandleDeviceSetChanged(uint change)
        {
            lock (_lifecycleGate)
            {
                if (_lifecycle != Lifecycle.Running && _lifecycle != Lifecycle.Starting)
                    return;
                FaultGenerationUnderLifecycle(change == NativeMethods.GidcRemoval ? "device_removed" : "device_arrived");
                RefreshSelectedDeviceUnderLifecycle();
                _accepting = _lifecycle == Lifecycle.Running && _selectedInterfaces.Count == 2;
            }
        }

        private void FaultGenerationUnderLifecycle(string reason)
        {
            _accepting = false;
            _deviceGeneration = unchecked(_deviceGeneration + 1);
            _selectedInterfaces.Clear();
            _selectedDeviceName = null;
            if (_triggers != null)
                _triggers.InvalidatePhysicalLedger();
            lock (_queueGate)
            {
                _queue.Clear();
                if (_triggers != null)
                    _queue.Enqueue(new QueuedInput { Cancel = true, Reason = reason });
            }
            _queueSignal.Set();
        }

        private void RefreshSelectedDeviceUnderLifecycle()
        {
            _deviceGeneration = unchecked(_deviceGeneration + 1);
            List<KeyValuePair<IntPtr, string>> matches = EnumerateTargetKeyboards();
            _selectedInterfaces.Clear();
            Guid containerId;
            if (HasExpectedFirmwareKeyboardTopology(matches) && TryGetSharedContainerId(matches, out containerId))
            {
                for (int i = 0; i < matches.Count; i++)
                    _selectedInterfaces[matches[i].Key] = new DeviceInputState { Name = matches[i].Value };
                _selectedDeviceName = "CXT 12E4 QMK keyboard interface pair (boot + NKRO), container " + containerId.ToString("D");
            }
            else
            {
                _selectedDeviceName = matches.Count == 0 ? null : "device_topology_ambiguous";
            }
        }

        internal static bool HasExpectedFirmwareKeyboardTopology(List<KeyValuePair<IntPtr, string>> matches)
        {
            if (matches == null || matches.Count != 2)
                return false;
            int boot = 0;
            int nkro = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                string upper = (matches[i].Value ?? string.Empty).ToUpperInvariant();
                string[] tokens = upper.Split(new[] { '#', '&' }, StringSplitOptions.RemoveEmptyEntries);
                bool mi00 = false;
                bool mi02 = false;
                bool col04 = false;
                bool anyCollection = false;
                for (int j = 0; j < tokens.Length; j++)
                {
                    mi00 |= tokens[j] == "MI_00";
                    mi02 |= tokens[j] == "MI_02";
                    col04 |= tokens[j] == "COL04";
                    anyCollection |= tokens[j].StartsWith("COL", StringComparison.Ordinal);
                }
                if (mi00 && !mi02 && !anyCollection)
                    boot++;
                else if (mi02 && !mi00 && col04)
                    nkro++;
                else
                    return false;
            }
            return boot == 1 && nkro == 1;
        }

        private static bool TryGetSharedContainerId(List<KeyValuePair<IntPtr, string>> matches, out Guid containerId)
        {
            containerId = Guid.Empty;
            if (matches == null || matches.Count != 2)
                return false;
            Guid first;
            Guid second;
            if (!TryReadContainerId(matches[0].Value, out first) ||
                !TryReadContainerId(matches[1].Value, out second) ||
                first == Guid.Empty || first != second)
                return false;
            containerId = first;
            return true;
        }

        private static bool TryReadContainerId(string deviceInterface, out Guid containerId)
        {
            containerId = Guid.Empty;
            if (string.IsNullOrEmpty(deviceInterface))
                return false;
            var key = new NativeMethods.DevPropKey
            {
                FormatId = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
                PropertyId = 2
            };
            var bytes = new byte[16];
            uint size = unchecked((uint)bytes.Length);
            uint propertyType;
            uint result = NativeMethods.CmGetDeviceInterfaceProperty(
                deviceInterface,
                ref key,
                out propertyType,
                bytes,
                ref size,
                0);
            if (result != NativeMethods.CrSuccess || propertyType != NativeMethods.DevPropTypeGuid || size != 16)
                return false;
            containerId = new Guid(bytes);
            return containerId != Guid.Empty;
        }

        private static List<KeyValuePair<IntPtr, string>> EnumerateTargetKeyboards()
        {
            var result = new List<KeyValuePair<IntPtr, string>>();
            uint count = 0;
            uint entrySize = unchecked((uint)Marshal.SizeOf(typeof(NativeMethods.RawInputDeviceList)));
            uint first = NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize);
            if (first == uint.MaxValue)
                NativeMethods.ThrowLastWin32("GetRawInputDeviceList(size)");
            if (count == 0)
                return result;

            IntPtr buffer = Marshal.AllocHGlobal(checked((int)(count * entrySize)));
            try
            {
                uint actual = NativeMethods.GetRawInputDeviceList(buffer, ref count, entrySize);
                if (actual == uint.MaxValue)
                    NativeMethods.ThrowLastWin32("GetRawInputDeviceList(data)");
                for (uint i = 0; i < actual; i++)
                {
                    IntPtr current = new IntPtr(buffer.ToInt64() + (long)i * entrySize);
                    var item = (NativeMethods.RawInputDeviceList)Marshal.PtrToStructure(current, typeof(NativeMethods.RawInputDeviceList));
                    if (item.Type != NativeMethods.RimTypeKeyboard || item.Device == IntPtr.Zero)
                        continue;
                    string name;
                    if (!TryReadDeviceName(item.Device, out name) || !IsTargetDeviceName(name) || !IsKeyboardDeviceInfo(item.Device))
                        continue;
                    result.Add(new KeyValuePair<IntPtr, string>(item.Device, name));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        internal static List<string> DescribeRawKeyboards()
        {
            var lines = new List<string>();
            uint count = 0;
            uint entrySize = unchecked((uint)Marshal.SizeOf(typeof(NativeMethods.RawInputDeviceList)));
            uint first = NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize);
            if (first == uint.MaxValue)
                NativeMethods.ThrowLastWin32("GetRawInputDeviceList(size)");
            if (count == 0)
                return lines;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)(count * entrySize)));
            try
            {
                uint actual = NativeMethods.GetRawInputDeviceList(buffer, ref count, entrySize);
                if (actual == uint.MaxValue)
                    NativeMethods.ThrowLastWin32("GetRawInputDeviceList(data)");
                for (uint i = 0; i < actual; i++)
                {
                    IntPtr current = new IntPtr(buffer.ToInt64() + (long)i * entrySize);
                    var item = (NativeMethods.RawInputDeviceList)Marshal.PtrToStructure(current, typeof(NativeMethods.RawInputDeviceList));
                    if (item.Type != NativeMethods.RimTypeKeyboard)
                        continue;
                    string name;
                    bool nameOk = TryReadDeviceName(item.Device, out name);
                    bool infoOk = IsKeyboardDeviceInfo(item.Device);
                    Guid containerId = Guid.Empty;
                    bool containerOk = nameOk && TryReadContainerId(name, out containerId);
                    lines.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "hDevice=0x{0:X} nameOk={1} infoKeyboard={2} target={3} container={4} name={5}",
                        item.Device.ToInt64(),
                        nameOk,
                        infoOk,
                        nameOk && IsTargetDeviceName(name),
                        containerOk ? containerId.ToString("D") : "<unavailable>",
                        nameOk ? name : "<unavailable>"));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return lines;
        }

        internal static bool IsTargetDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            string upper = name.ToUpperInvariant();
            if (!upper.StartsWith("\\\\?\\HID#", StringComparison.Ordinal))
                return false;
            string[] tokens = upper.Split(new[] { '#', '&' }, StringSplitOptions.RemoveEmptyEntries);
            bool vendor = false;
            bool product = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                vendor |= tokens[i] == "VID_5754";
                product |= tokens[i] == "PID_C401";
            }
            return vendor && product;
        }

        private static bool TryReadDeviceName(IntPtr device, out string name)
        {
            name = null;
            uint chars = 0;
            uint result = NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RidiDeviceName, (IntPtr)IntPtr.Zero, ref chars);
            if (result == uint.MaxValue || chars < 2 || chars > 32768)
                return false;
            var builder = new StringBuilder(checked((int)chars));
            uint capacity = chars;
            result = NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RidiDeviceName, builder, ref capacity);
            if (result == uint.MaxValue || result == 0)
                return false;
            name = builder.ToString();
            return !string.IsNullOrEmpty(name);
        }

        private static bool IsKeyboardDeviceInfo(IntPtr device)
        {
            const int InfoSize = 32;
            IntPtr buffer = Marshal.AllocHGlobal(InfoSize);
            try
            {
                for (int i = 0; i < InfoSize; i++)
                    Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, InfoSize);
                uint size = InfoSize;
                uint result = NativeMethods.GetRawInputDeviceInfo(device, NativeMethods.RidiDeviceInfo, buffer, ref size);
                return result != uint.MaxValue && size >= 8 && unchecked((uint)Marshal.ReadInt32(buffer, 4)) == NativeMethods.RimTypeKeyboard;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static bool TryReadKeyboard(IntPtr rawInput, out NativeMethods.RawInputHeader header, out NativeMethods.RawKeyboard keyboard)
        {
            header = new NativeMethods.RawInputHeader();
            keyboard = new NativeMethods.RawKeyboard();
            uint size = 0;
            uint headerSize = unchecked((uint)Marshal.SizeOf(typeof(NativeMethods.RawInputHeader)));
            uint result = NativeMethods.GetRawInputData(rawInput, NativeMethods.RidInput, IntPtr.Zero, ref size, headerSize);
            if (result == uint.MaxValue || size < headerSize + 16 || size > 65536)
                return false;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                uint actual = size;
                result = NativeMethods.GetRawInputData(rawInput, NativeMethods.RidInput, buffer, ref actual, headerSize);
                if (result == uint.MaxValue || actual < headerSize + 16)
                    return false;
                header = (NativeMethods.RawInputHeader)Marshal.PtrToStructure(buffer, typeof(NativeMethods.RawInputHeader));
                if (header.Size < headerSize + 16 || header.Size > actual)
                    return false;
                IntPtr keyboardPointer = new IntPtr(buffer.ToInt64() + headerSize);
                keyboard = (NativeMethods.RawKeyboard)Marshal.PtrToStructure(keyboardPointer, typeof(NativeMethods.RawKeyboard));
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        internal static int NormalizeVirtualKey(ushort virtualKey, ushort flags)
        {
            if (virtualKey == VkControl && (flags & NativeMethods.RiKeyE0) != 0)
                return VkRightControl;
            return virtualKey;
        }

        private void EmitDiagnostic(string deviceName, int virtualKey, bool isBreak, bool encoder, string detail, uint messageTime)
        {
            Action<RawInputDiagnosticEvent> callback = _diagnostic;
            if (callback == null)
                return;
            callback(new RawInputDiagnosticEvent
            {
                DeviceName = deviceName,
                VirtualKey = virtualKey,
                IsBreak = isBreak,
                IsEncoder = encoder,
                Detail = detail,
                MessageTime = messageTime
            });
        }

        internal void Stop()
        {
            Thread windowThread;
            Thread consumerThread;
            MessageWindow window;
            lock (_lifecycleGate)
            {
                if (_lifecycle == Lifecycle.Stopped || _lifecycle == Lifecycle.Disposed || _lifecycle == Lifecycle.Created)
                    return;
                if (_lifecycle == Lifecycle.Stopping)
                {
                    while (_lifecycle == Lifecycle.Stopping)
                        Monitor.Wait(_lifecycleGate);
                    return;
                }
                _lifecycle = Lifecycle.Stopping;
                _accepting = false;
                if (_triggers != null)
                    _triggers.InvalidatePhysicalLedger();
                lock (_queueGate)
                {
                    _queue.Clear();
                    _queueCompleted = true;
                }
                _queueSignal.Set();
                while (_businessInFlight > 0)
                    Monitor.Wait(_lifecycleGate);
                windowThread = _windowThread;
                consumerThread = _consumerThread;
                window = _window;
            }

            if (_triggers != null)
                _triggers.BeginStoppingAndCancel();
            if (window != null && window.Handle != IntPtr.Zero)
                NativeMethods.PostMessage(window.Handle, NativeMethods.WmAppStopRawInput, IntPtr.Zero, IntPtr.Zero);
            if (windowThread != null && windowThread != Thread.CurrentThread && !windowThread.Join(5000))
                Logger.Warn("Raw Input window thread did not stop within five seconds.");
            if (consumerThread != null && consumerThread != Thread.CurrentThread && !consumerThread.Join(5000))
                Logger.Warn("Raw Input consumer did not stop within five seconds.");

            lock (_lifecycleGate)
            {
                _lifecycle = Lifecycle.Stopped;
                Monitor.PulseAll(_lifecycleGate);
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_lifecycleGate)
                _lifecycle = Lifecycle.Disposed;
            _queueSignal.Dispose();
            _windowStarted.Dispose();
        }
    }
}
