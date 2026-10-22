using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

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
        private const int ReadBlockSize = 65536;
        private const int MarkerOverlap = 192;
        private const int PendingRequestTailBytes = 524288;

        private static readonly StatusMarker[] Markers =
        {
            new StatusMarker(
                "task_complete",
                "\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"",
                AiTaskState.Idle),
            new StatusMarker(
                "task_started",
                "\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"",
                AiTaskState.Thinking),
            new StatusMarker(
                "turn_aborted",
                "\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_aborted\"",
                AiTaskState.Error)
        };

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
            evidence = "no-rollout";
            completionRevision = string.Empty;
            rolloutPath = ChatGptThreadCatalog.NormalizeFileSystemPath(rolloutPath);
            if (string.IsNullOrWhiteSpace(rolloutPath) || !File.Exists(rolloutPath))
                return AiTaskState.Idle;

            StatusMarker marker = FindLastMarker(rolloutPath);
            if (marker == null)
            {
                evidence = "no-task-marker";
                return AiTaskState.Idle;
            }
            evidence = marker.Name;
            if (marker.Name == "task_complete")
                completionRevision = rolloutPath + ":" + marker.Offset.ToString(CultureInfo.InvariantCulture);
            if (marker.State == AiTaskState.Thinking && HasPendingUserRequest(rolloutPath, marker.Offset))
            {
                evidence = "pending-user-request";
                return AiTaskState.WaitingForInput;
            }
            return marker.State;
        }

        internal static AiTaskState ReadTextStateForTest(string text)
        {
            string completionRevision;
            return ReadTextStateForTest(text, out completionRevision);
        }

        internal static AiTaskState ReadTextStateForTest(string text, out string completionRevision)
        {
            string value = text ?? string.Empty;
            StatusMarker marker = FindLastMarkerInText(value);
            completionRevision = marker != null && marker.Name == "task_complete"
                ? marker.Offset.ToString(CultureInfo.InvariantCulture) : string.Empty;
            if (marker == null)
                return AiTaskState.Idle;
            if (marker.State == AiTaskState.Thinking && HasPendingUserRequestInText(value))
                return AiTaskState.WaitingForInput;
            return marker.State;
        }

        private static StatusMarker FindLastMarker(string path)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long position = stream.Length;
                string laterPrefix = string.Empty;
                var bytes = new byte[ReadBlockSize];
                while (position > 0)
                {
                    int count = (int)Math.Min(ReadBlockSize, position);
                    long start = position - count;
                    stream.Position = start;
                    int total = 0;
                    while (total < count)
                    {
                        int read = stream.Read(bytes, total, count - total);
                        if (read <= 0)
                            break;
                        total += read;
                    }

                    // Marker syntax is ASCII. A one-byte/one-character scan
                    // preserves its exact file offset even when a block splits
                    // a multibyte UTF-8 character elsewhere in the rollout.
                    string current = Encoding.ASCII.GetString(bytes, 0, total);
                    string searchable = current + laterPrefix;
                    StatusMarker found = FindLastMarkerInText(searchable);
                    if (found != null)
                    {
                        found.Offset += start;
                        return found;
                    }

                    laterPrefix = current.Substring(0, Math.Min(MarkerOverlap, current.Length));
                    position = start;
                }
            }
            return null;
        }

        private static StatusMarker FindLastMarkerInText(string text)
        {
            StatusMarker found = null;
            int foundAt = -1;
            for (int i = 0; i < Markers.Length; i++)
            {
                int at = text.LastIndexOf(Markers[i].Pattern, StringComparison.Ordinal);
                if (at > foundAt)
                {
                    foundAt = at;
                    found = new StatusMarker(Markers[i].Name, Markers[i].Pattern, Markers[i].State);
                    found.Offset = at;
                }
            }
            return found;
        }

        private static bool HasPendingUserRequest(string path, long turnStartedOffset)
        {
            return HasPendingUserRequestInText(ReadTail(path, PendingRequestTailBytes, turnStartedOffset));
        }

        private static bool HasPendingUserRequestInText(string tail)
        {
            int requestInput = tail.LastIndexOf("\"name\":\"request_user_input\"", StringComparison.Ordinal);
            int pluginInstall = tail.LastIndexOf("\"name\":\"request_plugin_install\"", StringComparison.Ordinal);
            int requestAt = Math.Max(requestInput, pluginInstall);
            if (requestAt < 0)
                return false;

            // A cancelled/unanswered call from an older turn is not a pending
            // request in the new turn. ReadTail is also bounded to the current
            // task_started offset when that marker lies beyond its size limit.
            StatusMarker boundary = FindLastMarkerInText(tail);
            if (boundary != null && requestAt < boundary.Offset)
                return false;

            int lineStart = tail.LastIndexOf('\n', requestAt);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int lineEnd = tail.IndexOf('\n', requestAt);
            if (lineEnd < 0)
                lineEnd = tail.Length;
            string line = tail.Substring(lineStart, lineEnd - lineStart);
            if (line.IndexOf("\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\"", StringComparison.Ordinal) < 0)
                return false;

            const string callIdMarker = "\"call_id\":\"";
            int callIdAt = line.IndexOf(callIdMarker, StringComparison.Ordinal);
            if (callIdAt < 0)
                return false;
            callIdAt += callIdMarker.Length;
            int callIdEnd = line.IndexOf('"', callIdAt);
            if (callIdEnd <= callIdAt)
                return false;
            string callId = line.Substring(callIdAt, callIdEnd - callIdAt);

            string callIdNeedle = callIdMarker + callId + "\"";
            return tail.IndexOf(callIdNeedle, lineEnd, StringComparison.Ordinal) < 0;
        }

        private static string ReadTail(string path, int maximumBytes, long minimumOffset)
        {
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(Math.Max(0, stream.Length - maximumBytes), minimumOffset);
                start = Math.Min(start, stream.Length);
                int count = (int)(stream.Length - start);
                stream.Position = start;
                var bytes = new byte[count];
                int total = 0;
                while (total < count)
                {
                    int read = stream.Read(bytes, total, count - total);
                    if (read <= 0)
                        break;
                    total += read;
                }
                return Encoding.UTF8.GetString(bytes, 0, total);
            }
        }

        private sealed class StatusMarker
        {
            internal readonly string Name;
            internal readonly string Pattern;
            internal readonly AiTaskState State;
            internal long Offset;

            internal StatusMarker(string name, string pattern, AiTaskState state)
            {
                Name = name;
                Pattern = pattern;
                State = state;
            }
        }
    }

    internal sealed class ChatGptCompletionLatch
    {
        private readonly Dictionary<string, AiTaskState> _observedStates =
            new Dictionary<string, AiTaskState>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unacknowledgedCompleted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _completionRevisions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal void Apply(IList<ChatGptTaskStatusSlot> slots)
        {
            if (slots == null)
                return;
            for (int i = 0; i < slots.Count; i++)
            {
                ChatGptTaskStatusSlot slot = slots[i];
                if (slot == null || string.IsNullOrWhiteSpace(slot.ThreadId))
                    continue;

                AiTaskState observed = slot.State;
                AiTaskState previous;
                bool hadPrevious = _observedStates.TryGetValue(slot.ThreadId, out previous);
                bool isCompletion = observed == AiTaskState.Idle &&
                    string.Equals(slot.Evidence, "task_complete", StringComparison.Ordinal);
                string previousRevision;
                bool hasRevision = !string.IsNullOrEmpty(slot.CompletionRevision);
                bool hasPreviousRevision = _completionRevisions.TryGetValue(slot.ThreadId, out previousRevision);
                bool changedCompletion = isCompletion && hasRevision &&
                    (!hasPreviousRevision ||
                     !string.Equals(slot.CompletionRevision, previousRevision, StringComparison.Ordinal));
                bool newlyCompleted = hadPrevious &&
                    isCompletion &&
                    ((previous != AiTaskState.Idle && previous != AiTaskState.Completed) || changedCompletion);

                // The rollout is append-only: its completion marker offset is
                // stable across tail growth and changes for every new turn.
                // Do not discard it on transient no-rollout/no-marker polls.
                if (isCompletion && hasRevision)
                    _completionRevisions[slot.ThreadId] = slot.CompletionRevision;

                if (observed != AiTaskState.Idle)
                    _unacknowledgedCompleted.Remove(slot.ThreadId);
                else if (newlyCompleted)
                    _unacknowledgedCompleted.Add(slot.ThreadId);

                _observedStates[slot.ThreadId] = observed;
                if (observed == AiTaskState.Idle && _unacknowledgedCompleted.Contains(slot.ThreadId))
                {
                    slot.State = AiTaskState.Completed;
                    slot.Evidence = "task_complete-unacknowledged";
                }
            }
        }

        internal bool Acknowledge(string threadId)
        {
            return !string.IsNullOrWhiteSpace(threadId) &&
                _unacknowledgedCompleted.Remove(threadId);
        }
    }

    internal sealed class ChatGptSixSlotStatusMonitor : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<IList<ChatGptTaskStatusSlot>> _onChanged;
        private readonly ChatGptTaskSlotRegistry _slotRegistry;
        private readonly Timer _timer;
        private readonly ChatGptCompletionLatch _completionLatch = new ChatGptCompletionLatch();
        private readonly string[] _threadIdsBySlot = new string[6];
        private string _lastSignature = string.Empty;
        private int _polling;
        private bool _disposed;

        internal ChatGptSixSlotStatusMonitor(
            ChatGptTaskSlotRegistry slotRegistry,
            Action<IList<ChatGptTaskStatusSlot>> onChanged)
        {
            if (slotRegistry == null)
                throw new ArgumentNullException("slotRegistry");
            if (onChanged == null)
                throw new ArgumentNullException("onChanged");
            _slotRegistry = slotRegistry;
            _onChanged = onChanged;
            _timer = new Timer(Poll, null, 250, 750);
        }

        internal void AcknowledgeSlot(int slot)
        {
            if (slot < 1 || slot > _threadIdsBySlot.Length)
                return;
            string threadId;
            bool changed;
            lock (_gate)
            {
                if (_disposed)
                    return;
                threadId = _threadIdsBySlot[slot - 1];
                changed = _completionLatch.Acknowledge(threadId);
                if (changed)
                    _lastSignature = string.Empty;
            }
            if (!changed)
                return;
            Logger.Info("ChatGPT task slot " + slot + " completion acknowledged; green changed to idle white.");
            try
            {
                _timer.Change(0, 750);
            }
            catch (ObjectDisposedException)
            {
                // Shutdown raced with a task-key acknowledgement.
            }
        }

        internal void AcknowledgeThread(string threadId)
        {
            bool changed;
            lock (_gate)
            {
                if (_disposed || string.IsNullOrEmpty(threadId)) return;
                changed = _completionLatch.Acknowledge(threadId);
                if (changed) _lastSignature = string.Empty;
            }
            if (!changed) return;
            try { _timer.Change(0, 750); } catch (ObjectDisposedException) { }
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
                    Array.Clear(_threadIdsBySlot, 0, _threadIdsBySlot.Length);
                    for (int i = 0; i < slots.Count; i++)
                    {
                        int index = slots[i].Slot - 1;
                        if (index >= 0 && index < _threadIdsBySlot.Length)
                            _threadIdsBySlot[index] = slots[i].ThreadId;
                    }

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
