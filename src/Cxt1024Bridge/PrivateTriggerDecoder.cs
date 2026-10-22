using System;
using System.Collections.Generic;

namespace Cxt1024Bridge
{
    internal struct PrivateInputContext
    {
        internal IntPtr Foreground;
        internal long Generation;

        internal bool Matches(PrivateInputContext other)
        {
            return Foreground != IntPtr.Zero && Foreground == other.Foreground && Generation == other.Generation;
        }
    }

    internal sealed class PrivateDecodeResult
    {
        internal readonly List<BridgeTrigger> Triggers = new List<BridgeTrigger>();
        internal string Fault;
    }

    // Pure receipt-time decoder, shared by the hook and device-specific Raw Input.
    // It never injects keys and cannot suppress/claim ownership of RightCtrl.
    // An encoder gesture is complete only after BOTH its F-key and marker break.
    internal sealed class PrivateTriggerDecoder
    {
        internal const int FirstPrivateKey = 0x7C;
        internal const int LastPrivateKey = 0x87;
        internal const int RightControl = 0xA3;

        private sealed class Press
        {
            internal bool Encoder;
            internal bool Down;
            internal bool Cancelled;
            internal long Timestamp;
            internal PrivateInputContext Context;
        }

        private readonly Press[] _presses = new Press[12];
        private readonly bool[] _observedDown = new bool[12];
        private bool _rightControlDown;
        private bool _quarantined;

        internal PrivateDecodeResult Process(int virtualKey, bool isDown, bool rightControlDown,
            long timestamp, PrivateInputContext context)
        {
            var result = new PrivateDecodeResult();
            if (virtualKey != RightControl && (virtualKey < FirstPrivateKey || virtualKey > LastPrivateKey))
                return result;

            // Keep key ownership even when a pending action is cancelled; a
            // later break must never manufacture a new press in a new window.
            for (int i = 0; i < _presses.Length; i++)
                if (_presses[i] != null && !_presses[i].Context.Matches(context))
                    _presses[i].Cancelled = true;

            if (virtualKey == RightControl)
                _rightControlDown = isDown;
            else
            {
                _rightControlDown = rightControlDown;
                _observedDown[virtualKey - FirstPrivateKey] = isDown;
            }

            if (_quarantined)
            {
                TryLeaveQuarantine();
                return result;
            }

            if (virtualKey == RightControl)
            {
                if (!isDown) CompleteReleasedEncoders(context, result);
                return result;
            }

            int index = virtualKey - FirstPrivateKey;
            Press existing = _presses[index];
            if (isDown)
            {
                if (existing != null)
                {
                    // Same identity repeats are harmless. A second physical
                    // control reusing a still-owned private code is ambiguous.
                    if (!existing.Down || existing.Encoder != _rightControlDown)
                        Quarantine(result, "overlapping_private_identity");
                    return result;
                }

                // A plain button pressed after an encoder owns the marker is
                // indistinguishable from another rotary packet. Do not guess.
                // Normal buttons already down before the marker retain their
                // own identities and are allowed to release normally.
                for (int i = 0; i < _presses.Length; i++)
                {
                    if (_presses[i] != null && _presses[i].Encoder)
                    {
                        Quarantine(result, "ambiguous_private_overlap");
                        return result;
                    }
                }

                var press = new Press
                {
                    Encoder = _rightControlDown,
                    Down = true,
                    Timestamp = timestamp,
                    Context = context
                };
                _presses[index] = press;
                if (!press.Encoder) result.Triggers.Add(CreateTrigger(index, press, true, timestamp));
                return result;
            }

            if (existing == null) return result; // orphan break is never a tap
            existing.Down = false;
            if (!existing.Encoder)
            {
                // Classification comes from make, never the current modifier.
                result.Triggers.Add(CreateTrigger(index, existing, false, timestamp));
                _presses[index] = null;
            }
            else
                CompleteReleasedEncoders(context, result);
            return result;
        }

        internal void CancelUntilReleased()
        {
            _quarantined = true;
            Array.Clear(_presses, 0, _presses.Length);
            TryLeaveQuarantine();
        }

        private void Quarantine(PrivateDecodeResult result, string reason)
        {
            result.Triggers.Clear();
            result.Fault = reason;
            CancelUntilReleased();
        }

        private void TryLeaveQuarantine()
        {
            if (_rightControlDown) return;
            for (int i = 0; i < _observedDown.Length; i++)
                if (_observedDown[i]) return;
            _quarantined = false;
        }

        private void CompleteReleasedEncoders(PrivateInputContext context, PrivateDecodeResult result)
        {
            if (_rightControlDown) return;
            for (int i = 0; i < _presses.Length; i++)
            {
                Press press = _presses[i];
                if (press == null || !press.Encoder || press.Down) continue;
                _presses[i] = null;
                if (!press.Cancelled && press.Context.Matches(context))
                    result.Triggers.Add(CreateTrigger(i, press, true, press.Timestamp));
            }
        }

        private static BridgeTrigger CreateTrigger(int index, Press press, bool isDown, long timestamp)
        {
            EncoderGesture gesture = (EncoderGesture)(index % 3);
            return new BridgeTrigger
            {
                IsEncoder = press.Encoder,
                Number = press.Encoder ? KeyboardHook.NormalizeEncoderNumber(index / 3 + 1, gesture) : index + 1,
                EncoderGesture = gesture,
                IsDown = isDown,
                Timestamp = timestamp,
                HasReceiptContext = true,
                ReceiptForeground = press.Context.Foreground,
                ReceiptContextGeneration = press.Context.Generation
            };
        }
    }
}
