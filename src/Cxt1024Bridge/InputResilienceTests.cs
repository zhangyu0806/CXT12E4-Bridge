using System;
using System.Diagnostics;
using System.Threading;

namespace Cxt1024Bridge
{
    internal static class InputResilienceTests
    {
        private static void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException("Input resilience: " + name);
        }

        internal static void Run()
        {
            Check(!HookInputPump.IsExpired(int.MaxValue - 20, int.MinValue + 20), "tick wrap");
            Check(HookInputPump.IsExpired(0, 501), "stale event threshold");
            int taps = 0, holds = 0, encoders = 0;
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var processor = new TriggerProcessor(
                () => 1, () => new IntPtr(1), () => 1,
                delegate { Interlocked.Increment(ref taps); },
                delegate { Interlocked.Increment(ref encoders); entered.Set(); release.WaitOne(3000); },
                delegate { Interlocked.Increment(ref holds); },
                delegate { Interlocked.Increment(ref holds); }, delegate { }))
            using (var pump = new HookInputPump(processor, () => new IntPtr(1)))
            {
                pump.Receive(new BridgeTrigger { Number = 9, IsDown = true, Timestamp = 1000 });
                Thread.Sleep(30);
                pump.Receive(new BridgeTrigger { IsEncoder = true, Number = 4, IsDown = true, Timestamp = 1040 });
                Check(entered.WaitOne(1000), "blocked business worker fixture");
                var watch = Stopwatch.StartNew();
                pump.Receive(new BridgeTrigger { Number = 9, IsDown = false, Timestamp = 1100 });
                Check(watch.ElapsedMilliseconds < 100, "key release does not wait for business lock");
                Thread.Sleep(720);
                release.Set();
                Thread.Sleep(150);
                Check(holds == 0, "released key must not become a 650ms hold while consumer blocked");
                Check(taps == 0, "expired release is cancelled, not replayed");
                pump.Receive(new BridgeTrigger { Number = 9, IsDown = true, Timestamp = 2000 });
                Thread.Sleep(30);
                pump.Receive(new BridgeTrigger { Number = 9, IsDown = false, Timestamp = 2050 });
                Thread.Sleep(100);
                Check(taps == 1, "fresh short press recovers");
                pump.Receive(new BridgeTrigger { Number = 9, IsDown = true, Timestamp = 3000 });
                Thread.Sleep(800);
                pump.Receive(new BridgeTrigger { Number = 9, IsDown = false, Timestamp = 3800 });
                Thread.Sleep(100);
                Check(holds == 1 && taps == 1, "hold once, no short action on release");
                Check(encoders == 1, "encoder delivered once");
            }

            // A blocked consumer must not force the producer to wait or turn
            // the overflowed physical press into either a tap or a long hold.
            taps = 0; holds = 0;
            long context = 1;
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var processor = new TriggerProcessor(
                () => 1, () => new IntPtr(1), () => Interlocked.Read(ref context),
                delegate { Interlocked.Increment(ref taps); },
                delegate { entered.Set(); release.WaitOne(3000); },
                delegate { Interlocked.Increment(ref holds); },
                delegate { Interlocked.Increment(ref holds); }, delegate { }))
            using (var pump = new HookInputPump(processor, () => new IntPtr(1), () => Interlocked.Read(ref context)))
            {
                pump.Receive(new BridgeTrigger { IsEncoder = true, Number = 4, IsDown = true, Timestamp = 4000 });
                Check(entered.WaitOne(1000), "overflow fixture");
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < 300; i++)
                    pump.Receive(new BridgeTrigger { Number = 9, IsDown = i % 2 == 0, Timestamp = 4100 + i });
                Check(watch.ElapsedMilliseconds < 150, "overflow producer stays responsive");
                Interlocked.Increment(ref context); // even if HWND comes back to the same app
                release.Set();
                Thread.Sleep(850);
                Check(taps == 0 && holds == 0, "overflow/context switch drops pending actions");
                pump.Receive(new BridgeTrigger { Number = 9, IsDown = true, Timestamp = 5000 });
                Thread.Sleep(30);
                pump.Receive(new BridgeTrigger { Number = 9, IsDown = false, Timestamp = 5050 });
                Thread.Sleep(100);
                Check(taps == 1, "queue recovers after overflow");
            }

            int latest = 0, writes = 0;
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var done = new ManualResetEvent(false))
            using (var worker = new LatestWorkWorker())
            {
                worker.Submit(delegate { entered.Set(); release.WaitOne(3000); Interlocked.Increment(ref writes); });
                Check(entered.WaitOne(1000), "slow export fixture");
                var watch = Stopwatch.StartNew();
                for (int i = 1; i <= 100; i++)
                {
                    int snapshot = i;
                    worker.Submit(delegate { latest = snapshot; Interlocked.Increment(ref writes); done.Set(); });
                }
                Check(watch.ElapsedMilliseconds < 100, "slow disk cannot block state publisher");
                release.Set();
                Check(done.WaitOne(1000) && latest == 100 && writes == 2, "only newest pending state exported");
            }
        }
    }
}
