using System;
using System.Collections.Generic;
using System.IO;

namespace Cxt1024Bridge
{
    internal static class CodexRolloutPathCacheTests
    {
        private static int _checks;
        private static void Check(bool condition, string label)
        {
            _checks++;
            if (!condition) throw new InvalidOperationException("Rollout path cache test failed: " + label);
        }

        internal static void Run()
        {
            _checks = 0;
            DateTime now = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
            int scans = 0;
            bool failScan = false;
            var files = new HashSet<string> { "first.jsonl" };
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "first", "first.jsonl" } };
            var roots = new[] { "sessions-one" };
            var cache = new CodexRolloutPathCache(delegate(IList<string> requested)
            {
                scans++;
                if (failScan) throw new IOException("test scan failure");
                return new Dictionary<string, string>(index, StringComparer.OrdinalIgnoreCase);
            }, files.Contains, delegate { return now; });

            Check(cache.Find("first", roots) == "first.jsonl" && scans == 1, "first lookup scans once");
            for (int i = 0; i < 64; i++)
                Check(cache.Find("remote-" + i, roots) == string.Empty, "remote chat stays absent");
            Check(scans == 1, "64 missing IDs share the initial scan");
            now = now.AddMilliseconds(750);
            for (int i = 0; i < 64; i++) cache.Find("remote-" + i, roots);
            Check(scans == 1, "next status poll does not repeat the directory walk");
            files.Add("late.jsonl"); index["late"] = "late.jsonl";
            now = now.AddMilliseconds(249);
            Check(cache.Find("late", roots) == string.Empty && scans == 1, "new ID respects shared retry interval");
            now = now.AddMilliseconds(1);
            Check(cache.Find("late", roots) == "late.jsonl" && scans == 2, "late local file appears at next retry");
            for (int i = 0; i < 64; i++) cache.Find("remote-" + i, roots);
            Check(scans == 2, "one miss refresh serves the entire catalog page");
            files.Remove("late.jsonl");
            Check(cache.Find("late", roots) == string.Empty && scans == 2, "deleted cached file is never returned");
            index["late"] = "moved.jsonl"; files.Add("moved.jsonl");
            now = now.AddSeconds(1);
            Check(cache.Find("late", roots) == "moved.jsonl" && scans == 3, "moved file is recovered after retry");
            Check(cache.Find("first", new[] { "sessions-two" }) == "first.jsonl" && scans == 4,
                "changed home invalidates the cache immediately");
            cache.Find("first", new[] { "SESSIONS-TWO" });
            Check(scans == 4, "Windows home comparison ignores case");
            now = now.AddSeconds(30);
            cache.Find("first", new[] { "sessions-two" });
            Check(scans == 5, "positive entries retain periodic refresh");
            now = now.AddSeconds(-1);
            cache.Find("first", new[] { "sessions-two" });
            Check(scans == 6, "backward clock adjustment cannot stall discovery");
            now = now.AddSeconds(1); failScan = true;
            try { cache.Find("missing", new[] { "sessions-two" }); Check(false, "expected scan failure"); }
            catch (IOException) { Check(scans == 7, "scan failure remains observable"); }
            failScan = false; index["recovered"] = "first.jsonl";
            Check(cache.Find("recovered", new[] { "sessions-two" }) == "first.jsonl" && scans == 8,
                "failed scan does not publish a cached miss");
            Check(cache.Find("first", new string[0]) == string.Empty && scans == 8, "empty roots do not scan");
            Console.WriteLine("Codex rollout path cache checks passed: " + _checks);
        }
    }
}
