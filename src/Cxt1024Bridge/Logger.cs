using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Collections.Concurrent;
using System.Threading;

namespace Cxt1024Bridge
{
    internal static class Logger
    {
        private sealed class Entry
        {
            internal DateTime At;
            internal string Level, Message;
            internal Exception Error;
        }
        private static readonly BlockingCollection<Entry> Queue = new BlockingCollection<Entry>(2048);
        private static readonly Thread Writer;
        private static int _dropped;
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cxt1024Bridge");

        static Logger()
        {
            Writer = new Thread(Drain) { IsBackground = true, Name = "CXT12E4 log writer" };
            Writer.Start();
        }

        internal static void FlushAndStop()
        {
            Queue.CompleteAdding();
            Writer.Join(1000); // Never block process shutdown on a stalled disk.
        }

        internal static string LogPath
        {
            get { return Path.Combine(DirectoryPath, "bridge.log"); }
        }

        internal static void Info(string message)
        {
            Write("INFO", message, null);
        }

        internal static void Warn(string message)
        {
            Write("WARN", message, null);
        }

        internal static void Error(string message, Exception exception)
        {
            Write("ERROR", message, exception);
        }

        private static void Write(string level, string message, Exception exception)
        {
            try
            {
                if (!Queue.TryAdd(new Entry { At = DateTime.Now, Level = level, Message = message, Error = exception }))
                    Interlocked.Increment(ref _dropped);
            }
            catch (InvalidOperationException) { }
        }

        private static void Drain()
        {
            foreach (Entry entry in Queue.GetConsumingEnumerable())
                WriteEntry(entry);
        }

        private static void WriteEntry(Entry entry)
        {
            try
            {
                    Directory.CreateDirectory(DirectoryPath);
                    var line = new StringBuilder();
                    line.Append(entry.At.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    line.Append(" [");
                    line.Append(entry.Level);
                    line.Append("] ");
                    line.Append(entry.Message ?? string.Empty);
                    if (entry.Error != null)
                    {
                        line.Append(" | ");
                        line.Append(entry.Error);
                    }
                    int dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped != 0) line.Append(" [log queue dropped=" + dropped + "]");
                    File.AppendAllText(LogPath, line.AppendLine().ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Logging must never stop the bridge or the keyboard hook.
            }
        }
    }
}
