using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Cxt1024Bridge
{
    internal enum AiTaskState
    {
        Idle,
        Thinking,
        Completed,
        WaitingForInput,
        Error
    }

    internal sealed class LightingController : IDisposable
    {
        internal const uint IdleLightsOffMilliseconds = 40u * 60u * 1000u;
        private readonly object _gate = new object();
        private readonly AutoResetEvent _changed = new AutoResetEvent(false);
        private readonly Thread _worker;
        private int _layer;
        private bool _stopping;
        private readonly AiTaskState[] _taskStates = new AiTaskState[6];
        private readonly bool[] _taskPresent = new bool[6];
        private readonly string[] _taskIds = new string[6];
        private readonly AiTaskState[] _terminalStates = new AiTaskState[6];
        private readonly bool[] _terminalPresent = new bool[6];
        private bool _factoryFirmwareLogged;
        private bool _customFirmwareLogged;
        private bool _idleLightsOff;
        private readonly Timer _healthTimer;
        private volatile string _phase = "waiting";
        private int _phaseAt;
        private int _slowReported;

        internal LightingController()
        {
            _healthTimer = new Timer(delegate
            {
                string phase = _phase;
                if (phase != "waiting" && unchecked((uint)(Environment.TickCount - Volatile.Read(ref _phaseAt))) >= 2500u &&
                    Interlocked.Exchange(ref _slowReported, 1) == 0)
                    Logger.Warn("Lighting worker delayed >2500ms; phase=" + phase);
            }, null, 1000, 1000);
            _worker = new Thread(WorkerLoop);
            _worker.Name = "CXT 12E4 RGB controller";
            _worker.IsBackground = true;
            _worker.Start();
        }

        internal void SetLayer(int layer)
        {
            lock (_gate)
            {
                _layer = layer;
            }
            _changed.Set();
        }

        internal void SetTaskStates(IList<ChatGptTaskStatusSlot> slots)
        {
            lock (_gate)
            {
                for (int i = 0; i < _taskStates.Length; i++)
                {
                    _taskStates[i] = AiTaskState.Idle;
                    _taskPresent[i] = false;
                    _taskIds[i] = string.Empty;
                }
                if (slots != null)
                {
                    for (int i = 0; i < slots.Count; i++)
                    {
                        int index = slots[i].Slot - 1;
                        if (index >= 0 && index < _taskStates.Length)
                        {
                            _taskStates[index] = slots[i].State;
                            _taskPresent[index] = true;
                            _taskIds[index] = slots[i].ThreadId;
                        }
                    }
                }
            }
            _changed.Set();
        }

        internal void SetTerminalStates(IList<TerminalSessionStatusSlot> slots)
        {
            lock (_gate)
            {
                for (int i = 0; i < _terminalStates.Length; i++)
                {
                    _terminalStates[i] = AiTaskState.Idle;
                    _terminalPresent[i] = false;
                }
                if (slots != null)
                {
                    for (int i = 0; i < slots.Count; i++)
                    {
                        int index = slots[i].Slot - 1;
                        if (index >= 0 && index < _terminalStates.Length)
                        {
                            _terminalStates[index] = slots[i].State;
                            _terminalPresent[index] = slots[i].Present;
                        }
                    }
                }
            }
            _changed.Set();
        }

        private void WorkerLoop()
        {
            HidDeviceInfo cachedDevice = null;
            long lastSuccess = 0;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                _changed.WaitOne(500);
                if (_stopping)
                    break;

                int layer;
                var taskStates = new AiTaskState[6];
                var taskPresent = new bool[6];
                var taskIds = new string[6];
                var terminalStates = new AiTaskState[6];
                var terminalPresent = new bool[6];
                lock (_gate)
                {
                    layer = _layer;
                    Array.Copy(_taskStates, taskStates, taskStates.Length);
                    Array.Copy(_taskPresent, taskPresent, taskPresent.Length);
                    Array.Copy(_taskIds, taskIds, taskIds.Length);
                    Array.Copy(_terminalStates, terminalStates, terminalStates.Length);
                    Array.Copy(_terminalPresent, terminalPresent, terminalPresent.Length);
                }

                try
                {
                    // Preserve the existing Passport mirror, off the HID path.
                    PassportStatusExport.Write(taskStates, taskPresent, terminalStates, terminalPresent, taskIds);
                    if (ShouldSuspendLightingForIdle())
                    {
                        lastSuccess = 0;
                        if (!_idleLightsOff)
                        {
                            Logger.Info("Windows input has been idle for 40 minutes; pausing the lighting heartbeat so all CXT 12E4 LEDs turn off.");
                            _idleLightsOff = true;
                        }
                        continue;
                    }
                    if (_idleLightsOff)
                    {
                        Logger.Info("Windows input resumed; restoring live CXT 12E4 task and tmux lighting.");
                        _idleLightsOff = false;
                    }

                    long cycleStarted = clock.ElapsedMilliseconds;
                    Volatile.Write(ref _phaseAt, Environment.TickCount);
                    _phase = cachedDevice == null ? "device-discovery" : "hid-exchange";
                    HidDeviceInfo device = cachedDevice ?? HidEnumerator.FindCxt1024Raw();
                    if (device == null)
                        throw new InvalidOperationException("CXT 12E4 raw HID interface is unavailable.");
                    if (!Cxt12E4LightingProtocol.IsCustomFirmware(device))
                    {
                        if (!_factoryFirmwareLogged)
                        {
                            Logger.Info("CXT 12E4 factory firmware detected; all host RGB writes remain disabled.");
                            _factoryFirmwareLogged = true;
                        }
                        continue;
                    }
                    _phase = "hid-exchange";
                    using (var lighting = new Cxt12E4LightingProtocol(device))
                        lighting.SetState(
                            layer,
                            taskStates,
                            taskPresent,
                            terminalStates,
                            terminalPresent,
                            false);
                    cachedDevice = device; // Avoid SetupAPI enumeration on every heartbeat.
                    long now = clock.ElapsedMilliseconds;
                    if (lastSuccess != 0 && now - lastSuccess >= 2500)
                        Logger.Warn("Lighting heartbeat recovered after gap_ms=" + (now - lastSuccess));
                    if (now - cycleStarted >= 1000)
                        Logger.Warn("Slow lighting HID cycle_ms=" + (now - cycleStarted));
                    lastSuccess = now;
                    if (!_customFirmwareLogged)
                    {
                        Logger.Info("CXT 12E4 per-key task-light firmware detected; ChatGPT and terminal RGB banks active.");
                        _customFirmwareLogged = true;
                    }
                }
                catch (Exception ex)
                {
                    cachedDevice = null; // Re-enumerate after unplug / reconnect / failed exchange.
                    Logger.Error("Unable to update CXT 12E4 per-key lighting", ex);
                }
                finally { _phase = "waiting"; Interlocked.Exchange(ref _slowReported, 0); }
            }
        }

        private static bool ShouldSuspendLightingForIdle()
        {
            var info = new NativeMethods.LastInputInfo();
            info.Size = (uint)Marshal.SizeOf(typeof(NativeMethods.LastInputInfo));
            if (!NativeMethods.GetLastInputInfo(ref info))
                return false;

            uint currentTick = unchecked((uint)Environment.TickCount);
            return HasReachedIdleLightsOff(currentTick, info.Time);
        }

        internal static bool HasReachedIdleLightsOff(uint currentTick, uint lastInputTick)
        {
            uint elapsed = unchecked(currentTick - lastInputTick);
            // Unsigned subtraction handles the normal 49.7-day tick wrap. A
            // result in the upper half of the range instead means Windows
            // reported a last-input tick slightly ahead of the current tick;
            // fail open and keep the lights active in that anomalous case.
            return elapsed < 0x80000000u && elapsed >= IdleLightsOffMilliseconds;
        }

        public void Dispose()
        {
            _stopping = true;
            _healthTimer.Dispose();
            _changed.Set();
            if (!_worker.Join(1500))
            {
                // The HID exchange may still be blocked.  Do not dispose the
                // wait handle underneath the live worker: after the exchange
                // returns it loops through WaitOne once more before observing
                // the stopping flag.
                Logger.Warn("RGB controller did not stop within 1500ms; leaving its wait handle alive until process exit.");
                return;
            }
            _changed.Dispose();
        }
    }
}
