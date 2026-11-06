using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Cxt1024Bridge
{
    internal sealed class ChatGptTaskStatusSlot
    {
        internal int Slot;
        internal string ThreadId;
        internal string Title;
        internal long RecencyAtMilliseconds;
        internal string RolloutPath;
        internal AiTaskState State;
        internal string Evidence;
        internal string CompletionRevision;
    }

    internal static class ChatGptTaskStatusReader
    {
        internal static bool OpenWithSnapshot(ChatGptThreadSlot thread, Func<ChatGptThreadSlot, bool> open,
            out ChatGptTaskStatusSlot opened)
        {
            opened = null;
            if (thread == null || open == null) return false;
            ChatGptTaskStatusSlot snapshot = ReadSlots(new[] { thread })[0];
            if (!open(thread)) return false;
            opened = snapshot;
            return true;
        }

        internal static List<ChatGptTaskStatusSlot> ReadRecent(int limit)
        {
            List<ChatGptThreadSlot> threads = ChatGptThreadCatalog.ReadRecent(limit);
            return ReadSlots(threads);
        }

        internal static List<ChatGptTaskStatusSlot> ReadSlots(IList<ChatGptThreadSlot> threads)
        {
            var result = new List<ChatGptTaskStatusSlot>();
            for (int i = 0; i < threads.Count; i++)
            {
                string evidence;
                string completionRevision;
                AiTaskState state = ReadRolloutState(threads[i].RolloutPath, out evidence, out completionRevision);
                result.Add(new ChatGptTaskStatusSlot
                {
                    Slot = i + 1,
                    ThreadId = threads[i].Id,
                    Title = threads[i].Title,
                    RecencyAtMilliseconds = threads[i].RecencyAtMilliseconds,
                    RolloutPath = threads[i].RolloutPath,
                    State = state,
                    Evidence = evidence,
                    CompletionRevision = completionRevision
                });
            }
            return result;
        }

        internal static AiTaskState ReadRolloutState(string rolloutPath, out string evidence)
        {
            string completionRevision;
            return ReadRolloutState(rolloutPath, out evidence, out completionRevision);
        }

        internal static AiTaskState ReadRolloutState(
            string rolloutPath, out string evidence, out string completionRevision)
        {
            CodexRolloutState result = CodexRolloutState.ReadFile(ChatGptThreadCatalog.NormalizeFileSystemPath(rolloutPath));
            evidence = result.Evidence;
            completionRevision = result.CompletionRevision;
            return result.State;
        }

        internal static AiTaskState ReadTextStateForTest(string text)
        {
            string revision;
            return ReadTextStateForTest(text, out revision);
        }

        internal static AiTaskState ReadTextStateForTest(string text, out string completionRevision)
        {
            CodexRolloutState result = CodexRolloutState.ReadText(text);
            completionRevision = result.CompletionRevision;
            return result.State;
        }
    }

    internal sealed class ChatGptCompletionLatch
    {
        private readonly string _path;
        private readonly Dictionary<string, HashSet<string>> _acknowledged =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        internal string PersistenceError { get; private set; }

        internal ChatGptCompletionLatch(string path = null)
        {
            _path = path;
            PersistenceError = string.Empty;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var stored = Serializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                if (stored == null) return;
                foreach (var pair in stored)
                {
                    var values = pair.Value as object[];
                    if (string.IsNullOrWhiteSpace(pair.Key) || values == null) continue;
                    var revisions = new HashSet<string>(StringComparer.Ordinal);
                    foreach (object value in values)
                        if (value is string && !string.IsNullOrEmpty((string)value)) revisions.Add((string)value);
                    _acknowledged[pair.Key] = revisions;
                }
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) &&
                    !(error is ArgumentException) && !(error is InvalidOperationException)) throw;
                PersistenceError = error.Message;
            }
        }

        internal void Apply(IList<ChatGptTaskStatusSlot> slots)
        {
            if (slots == null) return;
            foreach (ChatGptTaskStatusSlot slot in slots)
            {
                HashSet<string> revisions;
                if (slot != null && slot.State == AiTaskState.Completed && !string.IsNullOrEmpty(slot.ThreadId) &&
                    !string.IsNullOrEmpty(slot.CompletionRevision) && _acknowledged.TryGetValue(slot.ThreadId, out revisions) &&
                    revisions.Contains(slot.CompletionRevision))
                {
                    slot.State = AiTaskState.Idle;
                    slot.Evidence = "completion-viewed";
                }
            }
        }

        internal bool Acknowledge(ChatGptTaskStatusSlot opened)
        {
            return opened != null && opened.State == AiTaskState.Completed &&
                Acknowledge(opened.ThreadId, opened.CompletionRevision);
        }

        internal bool Acknowledge(string threadId, string revision)
        {
            if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrEmpty(revision)) return false;
            HashSet<string> revisions;
            bool existing = _acknowledged.TryGetValue(threadId, out revisions);
            if (!existing) _acknowledged[threadId] = revisions = new HashSet<string>(StringComparer.Ordinal);
            if (!revisions.Add(revision)) return false;
            try
            {
                Save();
                PersistenceError = string.Empty;
                return true;
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is UnauthorizedAccessException) &&
                    !(error is ArgumentException) && !(error is InvalidOperationException)) throw;
                revisions.Remove(revision);
                if (!existing && revisions.Count == 0) _acknowledged.Remove(threadId);
                PersistenceError = error.Message;
                return false;
            }
        }

        private static JavaScriptSerializer Serializer()
        { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 128 }; }

        private void Save()
        {
            if (string.IsNullOrEmpty(_path)) return;
            string directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            Directory.CreateDirectory(directory);
            var data = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _acknowledged)
            {
                var values = new string[pair.Value.Count]; pair.Value.CopyTo(values);
                data[pair.Key] = values;
            }
            string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, Serializer().Serialize(data), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal sealed class ChatGptSixSlotStatusMonitor : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<IList<ChatGptTaskStatusSlot>> _onChanged;
        private readonly ChatGptTaskSlotRegistry _slotRegistry;
        private readonly Timer _timer;
        private readonly ChatGptCompletionLatch _completionLatch;
        private string _lastSignature = string.Empty;
        private int _polling;
        private bool _disposed;

        internal ChatGptSixSlotStatusMonitor(
            ChatGptTaskSlotRegistry slotRegistry,
            Action<IList<ChatGptTaskStatusSlot>> onChanged,
            string acknowledgementPath = null)
        {
            if (slotRegistry == null)
                throw new ArgumentNullException("slotRegistry");
            if (onChanged == null)
                throw new ArgumentNullException("onChanged");
            _slotRegistry = slotRegistry;
            _onChanged = onChanged;
            _completionLatch = new ChatGptCompletionLatch(acknowledgementPath);
            _timer = new Timer(Poll, null, 250, 750);
        }

        internal void AcknowledgeOpenedTask(ChatGptTaskStatusSlot opened)
        {
            bool changed;
            lock (_gate)
            {
                if (_disposed) return;
                changed = _completionLatch.Acknowledge(opened);
                if (changed) _lastSignature = string.Empty;
                else if (_completionLatch.PersistenceError.Length != 0)
                    Logger.Error("Unable to persist viewed Codex completion", new IOException(_completionLatch.PersistenceError));
            }
            if (changed) try { _timer.Change(0, 750); } catch (ObjectDisposedException) { }
        }

        private void Poll(object ignored)
        {
            if (Interlocked.Exchange(ref _polling, 1) != 0)
                return;
            try
            {
                List<ChatGptTaskStatusSlot> slots =
                    ChatGptTaskStatusReader.ReadSlots(_slotRegistry.Snapshot());
                bool changed;
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    _completionLatch.Apply(slots);
                    string signature = BuildSignature(slots);
                    changed = !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
                    if (changed)
                    {
                        _lastSignature = signature;
                        Logger.Info("ChatGPT task LEDs: " + DescribeSlots(slots));
                        _onChanged(slots);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Six-slot ChatGPT status polling failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        private static string BuildSignature(IList<ChatGptTaskStatusSlot> slots)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < slots.Count; i++)
            {
                builder.Append(slots[i].Slot);
                builder.Append(':');
                builder.Append(slots[i].ThreadId);
                builder.Append(':');
                builder.Append((int)slots[i].State);
                builder.Append(':');
                builder.Append(slots[i].CompletionRevision);
                builder.Append('|');
            }
            return builder.ToString();
        }

        private static string DescribeSlots(IList<ChatGptTaskStatusSlot> slots)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < slots.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                builder.Append(slots[i].Slot);
                builder.Append('=');
                builder.Append(slots[i].State);
                builder.Append('(');
                string id = slots[i].ThreadId ?? string.Empty;
                builder.Append(id.Substring(0, Math.Min(8, id.Length)));
                builder.Append('/');
                builder.Append(slots[i].Evidence);
                builder.Append(')');
            }
            return builder.ToString();
        }

        public void Dispose()
        {
            lock (_gate)
                _disposed = true;
            _timer.Dispose();
        }
    }
}
