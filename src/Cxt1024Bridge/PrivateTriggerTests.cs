using System;
using System.Collections.Generic;
using System.Threading;

namespace Cxt1024Bridge
{
    internal static class PrivateTriggerTests
    {
        internal static void Run()
        {
            foreach (int held in new[] { 100, 250, 700 })
            {
                foreach (int key in new[] { 0x7D, 0x80, 0x83, 0x86 })
                {
                    CheckPressRelease(key, held, false);
                    CheckPressRelease(key, held, true);
                }
            }
            CheckRotation();
            CheckFrozenButtonRelease();
            CheckOverlapsFailClosed();
            CheckButtonTypematicDuringMarker();
            CheckContextCancellation();
            CheckMarkerAloneAndOrphans();
            CheckReceiptPump();
        }

        private static PrivateInputContext Context(int window, long generation)
        {
            return new PrivateInputContext { Foreground = new IntPtr(window), Generation = generation };
        }

        private static PrivateDecodeResult Edge(PrivateTriggerDecoder decoder, int key, bool down, bool control,
            int timestamp, PrivateInputContext context)
        {
            return decoder.Process(key, down, control, timestamp, context);
        }

        private static void CheckPressRelease(int key, int held, bool markerFirst)
        {
            var decoder = new PrivateTriggerDecoder();
            PrivateInputContext context = Context(1, 5);
            Check(Edge(decoder, 0xA3, true, true, 0, context).Triggers.Count == 0, "marker make is not an action");
            Check(Edge(decoder, key, true, true, 1, context).Triggers.Count == 0, "press does not dispatch at make");
            Check(Edge(decoder, key, true, true, 20, context).Triggers.Count == 0, "repeat make is deduplicated");
            PrivateDecodeResult first = markerFirst
                ? Edge(decoder, 0xA3, false, false, held, context)
                : Edge(decoder, key, false, true, held, context);
            Check(first.Triggers.Count == 0, "both releases are required");
            PrivateDecodeResult done = markerFirst
                ? Edge(decoder, key, false, false, held + 1, context)
                : Edge(decoder, 0xA3, false, false, held + 1, context);
            Check(done.Triggers.Count == 1, "exactly one action after " + held + " ms hold");
            BridgeTrigger trigger = done.Triggers[0];
            Check(trigger.IsEncoder && trigger.Number == (key - 0x7C) / 3 + 1 &&
                trigger.EncoderGesture == EncoderGesture.Press && trigger.IsDown, "physical K1-K4 press order preserved");
            Check(trigger.HasReceiptContext && trigger.ReceiptForeground == context.Foreground &&
                trigger.ReceiptContextGeneration == context.Generation, "make context preserved");
            Check(Edge(decoder, key, false, false, held + 2, context).Triggers.Count == 0, "duplicate break is not a tap");
            Check(Edge(decoder, 0xA3, false, false, held + 3, context).Triggers.Count == 0, "duplicate marker break is not a tap");
        }

        private static void CheckRotation()
        {
            var decoder = new PrivateTriggerDecoder();
            PrivateInputContext context = Context(1, 1);
            Edge(decoder, 0xA3, true, true, 0, context);
            Check(Edge(decoder, 0x7C, true, true, 1, context).Triggers.Count == 0, "rotation waits at make");
            Check(Edge(decoder, 0x7C, false, true, 2, context).Triggers.Count == 0, "rotation waits for marker break");
            PrivateDecodeResult done = Edge(decoder, 0xA3, false, false, 3, context);
            Check(done.Triggers.Count == 1 && done.Triggers[0].Number == 4 &&
                done.Triggers[0].EncoderGesture == EncoderGesture.CounterClockwise, "rotation retains PCB normalization");
        }

        private static void CheckFrozenButtonRelease()
        {
            var decoder = new PrivateTriggerDecoder();
            PrivateInputContext context = Context(1, 1);
            PrivateDecodeResult down = Edge(decoder, 0x7C, true, false, 0, context);
            Check(down.Triggers.Count == 1 && !down.Triggers[0].IsEncoder && down.Triggers[0].IsDown, "normal key make");
            Check(Edge(decoder, 0x7C, true, false, 1, context).Triggers.Count == 0, "normal key repeat is deduplicated");
            Edge(decoder, 0xA3, true, true, 1, context);
            Edge(decoder, 0x7D, true, true, 2, context);
            PrivateDecodeResult up = Edge(decoder, 0x7C, false, true, 3, context);
            Check(up.Triggers.Count == 1 && !up.Triggers[0].IsEncoder && !up.Triggers[0].IsDown &&
                up.Triggers[0].Number == 1, "normal key up survives intervening encoder marker");
            Edge(decoder, 0x7D, false, true, 4, context);
            Check(Edge(decoder, 0xA3, false, false, 5, context).Triggers.Count == 1, "distinct encoder completes after normal key release");
        }

        private static void CheckOverlapsFailClosed()
        {
            PrivateInputContext context = Context(1, 1);
            var decoder = new PrivateTriggerDecoder();
            Edge(decoder, 0xA3, true, true, 0, context);
            Edge(decoder, 0x7D, true, true, 1, context);
            PrivateDecodeResult conflict = Edge(decoder, 0x7C, true, true, 2, context);
            Check(conflict.Fault != null && conflict.Triggers.Count == 0, "knob-first ordinary key ambiguity fails closed");
            Check(Edge(decoder, 0x7D, false, true, 3, context).Triggers.Count == 0, "quarantined encoder release cannot execute");
            Check(Edge(decoder, 0xA3, false, false, 4, context).Triggers.Count == 0, "marker release cannot revive overlap");
            Check(Edge(decoder, 0x7C, true, false, 5, context).Triggers.Count == 0, "held conflicting code cannot rebind after Ctrl release");
            Edge(decoder, 0x7C, false, false, 6, context);
            Check(Edge(decoder, 0x7C, true, false, 7, context).Triggers.Count == 1, "new press works after full release");

            decoder = new PrivateTriggerDecoder();
            Edge(decoder, 0x7C, true, false, 0, context);
            Edge(decoder, 0xA3, true, true, 1, context);
            Check(Edge(decoder, 0x7C, true, true, 2, context).Fault != null, "same private code with different identity is rejected");
            Edge(decoder, 0x7C, false, true, 3, context);
            Check(Edge(decoder, 0xA3, false, false, 4, context).Triggers.Count == 0, "same-code conflict cannot execute on release");
        }

        private static void CheckContextCancellation()
        {
            foreach (PrivateInputContext changed in new[] { Context(2, 1), Context(1, 2), Context(0, 1) })
            {
                var decoder = new PrivateTriggerDecoder();
                Edge(decoder, 0xA3, true, true, 0, Context(1, 1));
                Edge(decoder, 0x7D, true, true, 1, Context(1, 1));
                Edge(decoder, 0x7D, false, true, 700, changed);
                Check(Edge(decoder, 0xA3, false, false, 701, changed).Triggers.Count == 0,
                    "held action cancels after window or generation change");
            }
            var returned = new PrivateTriggerDecoder();
            Edge(returned, 0xA3, true, true, 0, Context(1, 1));
            Edge(returned, 0x7D, true, true, 1, Context(1, 1));
            Edge(returned, 0x7D, true, true, 50, Context(2, 2));
            Edge(returned, 0x7D, false, true, 700, Context(1, 3));
            Check(Edge(returned, 0xA3, false, false, 701, Context(1, 3)).Triggers.Count == 0,
                "returning to original HWND does not rebind a held action");
        }

        private static void CheckButtonTypematicDuringMarker()
        {
            // The ordinary button's typematic make after RightCtrl arrives is
            // indistinguishable from a second physical control using its code.
            // Preserve the deliberate fail-closed policy and test the complete
            // receipt/physical-ledger/business chain, not just decoder output.
            int taps = 0, holds = 0, encoders = 0;
            using (var action = new ManualResetEvent(false))
            using (var processor = new TriggerProcessor(() => 7, () => new IntPtr(1), () => 1,
                delegate { Interlocked.Increment(ref taps); action.Set(); },
                delegate { Interlocked.Increment(ref encoders); action.Set(); },
                delegate { Interlocked.Increment(ref holds); action.Set(); },
                delegate { Interlocked.Increment(ref holds); action.Set(); }, delegate { }))
            using (var pump = new HookInputPump(processor, () => new IntPtr(1), () => 1))
            {
                pump.Receive(Packet(0x7C, true, false, 0));
                pump.Receive(Packet(0xA3, true, true, 10));
                pump.Receive(Packet(0x7C, true, true, 20)); // typematic: identity now ambiguous
                Check(!action.WaitOne(700), "typematic/marker conflict cancels the 650 ms hold and phantom encoder");
                pump.Receive(Packet(0xA3, false, false, 720));
                pump.Receive(Packet(0x7C, true, false, 730)); // still physically held: cannot rebind
                pump.Receive(Packet(0x7C, false, false, 740));
                Check(!action.WaitOne(40), "full release of quarantined typematic press produces no tap");
                pump.Receive(Packet(0x7C, true, false, 800));
                pump.Receive(Packet(0x7C, false, false, 850));
                Check(action.WaitOne(2000) && Volatile.Read(ref taps) == 1 &&
                    Volatile.Read(ref holds) == 0 && Volatile.Read(ref encoders) == 0,
                    "fresh ordinary press recovers after all conflicting keys release");
            }
        }

        private static void CheckMarkerAloneAndOrphans()
        {
            var decoder = new PrivateTriggerDecoder();
            PrivateInputContext context = Context(1, 1);
            Check(Edge(decoder, 0xA3, true, true, 0, context).Triggers.Count == 0, "user RightCtrl alone emits no action");
            Check(Edge(decoder, 0xA3, false, false, 1, context).Triggers.Count == 0, "user RightCtrl release emits no action");
            Check(Edge(decoder, 0x7D, false, false, 2, context).Triggers.Count == 0, "orphan F break emits no action");
        }

        private static void CheckReceiptPump()
        {
            int calls = 0;
            var action = new ManualResetEvent(false);
            using (var processor = new TriggerProcessor(() => 7, () => new IntPtr(1), () => 1,
                delegate { }, delegate { Interlocked.Increment(ref calls); action.Set(); },
                delegate { }, delegate { }, delegate { }))
            using (var pump = new HookInputPump(processor, () => new IntPtr(1), () => 1))
            {
                pump.Receive(Packet(0xA3, true, true, 0));
                pump.Receive(Packet(0x7D, true, true, 1));
                Check(!action.WaitOne(700), "pump does not dispatch a held encoder");
                pump.Receive(Packet(0x7D, false, true, 700));
                Check(!action.WaitOne(30), "pump does not dispatch before marker release");
                pump.Receive(Packet(0xA3, false, false, 701));
                Check(action.WaitOne(2000) && Volatile.Read(ref calls) == 1,
                    "700 ms physical hold is not a stale queue item");
            }
            action.Dispose();
        }

        private static BridgeTrigger Packet(int key, bool down, bool control, long timestamp)
        {
            return new BridgeTrigger { PrivateVirtualKey = key, PrivateRightControlDown = control,
                IsDown = down, Timestamp = timestamp };
        }

        private static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Private trigger lifecycle: " + message);
        }
    }
}
