using System;
using System.Threading;

namespace Cxt1024Bridge
{
    internal static class AuditLayerTests
    {
        private static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Layer audit regression: " + message);
        }

        internal static void Run()
        {
            var manager = new LayerManager();
            manager.ObserveForeground(Window("Build 10%"));
            long generation = manager.ContextGeneration;
            long content = manager.ContentGeneration;
            int holds = 0;
            int taps = 0;
            int encoderActions = 0;
            using (var processor = new TriggerProcessor(
                () => manager.CurrentLayer, () => new IntPtr(123), () => manager.ContextGeneration,
                (layer, key, isLong, hwnd, epoch) => taps++,
                (layer, key, gesture, hwnd, epoch) => encoderActions++,
                (key, hwnd, epoch) => holds++, (key, hwnd, epoch) => holds++,
                (key, hwnd, epoch) => { }))
            {
                processor.Process(new BridgeTrigger { Number = 9, IsDown = true, Timestamp = 100 });
                manager.ObserveForeground(Window("Build 11%"));
                Check(manager.ContextGeneration == generation, "progress title does not cancel physical holds");
                Check(manager.ContentGeneration > content, "title still advances separate content epoch");
                processor.Process(new BridgeTrigger { Number = 9, IsDown = false, Timestamp = 750 });
                Check(holds == 1 && taps == 0, "650ms release fallback survives title progress and emits only hold");
                processor.Process(new BridgeTrigger { IsEncoder = true, IsDown = true, Number = 1,
                    EncoderGesture = EncoderGesture.Press, HasReceiptContext = true,
                    ReceiptForeground = new IntPtr(456), ReceiptContextGeneration = generation });
                Check(encoderActions == 0, "encoder cannot rebind to new HWND at release");
                processor.Process(new BridgeTrigger { IsEncoder = true, IsDown = true, Number = 1,
                    EncoderGesture = EncoderGesture.Press, HasReceiptContext = true,
                    ReceiptForeground = new IntPtr(123), ReceiptContextGeneration = generation - 1 });
                Check(encoderActions == 0, "encoder cannot rebind after away-and-back");
                processor.Process(new BridgeTrigger { IsEncoder = true, IsDown = true, Number = 1,
                    EncoderGesture = EncoderGesture.Press, HasReceiptContext = true,
                    ReceiptForeground = new IntPtr(123), ReceiptContextGeneration = generation });
                Check(encoderActions == 1, "valid released encoder executes once");
            }
            ForegroundInfo changed = Window("Build 11%");
            changed.Window = new IntPtr(456);
            manager.ObserveForeground(changed);
            Check(manager.ContextGeneration != generation, "actual window switch still invalidates context");
            manager.ObserveForeground(Window("Build 11%"));
            Check(manager.ContextGeneration > generation + 1, "A-B-A window changes retain epochs");
            manager.SetManualLayer(2);
            bool automaticNotice = false;
            manager.LayerChanged += delegate(object sender, LayerChangedEventArgs args) { automaticNotice = !args.Manual; };
            manager.ObserveForeground(changed);
            Check(!manager.IsManual && automaticNotice, "manual badge clears when another window uses the same logical layer");
            Check(!ActionDispatcher.IsRotaryExpired(100, 350), "250ms rotary boundary is inclusive");
            Check(ActionDispatcher.IsRotaryExpired(100, 351), "rotary expires at 251ms");
            int wrap = int.MaxValue - 10;
            Check(ActionDispatcher.IsRotaryExpired(wrap, unchecked(wrap + 251)), "rotary expiry handles tick wrap");
            TestRotaryQueue();
            Console.WriteLine("PASS: all-layer title/hold, frozen encoder context and bounded rotary regressions.");
        }

        private static ForegroundInfo Window(string title)
        {
            return new ForegroundInfo { Window = new IntPtr(123), ProcessId = 42,
                AutomaticLayer = 2, ProcessName = "WindowsTerminal", Title = title };
        }

        private static void TestRotaryQueue()
        {
            using (var started = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var finished = new ManualResetEvent(false))
            using (var dispatcher = new ActionDispatcher())
            {
                int now = 100;
                int rotations = 0;
                dispatcher.Enqueue(delegate { started.Set(); release.WaitOne(3000); });
                Check(started.WaitOne(1000), "rotary test worker started");
                try
                {
                    for (int i = 0; i < ActionDispatcher.MaximumQueuedRotary; i++)
                        Check(dispatcher.EnqueueRotary(() => rotations++, () => Volatile.Read(ref now)), "rotary capacity accepts step");
                    Check(!dispatcher.EnqueueRotary(() => rotations++, () => Volatile.Read(ref now)), "rotary capacity rejects overflow");
                    Volatile.Write(ref now, 351);
                    dispatcher.Enqueue(() => finished.Set());
                }
                finally { release.Set(); }
                Check(finished.WaitOne(2000), "ordinary button is not dropped with rotary saturation");
                Check(rotations == 0, "expired backlog does not replay");
                finished.Reset();
                Check(dispatcher.EnqueueRotary(() => { rotations++; finished.Set(); }, () => Volatile.Read(ref now)), "rotary capacity recovers after draining");
                Check(finished.WaitOne(1000) && rotations == 1, "fresh rotary remains responsive");
            }
        }
    }
}
