using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Cxt1024Bridge
{
    internal sealed class TerminalSessionStatusSlot
    {
        internal int Slot;
        internal string SessionId;
        internal string SessionIdentity;
        internal string SessionName;
        internal AiTaskState State;
        internal long StateRevisionNanoseconds;
        internal bool Present;
    }

    internal sealed class TerminalPaneSnapshot
    {
        internal string SessionId;
        internal string SessionIdentity;
        internal long SessionCreatedUnix;
        internal int ServerPid;
        internal string SessionName;
        internal string PaneId;
    }

    internal sealed class TerminalSessionTarget
    {
        internal int Slot;
        internal string SessionId;
        internal string SessionIdentity;
        internal long SessionCreatedUnix;
        internal int ServerPid;
        internal string SessionName;
        internal long SwitchRevisionNanoseconds;
    }

    internal enum TerminalTmuxAction
    {
        None,
        SplitRight,
        SplitDown,
        SelectHorizontal,
        SelectVertical,
        ToggleZoom,
        ToggleOrientation,
        RotatePanes,
        ChooseWindow,
        StashAgentInput,
        CopyMode,
        LastPane,
        ResizeLeft,
        ResizeRight,
        EqualizeHorizontal,
        ResizeUp,
        ResizeDown,
        EqualizeVertical,
        PreviousWindow,
        NextWindow,
        LastWindow,
        SelectLeft,
        SelectRight,
        SelectUp,
        SelectDown,
        PreviousSession,
        NextSession
    }

    internal sealed class TerminalClientTarget
    {
        internal long Activity;
        internal string ClientName;
        internal string SessionId;
        internal string PaneId;
        internal string WindowId;
    }

    internal static class TerminalSessionIdentity
    {
        internal static string Build(string sessionId, long sessionCreatedUnix, int serverPid)
        {
            if (string.IsNullOrEmpty(sessionId))
                return string.Empty;
            if (sessionCreatedUnix <= 0 || serverPid <= 0)
                return sessionId;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}@{1}:{2}",
                sessionId,
                sessionCreatedUnix,
                serverPid);
        }

        internal static string ForSlot(TerminalSessionStatusSlot slot)
        {
            if (slot == null)
                return string.Empty;
            return string.IsNullOrEmpty(slot.SessionIdentity)
                ? slot.SessionId ?? string.Empty
                : slot.SessionIdentity;
        }

    }

    internal sealed class TmuxServerUnavailableException : InvalidOperationException
    {
        internal TmuxServerUnavailableException(string message)
            : base(message)
        {
        }
    }

    internal static class TmuxInventoryCache
    {
        private const string Header = "CXT-TMUX-INVENTORY-V1";
        private const int MaximumPaneCount = 512;
        private const long MaximumCacheBytes = 1024 * 1024;
        private static readonly TimeSpan MaximumAge = TimeSpan.FromHours(1);
        private static readonly TimeSpan ClockDriftTolerance = TimeSpan.FromSeconds(30);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        internal static ulong CurrentUptimeMilliseconds()
        {
            return GetTickCount64();
        }

        internal static bool TryRead(
            string path,
            DateTime nowUtc,
            ulong currentUptimeMilliseconds,
            out IList<TerminalPaneSnapshot> panes)
        {
            panes = new List<TerminalPaneSnapshot>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;
            try
            {
                if (new FileInfo(path).Length > MaximumCacheBytes)
                    return false;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length < 2 || lines.Length > MaximumPaneCount + 1)
                    return false;
                string[] headerFields = lines[0].Split('\t');
                long savedUtcTicks;
                ulong savedUptimeMilliseconds;
                if (headerFields.Length != 3 ||
                    !string.Equals(headerFields[0], Header, StringComparison.Ordinal) ||
                    !long.TryParse(headerFields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out savedUtcTicks) ||
                    !ulong.TryParse(headerFields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out savedUptimeMilliseconds) ||
                    savedUtcTicks < DateTime.MinValue.Ticks || savedUtcTicks > DateTime.MaxValue.Ticks ||
                    currentUptimeMilliseconds < savedUptimeMilliseconds)
                    return false;

                DateTime savedUtc = new DateTime(savedUtcTicks, DateTimeKind.Utc);
                TimeSpan wallElapsed = nowUtc - savedUtc;
                TimeSpan uptimeElapsed = TimeSpan.FromMilliseconds(
                    currentUptimeMilliseconds - savedUptimeMilliseconds);
                if (wallElapsed < TimeSpan.Zero || wallElapsed > MaximumAge ||
                    Math.Abs((wallElapsed - uptimeElapsed).TotalMilliseconds) > ClockDriftTolerance.TotalMilliseconds)
                    return false;

                var parsed = new List<TerminalPaneSnapshot>();
                var seenPaneIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                        continue;
                    string[] fields = lines[i].Split('\t');
                    long sessionCreatedUnix;
                    int serverPid;
                    string sessionId;
                    string sessionName;
                    string paneId;
                    if (fields.Length != 5 ||
                        !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sessionCreatedUnix) ||
                        !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out serverPid) ||
                        !TryDecode(fields[0], out sessionId) ||
                        !TryDecode(fields[3], out sessionName) ||
                        !TryDecode(fields[4], out paneId) ||
                        string.IsNullOrEmpty(sessionId) || sessionId[0] != '$' ||
                        sessionCreatedUnix <= 0 || serverPid <= 0 ||
                        string.IsNullOrEmpty(sessionName) ||
                        string.IsNullOrEmpty(paneId) || paneId[0] != '%' ||
                        !seenPaneIds.Add(paneId))
                        return false;
                    parsed.Add(new TerminalPaneSnapshot
                    {
                        SessionId = sessionId,
                        SessionIdentity = TerminalSessionIdentity.Build(sessionId, sessionCreatedUnix, serverPid),
                        SessionCreatedUnix = sessionCreatedUnix,
                        ServerPid = serverPid,
                        SessionName = sessionName,
                        PaneId = paneId
                    });
                }
                if (parsed.Count == 0)
                    return false;
                panes = parsed;
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        internal static void Write(
            string path,
            IList<TerminalPaneSnapshot> panes,
            DateTime savedUtc,
            ulong savedUptimeMilliseconds)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("An inventory cache path is required.", "path");
            if (panes == null || panes.Count == 0)
            {
                if (!Delete(path))
                    throw new IOException("Unable to clear the tmux inventory cache.");
                return;
            }
            if (panes.Count > MaximumPaneCount)
                throw new InvalidDataException("The tmux pane inventory is too large to cache.");
            var lines = new List<string>();
            var seenPaneIds = new HashSet<string>(StringComparer.Ordinal);
            lines.Add(string.Format(
                CultureInfo.InvariantCulture,
                "{0}\t{1}\t{2}",
                Header,
                savedUtc.ToUniversalTime().Ticks,
                savedUptimeMilliseconds));
            for (int i = 0; i < panes.Count; i++)
            {
                TerminalPaneSnapshot pane = panes[i];
                if (pane == null || string.IsNullOrEmpty(pane.SessionId) ||
                    pane.SessionId[0] != '$' ||
                    pane.SessionCreatedUnix <= 0 || pane.ServerPid <= 0 ||
                    string.IsNullOrEmpty(pane.SessionName) || string.IsNullOrEmpty(pane.PaneId) ||
                    pane.PaneId[0] != '%' || !seenPaneIds.Add(pane.PaneId))
                    throw new InvalidDataException("Cannot cache an incomplete tmux pane inventory.");
                lines.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}\t{1}\t{2}\t{3}\t{4}",
                    Encode(pane.SessionId),
                    pane.SessionCreatedUnix,
                    pane.ServerPid,
                    Encode(pane.SessionName),
                    Encode(pane.PaneId)));
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllLines(temporaryPath, lines.ToArray(), new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temporaryPath, path, null);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }

        internal static bool Delete(string path)
        {
            if (string.IsNullOrEmpty(path))
                return true;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static bool TryDecode(string value, out string decoded)
        {
            decoded = string.Empty;
            try
            {
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }

    internal sealed class TerminalCompletionLatch
    {
        private readonly Dictionary<string, AiTaskState> _observedStates =
            new Dictionary<string, AiTaskState>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _highestCompletionRevisions =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _acknowledgedThroughRevisions =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _unacknowledgedCompletionRevisions =
            new Dictionary<string, long>(StringComparer.Ordinal);

        internal void Apply(IList<TerminalSessionStatusSlot> slots)
        {
            if (slots == null)
                return;
            for (int i = 0; i < slots.Count; i++)
            {
                TerminalSessionStatusSlot slot = slots[i];
                string identity = TerminalSessionIdentity.ForSlot(slot);
                if (slot == null || !slot.Present || string.IsNullOrEmpty(identity))
                    continue;

                AiTaskState observed = slot.State;
                AiTaskState previous;
                bool hadPrevious = _observedStates.TryGetValue(identity, out previous);
                long highestCompletionRevision;
                _highestCompletionRevisions.TryGetValue(identity, out highestCompletionRevision);
                long acknowledgedThroughRevision;
                _acknowledgedThroughRevisions.TryGetValue(identity, out acknowledgedThroughRevision);
                bool hasNewCompletionRevision = slot.StateRevisionNanoseconds > 0 &&
                    slot.StateRevisionNanoseconds > highestCompletionRevision;
                bool newlyCompleted = observed == AiTaskState.Completed &&
                    ((hadPrevious && hasNewCompletionRevision) ||
                     (hadPrevious && slot.StateRevisionNanoseconds <= 0 &&
                      previous != AiTaskState.Idle && previous != AiTaskState.Completed) ||
                     (!hadPrevious && acknowledgedThroughRevision > 0 &&
                      slot.StateRevisionNanoseconds > acknowledgedThroughRevision)) &&
                    (acknowledgedThroughRevision <= 0 ||
                     slot.StateRevisionNanoseconds <= 0 ||
                     slot.StateRevisionNanoseconds > acknowledgedThroughRevision);

                if (observed != AiTaskState.Completed)
                    _unacknowledgedCompletionRevisions.Remove(identity);
                else if (newlyCompleted)
                    _unacknowledgedCompletionRevisions[identity] = slot.StateRevisionNanoseconds;

                _observedStates[identity] = observed;
                if (observed == AiTaskState.Completed &&
                    slot.StateRevisionNanoseconds > highestCompletionRevision)
                    _highestCompletionRevisions[identity] = slot.StateRevisionNanoseconds;
                if (observed == AiTaskState.Completed &&
                    !_unacknowledgedCompletionRevisions.ContainsKey(identity))
                    slot.State = AiTaskState.Idle;
            }
        }

        internal bool Acknowledge(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return false;
            return _unacknowledgedCompletionRevisions.Remove(sessionId);
        }

        internal bool AcknowledgeUpTo(string sessionId, long cutoffRevisionNanoseconds)
        {
            if (string.IsNullOrEmpty(sessionId) || cutoffRevisionNanoseconds <= 0)
                return false;
            long previousCutoff;
            if (!_acknowledgedThroughRevisions.TryGetValue(sessionId, out previousCutoff) ||
                cutoffRevisionNanoseconds > previousCutoff)
                _acknowledgedThroughRevisions[sessionId] = cutoffRevisionNanoseconds;
            long completionRevision;
            if (!_unacknowledgedCompletionRevisions.TryGetValue(sessionId, out completionRevision) ||
                completionRevision <= 0 || completionRevision > cutoffRevisionNanoseconds)
                return false;
            return _unacknowledgedCompletionRevisions.Remove(sessionId);
        }

        internal void RetainLiveSessions(IList<TerminalSessionStatusSlot> slots)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            if (slots != null)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    TerminalSessionStatusSlot slot = slots[i];
                    string identity = TerminalSessionIdentity.ForSlot(slot);
                    if (slot != null && slot.Present && !string.IsNullOrEmpty(identity))
                        live.Add(identity);
                }
            }
            var stale = new List<string>();
            foreach (string sessionName in _observedStates.Keys)
            {
                if (!live.Contains(sessionName))
                    stale.Add(sessionName);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                _observedStates.Remove(stale[i]);
                _highestCompletionRevisions.Remove(stale[i]);
                _acknowledgedThroughRevisions.Remove(stale[i]);
                _unacknowledgedCompletionRevisions.Remove(stale[i]);
            }
        }
    }

    internal sealed class TerminalSixSessionStatusMonitor : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Action<IList<TerminalSessionStatusSlot>> _onChanged;
        private readonly Timer _timer;
        private readonly string _statusDirectory;
        private readonly string _inventoryCachePath;
        private readonly TerminalCompletionLatch _completionLatch = new TerminalCompletionLatch();
        private static readonly TimeSpan InventoryRefreshInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan InventoryCacheWriteInterval = TimeSpan.FromSeconds(30);
        // Start fail-closed. If the first WSL/tmux inventory read fails, an
        // empty live inventory keeps every session LED off instead of trusting
        // possibly stale pane state files. Once a live inventory has been
        // verified, transport failures keep that last-known inventory. Only
        // tmux's explicit no-server response can clear presence; empty or
        // malformed output is not proof that a session disappeared.
        private IList<TerminalPaneSnapshot> _lastLivePanes = new List<TerminalPaneSnapshot>();
        private ulong _nextInventoryAttemptUptimeMilliseconds;
        private DateTime _lastInventoryCacheWriteUtc = DateTime.MinValue;
        private string _lastCachedInventorySignature = string.Empty;
        private string _lastSignature = string.Empty;
        private int _consecutiveInventoryFailures;
        private int _polling;
        private bool _disposed;
        private bool _liveInventoryAvailable;
        private bool _inventoryDegradedWarningLogged;
        private bool _inventoryFailClosedWarningLogged;

        internal TerminalSixSessionStatusMonitor(Action<IList<TerminalSessionStatusSlot>> onChanged)
        {
            _onChanged = onChanged;
            _statusDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cxt1024Bridge",
                "terminal-status");
            Directory.CreateDirectory(_statusDirectory);
            _inventoryCachePath = Path.Combine(_statusDirectory, "live-inventory-v1.cache");
            IList<TerminalPaneSnapshot> cachedPanes;
            if (TmuxInventoryCache.TryRead(
                _inventoryCachePath,
                DateTime.UtcNow,
                TmuxInventoryCache.CurrentUptimeMilliseconds(),
                out cachedPanes))
            {
                _lastLivePanes = cachedPanes;
                _liveInventoryAvailable = true;
                _lastCachedInventorySignature = BuildInventorySignature(cachedPanes);
                Logger.Info("Restored the last verified tmux inventory from the same Windows boot; a live refresh will follow.");
            }
            _timer = new Timer(Poll, null, 0, 650);
        }

        private void Poll(object state)
        {
            if (_disposed || Interlocked.Exchange(ref _polling, 1) != 0)
                return;
            try
            {
                ulong pollUptimeMilliseconds = TmuxInventoryCache.CurrentUptimeMilliseconds();
                if (pollUptimeMilliseconds >= _nextInventoryAttemptUptimeMilliseconds)
                {
                    try
                    {
                        IList<TerminalPaneSnapshot> livePanes = TmuxSessionClient.ReadPanes();
                        bool wasDegraded = _consecutiveInventoryFailures > 0;
                        _lastLivePanes = livePanes;
                        _consecutiveInventoryFailures = 0;
                        _liveInventoryAvailable = true;
                        _nextInventoryAttemptUptimeMilliseconds = AddRetryDelay(
                            TmuxInventoryCache.CurrentUptimeMilliseconds(),
                            InventoryRefreshInterval);
                        PersistInventoryCacheIfNeeded(livePanes);
                        if (wasDegraded)
                            Logger.Info("Live tmux session inventory recovered after WSL transport failures.");
                        _inventoryDegradedWarningLogged = false;
                        _inventoryFailClosedWarningLogged = false;
                    }
                    catch (TmuxServerUnavailableException ex)
                    {
                        _lastLivePanes = new List<TerminalPaneSnapshot>();
                        _liveInventoryAvailable = true;
                        _consecutiveInventoryFailures = 0;
                        _nextInventoryAttemptUptimeMilliseconds = AddRetryDelay(
                            TmuxInventoryCache.CurrentUptimeMilliseconds(),
                            InventoryRefreshInterval);
                        if (!TmuxInventoryCache.Delete(_inventoryCachePath))
                            Logger.Warn("tmux reported no server, but the stale inventory cache could not be deleted.");
                        _lastCachedInventorySignature = string.Empty;
                        _inventoryDegradedWarningLogged = false;
                        if (!_inventoryFailClosedWarningLogged)
                        {
                            Logger.Info("tmux explicitly reported that no server is running; session LEDs were cleared. " + ex.Message);
                            _inventoryFailClosedWarningLogged = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _consecutiveInventoryFailures++;
                        _nextInventoryAttemptUptimeMilliseconds = AddRetryDelay(
                            TmuxInventoryCache.CurrentUptimeMilliseconds(),
                            GetInventoryRetryDelay(_consecutiveInventoryFailures));
                        if (ShouldRetainLiveInventoryAfterFailure(
                            ex,
                            _liveInventoryAvailable))
                        {
                            if (!_inventoryDegradedWarningLogged)
                            {
                                Logger.Warn(
                                    "Unable to refresh live tmux session inventory; retaining the last verified inventory and backing off WSL retries. " +
                                    ex.Message);
                                _inventoryDegradedWarningLogged = true;
                            }
                        }
                        else
                        {
                            _lastLivePanes = new List<TerminalPaneSnapshot>();
                            _liveInventoryAvailable = false;
                            if (!_inventoryFailClosedWarningLogged)
                            {
                                Logger.Warn("Unable to read an initial live tmux session inventory; no verified cache exists, so session LEDs remain off until a successful read. " + ex.Message);
                                _inventoryFailClosedWarningLogged = true;
                            }
                        }
                    }
                }
                IList<TerminalSessionStatusSlot> slots;
                bool changed;
                lock (_gate)
                {
                    if (_disposed)
                        return;
                    // Read the raw snapshot under the same lifecycle gate as
                    // acknowledgement. Otherwise a pre-switch thinking
                    // snapshot could consume a token armed after the file had
                    // already advanced to completed, causing the next poll to
                    // incorrectly relatch green.
                    slots = ReadSlots(_statusDirectory, _lastLivePanes);
                    _completionLatch.Apply(slots);
                    if (_liveInventoryAvailable)
                        _completionLatch.RetainLiveSessions(slots);
                    string signature = BuildSignature(slots);
                    changed = !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
                    if (changed)
                    {
                        _lastSignature = signature;
                        Logger.Info("tmux session LEDs: " + DescribeSlots(slots));
                        _onChanged(slots);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to read terminal session status", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        internal static bool ShouldRetainLiveInventoryAfterFailure(
            Exception exception,
            bool liveInventoryAvailable)
        {
            return exception != null &&
                !(exception is TmuxServerUnavailableException) &&
                liveInventoryAvailable;
        }

        internal static TimeSpan GetInventoryRetryDelay(int consecutiveFailures)
        {
            int failures = Math.Max(1, consecutiveFailures);
            int seconds = 2;
            for (int i = 1; i < failures && seconds < 60; i++)
                seconds = Math.Min(60, seconds * 2);
            return TimeSpan.FromSeconds(seconds);
        }

        private static ulong AddRetryDelay(ulong uptimeMilliseconds, TimeSpan delay)
        {
            ulong delayMilliseconds = (ulong)Math.Max(0, Math.Ceiling(delay.TotalMilliseconds));
            return ulong.MaxValue - uptimeMilliseconds < delayMilliseconds
                ? ulong.MaxValue
                : uptimeMilliseconds + delayMilliseconds;
        }

        private void PersistInventoryCacheIfNeeded(IList<TerminalPaneSnapshot> panes)
        {
            if (panes == null || panes.Count == 0)
            {
                if (!TmuxInventoryCache.Delete(_inventoryCachePath))
                    Logger.Warn("Unable to clear the empty tmux inventory cache.");
                _lastCachedInventorySignature = string.Empty;
                return;
            }
            string inventorySignature = BuildInventorySignature(panes);
            DateTime nowUtc = DateTime.UtcNow;
            if (string.Equals(inventorySignature, _lastCachedInventorySignature, StringComparison.Ordinal) &&
                nowUtc - _lastInventoryCacheWriteUtc < InventoryCacheWriteInterval)
                return;
            try
            {
                TmuxInventoryCache.Write(
                    _inventoryCachePath,
                    panes,
                    nowUtc,
                    TmuxInventoryCache.CurrentUptimeMilliseconds());
                _lastCachedInventorySignature = inventorySignature;
                _lastInventoryCacheWriteUtc = nowUtc;
            }
            catch (Exception ex)
            {
                Logger.Warn("Unable to persist the verified tmux inventory cache. " + ex.Message);
            }
        }

        internal static string BuildInventorySignature(IList<TerminalPaneSnapshot> panes)
        {
            if (panes == null || panes.Count == 0)
                return string.Empty;
            var parts = new List<string>();
            for (int i = 0; i < panes.Count; i++)
            {
                TerminalPaneSnapshot pane = panes[i];
                if (pane == null)
                    continue;
                parts.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}\t{1}\t{2}\t{3}\t{4}",
                    pane.SessionId,
                    pane.SessionCreatedUnix,
                    pane.ServerPid,
                    pane.SessionName,
                    pane.PaneId));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join("\n", parts.ToArray());
        }

        internal void AcknowledgeSession(TerminalSessionTarget target)
        {
            if (target == null || target.Slot < 1 || target.Slot > 6 || string.IsNullOrEmpty(target.SessionIdentity))
                return;
            bool changed;
            bool sampledCompleted = false;
            lock (_gate)
            {
                if (_disposed)
                    return;
                // Never clear a completion that arrived while switch-client
                // was running or after it returned. Its revision is later than
                // the pre-switch cutoff and must remain unread/green.
                changed = _completionLatch.AcknowledgeUpTo(
                    target.SessionIdentity,
                    target.SwitchRevisionNanoseconds);
                try
                {
                    AiTaskState sampledState;
                    long sampledRevision;
                    bool found = TryReadExactTargetState(_statusDirectory, target, out sampledState, out sampledRevision);
                    if (!found && _liveInventoryAvailable)
                    {
                        IList<TerminalSessionStatusSlot> rawSlots = ReadSlots(_statusDirectory, _lastLivePanes);
                        for (int i = 0; i < rawSlots.Count; i++)
                        {
                            TerminalSessionStatusSlot slot = rawSlots[i];
                            if (!string.Equals(
                                    TerminalSessionIdentity.ForSlot(slot),
                                    target.SessionIdentity,
                                    StringComparison.Ordinal))
                                continue;
                            found = true;
                            sampledState = slot.State;
                            sampledRevision = slot.StateRevisionNanoseconds;
                            break;
                        }
                    }
                    if (found && sampledState == AiTaskState.Completed &&
                        sampledRevision > 0 &&
                        sampledRevision <= target.SwitchRevisionNanoseconds)
                    {
                        sampledCompleted = true;
                        var exactSlot = new List<TerminalSessionStatusSlot>
                        {
                            new TerminalSessionStatusSlot
                            {
                                Slot = target.Slot,
                                SessionId = target.SessionId,
                                SessionIdentity = target.SessionIdentity,
                                SessionName = target.SessionName,
                                State = AiTaskState.Completed,
                                StateRevisionNanoseconds = sampledRevision,
                                Present = true
                            }
                        };
                        _completionLatch.Apply(exactSlot);
                        changed = _completionLatch.AcknowledgeUpTo(
                            target.SessionIdentity,
                            target.SwitchRevisionNanoseconds) || changed;
                    }
                }
                catch (Exception ex)
                {
                    // A status sampling failure must never escape the
                    // successful switch path or prevent terminal focus.
                    Logger.Warn("Unable to sample tmux completion during session acknowledgement. " + ex.Message);
                }
                _lastSignature = string.Empty;
            }
            Logger.Info(changed
                ? "tmux session slot " + target.Slot + " completion acknowledged; green changed to idle white."
                : sampledCompleted
                    ? "tmux session slot " + target.Slot + " completion was already acknowledged."
                    : "tmux session slot " + target.Slot + " had no completed state at switch time; a later completion will remain green.");
            try
            {
                _timer.Change(0, 650);
            }
            catch (ObjectDisposedException)
            {
                // Shutdown raced with a successful session switch.
            }
        }

        internal static bool TryReadExactTargetState(
            string directory,
            TerminalSessionTarget target,
            out AiTaskState state,
            out long revisionNanoseconds)
        {
            state = AiTaskState.Idle;
            revisionNanoseconds = 0;
            if (target == null || string.IsNullOrEmpty(target.SessionId) ||
                target.SessionCreatedUnix <= 0 || target.ServerPid <= 0 ||
                !Directory.Exists(directory))
                return false;
            bool found = false;
            string[] files = Directory.GetFiles(directory, "pane-*.state", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(files[i]) < DateTime.UtcNow.AddDays(-7))
                        continue;
                    string[] fields = File.ReadAllText(files[i]).Trim().Split('\t');
                    long created;
                    int serverPid;
                    long candidateRevision;
                    if (fields.Length < 7 ||
                        !string.Equals(fields[4], target.SessionId, StringComparison.Ordinal) ||
                        !long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out created) ||
                        !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out serverPid) ||
                        created != target.SessionCreatedUnix || serverPid != target.ServerPid)
                        continue;
                    if (fields.Length >= 8)
                    {
                        if (!long.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out candidateRevision))
                            continue;
                    }
                    else
                    {
                        long reportedAt;
                        if (!long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out reportedAt) ||
                            reportedAt > long.MaxValue / 1000000000L)
                            continue;
                        candidateRevision = reportedAt * 1000000000L;
                    }
                    bool present;
                    AiTaskState candidate = ParseState(fields[2], out present);
                    if (!present)
                        continue;
                    found = true;
                    if (StatePriority(candidate) > StatePriority(state) ||
                        (StatePriority(candidate) == StatePriority(state) && candidateRevision >= revisionNanoseconds))
                    {
                        state = candidate;
                        revisionNanoseconds = candidateRevision;
                    }
                }
                catch (IOException)
                {
                    // Atomic hook replacement can race one read; the already
                    // latched completion was still acknowledged above.
                }
            }
            return found;
        }

        internal static IList<TerminalSessionStatusSlot> ReadSlots(string directory)
        {
            return ReadSlots(directory, null);
        }

        internal static IList<TerminalSessionStatusSlot> ReadSlots(
            string directory,
            IList<TerminalPaneSnapshot> livePanes)
        {
            var result = new TerminalSessionStatusSlot[6];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new TerminalSessionStatusSlot
                {
                    Slot = i + 1,
                    SessionId = string.Empty,
                    SessionIdentity = string.Empty,
                    SessionName = string.Empty,
                    State = AiTaskState.Idle,
                    StateRevisionNanoseconds = 0,
                    Present = false
                };
            }

            Dictionary<string, TerminalPaneSnapshot> liveSessionByPane = null;
            var ambiguousSlots = new bool[result.Length];
            if (livePanes != null)
            {
                liveSessionByPane = new Dictionary<string, TerminalPaneSnapshot>(StringComparer.Ordinal);
                for (int i = 0; i < livePanes.Count; i++)
                {
                    TerminalPaneSnapshot pane = livePanes[i];
                    if (pane == null || string.IsNullOrEmpty(pane.PaneId) ||
                        string.IsNullOrEmpty(pane.SessionId) || string.IsNullOrEmpty(pane.SessionName))
                        continue;
                    liveSessionByPane[pane.PaneId] = pane;
                    int liveSlot = ParseSessionSlot(pane.SessionName);
                    if (liveSlot < 1 || liveSlot > result.Length)
                        continue;
                    TerminalSessionStatusSlot liveTarget = result[liveSlot - 1];
                    if (!liveTarget.Present)
                    {
                        liveTarget.Present = true;
                        liveTarget.SessionId = pane.SessionId;
                        liveTarget.SessionIdentity = string.IsNullOrEmpty(pane.SessionIdentity)
                            ? TerminalSessionIdentity.Build(pane.SessionId, pane.SessionCreatedUnix, pane.ServerPid)
                            : pane.SessionIdentity;
                        liveTarget.SessionName = pane.SessionName;
                    }
                    else if (!string.Equals(liveTarget.SessionId, pane.SessionId, StringComparison.Ordinal))
                    {
                        ambiguousSlots[liveSlot - 1] = true;
                    }
                }
                for (int i = 0; i < ambiguousSlots.Length; i++)
                {
                    if (!ambiguousSlots[i])
                        continue;
                    result[i].Present = false;
                    result[i].SessionId = string.Empty;
                    result[i].SessionIdentity = string.Empty;
                    result[i].SessionName = string.Empty;
                }
            }

            if (!Directory.Exists(directory))
                return result;

            string[] files = Directory.GetFiles(directory, "pane-*.state", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(files[i]) < DateTime.UtcNow.AddDays(-7))
                        continue;
                    string[] fields = File.ReadAllText(files[i]).Trim().Split('\t');
                    if (fields.Length < 3)
                        continue;
                    TerminalPaneSnapshot liveSession = null;
                    if (liveSessionByPane != null)
                    {
                        if (!liveSessionByPane.TryGetValue(fields[1], out liveSession))
                            continue;
                        if (fields.Length >= 7)
                        {
                            long reportedCreated;
                            int reportedPid;
                            if (!string.Equals(fields[4], liveSession.SessionId, StringComparison.Ordinal) ||
                                !long.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out reportedCreated) ||
                                !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out reportedPid) ||
                                reportedCreated != liveSession.SessionCreatedUnix ||
                                reportedPid != liveSession.ServerPid)
                                continue;
                        }
                        else if (liveSession.SessionCreatedUnix > 0)
                        {
                            long reportedAt;
                            if (!string.Equals(liveSession.SessionName, fields[0], StringComparison.Ordinal) ||
                                fields.Length < 4 ||
                                !long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out reportedAt) ||
                                reportedAt < liveSession.SessionCreatedUnix)
                                continue;
                        }
                        else if (!string.Equals(liveSession.SessionName, fields[0], StringComparison.Ordinal))
                            continue;
                    }
                    int slot = ParseSessionSlot(liveSession == null ? fields[0] : liveSession.SessionName);
                    if (slot < 1 || slot > 6 || ambiguousSlots[slot - 1])
                        continue;
                    bool present;
                    AiTaskState state = ParseState(fields[2], out present);
                    if (!present)
                        continue;
                    long stateRevision = 0;
                    if (fields.Length >= 8)
                        long.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out stateRevision);
                    if (stateRevision <= 0 && fields.Length >= 4)
                    {
                        long reportedAt;
                        if (long.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out reportedAt) &&
                            reportedAt > 0 && reportedAt <= long.MaxValue / 1000000000L)
                            stateRevision = reportedAt * 1000000000L;
                    }

                    TerminalSessionStatusSlot target = result[slot - 1];
                    if (liveSessionByPane == null)
                    {
                        target.Present = true;
                        target.SessionId = "legacy-name:" + fields[0];
                        target.SessionIdentity = target.SessionId;
                        target.SessionName = fields[0];
                    }
                    if (StatePriority(state) > StatePriority(target.State) ||
                        (StatePriority(state) == StatePriority(target.State) && stateRevision >= target.StateRevisionNanoseconds))
                    {
                        target.State = state;
                        target.StateRevisionNanoseconds = stateRevision;
                    }
                }
                catch (IOException)
                {
                    // The WSL hook replaces files atomically. A single skipped poll is harmless.
                }
            }
            return result;
        }

        internal static int ParseSessionSlot(string sessionName)
        {
            // The public convention is an exact two-digit prefix (01..06).
            // Do not silently treat a legacy single-digit name such as "1-old"
            // as slot 01: its LED and switch target would then disagree with the
            // documented live-session namespace.
            if (string.IsNullOrEmpty(sessionName) || sessionName.Length < 2 ||
                !char.IsDigit(sessionName[0]) || !char.IsDigit(sessionName[1]))
                return 0;
            int slot;
            return int.TryParse(
                sessionName.Substring(0, 2),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out slot)
                ? slot
                : 0;
        }

        private static AiTaskState ParseState(string value, out bool present)
        {
            present = true;
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "thinking":
                case "busy":
                case "running":
                    return AiTaskState.Thinking;
                case "completed":
                case "done":
                    return AiTaskState.Completed;
                case "waiting":
                case "permission":
                    return AiTaskState.WaitingForInput;
                case "error":
                case "failed":
                    return AiTaskState.Error;
                case "off":
                case "absent":
                    present = false;
                    return AiTaskState.Idle;
                default:
                    return AiTaskState.Idle;
            }
        }

        private static int StatePriority(AiTaskState state)
        {
            switch (state)
            {
                case AiTaskState.Error:
                    return 5;
                case AiTaskState.WaitingForInput:
                    return 4;
                case AiTaskState.Thinking:
                    return 3;
                case AiTaskState.Completed:
                    return 2;
                default:
                    return 1;
            }
        }

        private static string BuildSignature(IList<TerminalSessionStatusSlot> slots)
        {
            var parts = new string[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                parts[i] = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}:{2}:{3}:{4}:{5}",
                    slots[i].Slot,
                    slots[i].Present ? 1 : 0,
                    (int)slots[i].State,
                    slots[i].SessionId ?? string.Empty,
                    slots[i].SessionIdentity ?? string.Empty,
                    slots[i].SessionName ?? string.Empty);
            }
            return string.Join("|", parts);
        }

        private static string DescribeSlots(IList<TerminalSessionStatusSlot> slots)
        {
            var parts = new string[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                parts[i] = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}={1}{2}",
                    slots[i].Slot,
                    slots[i].Present ? slots[i].State.ToString() : "Off",
                    slots[i].Present && !string.IsNullOrEmpty(slots[i].SessionName)
                        ? "(" + slots[i].SessionName + ")"
                        : string.Empty);
            }
            return string.Join(", ", parts);
        }

        public void Dispose()
        {
            lock (_gate)
                _disposed = true;
            _timer.Dispose();
        }
    }

    internal static class TmuxSessionClient
    {
        private const int ActionCommandTimeoutMs = 1800;
        private const int InventoryCommandTimeoutMs = 5000;

        internal static IList<TerminalPaneSnapshot> ReadPanes()
        {
            string output = RunTmuxWithTimeout(
                InventoryCommandTimeoutMs,
                "list-panes",
                "-a",
                "-F",
                "#{session_id}\t#{session_created}\t#{pid}\t#{session_name}\t#{pane_id}");
            return ParseStrictPanes(output);
        }

        internal static IList<TerminalPaneSnapshot> ParseStrictPanes(string output)
        {
            string[] lines = SplitLines(output);
            if (lines.Length == 0)
                throw new InvalidDataException("tmux returned an empty live pane inventory.");
            var panes = new List<TerminalPaneSnapshot>();
            var seenPaneIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] fields = lines[i].Split('\t');
                long created;
                int serverPid;
                if (fields.Length != 5 ||
                    !IsTmuxId(fields[0], '$') ||
                    !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out created) ||
                    created <= 0 ||
                    !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out serverPid) ||
                    serverPid <= 0 ||
                    string.IsNullOrEmpty(fields[3]) ||
                    !IsTmuxId(fields[4], '%') ||
                    !seenPaneIds.Add(fields[4]))
                    throw new InvalidDataException("tmux returned a malformed live pane inventory.");
                panes.Add(new TerminalPaneSnapshot
                {
                    SessionId = fields[0],
                    SessionIdentity = TerminalSessionIdentity.Build(fields[0], created, serverPid),
                    SessionCreatedUnix = created,
                    ServerPid = serverPid,
                    SessionName = fields[3],
                    PaneId = fields[4]
                });
            }
            return panes;
        }

        internal static IList<TerminalPaneSnapshot> ParsePanes(string output)
        {
            var result = new List<TerminalPaneSnapshot>();
            string[] lines = SplitLines(output);
            for (int i = 0; i < lines.Length; i++)
            {
                string[] fields = lines[i].Split('\t');
                long created;
                int serverPid;
                if (fields.Length < 5 || string.IsNullOrEmpty(fields[0]) ||
                    !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out created) ||
                    !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out serverPid) ||
                    string.IsNullOrEmpty(fields[3]) || string.IsNullOrEmpty(fields[4]))
                    continue;
                result.Add(new TerminalPaneSnapshot
                {
                    SessionId = fields[0],
                    SessionIdentity = TerminalSessionIdentity.Build(fields[0], created, serverPid),
                    SessionCreatedUnix = created,
                    ServerPid = serverPid,
                    SessionName = fields[3],
                    PaneId = fields[4]
                });
            }
            return result;
        }

        internal static bool SwitchToSlot(int slot, out TerminalSessionTarget target)
        {
            target = null;
            if (slot < 1 || slot > 6)
                return false;

            target = ResolveSessionTarget(
                slot,
                SplitLines(RunTmux("list-sessions", "-F", "#{session_id}\t#{session_created}\t#{pid}\t#{session_name}")));
            if (target == null)
            {
                Logger.Info("No unique live tmux session exists for illuminated slot " + slot + ".");
                return false;
            }

            string clientName = SelectMostRecentClient(
                SplitLines(RunTmux("list-clients", "-F", "#{client_activity}\t#{client_name}")));
            if (string.IsNullOrEmpty(clientName))
            {
                Logger.Warn("Unable to switch tmux session because no attached tmux client was found.");
                return false;
            }

            target.SwitchRevisionNanoseconds = SwitchClientWithCutoff(clientName, target);
            Logger.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Switched tmux client {0} to session slot {1} ({2}).",
                clientName,
                slot,
                target.SessionName));
            return true;
        }

        internal static TerminalSessionTarget ResolveSessionTarget(int slot, IList<string> sessionLines)
        {
            TerminalSessionTarget selected = null;
            if (sessionLines == null)
                return null;
            for (int i = 0; i < sessionLines.Count; i++)
            {
                string line = sessionLines[i] ?? string.Empty;
                string[] fields = line.Split('\t');
                long created;
                int serverPid;
                if (fields.Length != 4 || !IsTmuxId(fields[0], '$') ||
                    !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out created) ||
                    created <= 0 ||
                    !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out serverPid) ||
                    serverPid <= 0 ||
                    string.IsNullOrEmpty(fields[3]))
                    continue;
                string sessionId = fields[0];
                string sessionName = fields[3];
                if (TerminalSixSessionStatusMonitor.ParseSessionSlot(sessionName) != slot)
                    continue;
                if (selected != null && !string.Equals(selected.SessionId, sessionId, StringComparison.Ordinal))
                    return null;
                selected = new TerminalSessionTarget
                {
                    Slot = slot,
                    SessionId = sessionId,
                    SessionIdentity = TerminalSessionIdentity.Build(sessionId, created, serverPid),
                    SessionCreatedUnix = created,
                    ServerPid = serverPid,
                    SessionName = sessionName
                };
            }
            return selected;
        }

        internal static string ResolveSessionName(int slot, IList<string> sessionNames)
        {
            if (sessionNames == null)
                return string.Empty;
            string selected = string.Empty;
            for (int i = 0; i < sessionNames.Count; i++)
            {
                string candidate = sessionNames[i] ?? string.Empty;
                if (TerminalSixSessionStatusMonitor.ParseSessionSlot(candidate) == slot)
                {
                    if (!string.IsNullOrEmpty(selected) && !string.Equals(selected, candidate, StringComparison.Ordinal))
                        return string.Empty;
                    selected = candidate;
                }
            }
            return selected;
        }

        internal static string SelectMostRecentClient(IList<string> clientLines)
        {
            string selected = string.Empty;
            long selectedActivity = long.MinValue;
            if (clientLines == null)
                return selected;
            for (int i = 0; i < clientLines.Count; i++)
            {
                string line = clientLines[i] ?? string.Empty;
                int separator = line.IndexOf('\t');
                if (separator <= 0 || separator >= line.Length - 1)
                    continue;
                long activity;
                if (!long.TryParse(
                    line.Substring(0, separator),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out activity))
                    continue;
                if (activity >= selectedActivity)
                {
                    selectedActivity = activity;
                    selected = line.Substring(separator + 1);
                }
            }
            return selected;
        }

        internal static TerminalClientTarget ResolveMostRecentClientTarget(IList<string> clientLines)
        {
            TerminalClientTarget selected = null;
            if (clientLines == null)
                return null;

            for (int i = 0; i < clientLines.Count; i++)
            {
                string[] fields = (clientLines[i] ?? string.Empty).Split('\t');
                long activity;
                if (fields.Length != 5 ||
                    !long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out activity) ||
                    string.IsNullOrEmpty(fields[1]) ||
                    !IsTmuxId(fields[2], '$') ||
                    !IsTmuxId(fields[3], '%') ||
                    !IsTmuxId(fields[4], '@'))
                    continue;

                if (selected == null || activity >= selected.Activity)
                {
                    selected = new TerminalClientTarget
                    {
                        Activity = activity,
                        ClientName = fields[1],
                        SessionId = fields[2],
                        PaneId = fields[3],
                        WindowId = fields[4]
                    };
                }
            }
            return selected;
        }

        internal static TerminalClientTarget ResolveUniqueClientTarget(IList<string> lines, string explicitTty)
        {
            var candidates = new List<TerminalClientTarget>();
            foreach (string line in lines ?? new string[0])
            {
                TerminalClientTarget item = ResolveMostRecentClientTarget(new[] { line });
                if (item != null && (string.IsNullOrEmpty(explicitTty) ||
                    string.Equals(item.ClientName, explicitTty, StringComparison.Ordinal))) candidates.Add(item);
            }
            if (candidates.Count != 1)
                throw new InvalidOperationException("无法唯一对应 tmux 客户端；请只保留一个客户端，或设置 CXT_TMUX_TTY。");
            return candidates[0];
        }

        internal static bool SwitchToIdentity(string identity, out TerminalSessionTarget target)
        {
            target = null;
            foreach (TerminalPaneSnapshot pane in ReadPanes())
            {
                if (!string.Equals(pane.SessionIdentity, identity, StringComparison.Ordinal)) continue;
                target = new TerminalSessionTarget { Slot = TerminalSixSessionStatusMonitor.ParseSessionSlot(pane.SessionName),
                    SessionId = pane.SessionId, SessionIdentity = pane.SessionIdentity,
                    SessionCreatedUnix = pane.SessionCreatedUnix, ServerPid = pane.ServerPid, SessionName = pane.SessionName };
                break;
            }
            if (target == null) return false;
            string client = SelectMostRecentClient(SplitLines(RunTmux("list-clients", "-F", "#{client_activity}\t#{client_name}")));
            if (string.IsNullOrEmpty(client)) return false;
            target.SwitchRevisionNanoseconds = SwitchClientWithCutoff(client, target);
            return true;
        }

        private static bool IsTmuxId(string value, char prefix)
        {
            if (string.IsNullOrEmpty(value) || value.Length < 2 || value[0] != prefix)
                return false;
            for (int i = 1; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9')
                    return false;
            }
            return true;
        }

        internal static IList<string> BuildActiveActionArguments(
            TerminalTmuxAction action,
            TerminalClientTarget target)
        {
            if (target == null || string.IsNullOrEmpty(target.PaneId) || string.IsNullOrEmpty(target.WindowId))
                throw new ArgumentException("A live tmux client pane and window are required.", "target");

            switch (action)
            {
                case TerminalTmuxAction.SelectLeft:
                    return new[] { "select-pane", "-L", "-t", target.PaneId };
                case TerminalTmuxAction.SelectRight:
                    return new[] { "select-pane", "-R", "-t", target.PaneId };
                case TerminalTmuxAction.SelectUp:
                    return new[] { "select-pane", "-U", "-t", target.PaneId };
                case TerminalTmuxAction.SelectDown:
                    return new[] { "select-pane", "-D", "-t", target.PaneId };
                case TerminalTmuxAction.PreviousSession:
                    return new[] { "switch-client", "-c", target.ClientName, "-p" };
                case TerminalTmuxAction.NextSession:
                    return new[] { "switch-client", "-c", target.ClientName, "-n" };
                case TerminalTmuxAction.SplitRight:
                    return new[] { "split-window", "-h", "-c", "#{pane_current_path}", "-t", target.PaneId };
                case TerminalTmuxAction.SplitDown:
                    return new[] { "split-window", "-v", "-c", "#{pane_current_path}", "-t", target.PaneId };
                case TerminalTmuxAction.SelectHorizontal:
                    return new[]
                    {
                        "if-shell", "-F", "-t", target.PaneId, "#{pane_at_right}",
                        "select-pane -L -t " + target.PaneId,
                        "select-pane -R -t " + target.PaneId
                    };
                case TerminalTmuxAction.SelectVertical:
                    return new[]
                    {
                        "if-shell", "-F", "-t", target.PaneId, "#{pane_at_bottom}",
                        "select-pane -U -t " + target.PaneId,
                        "select-pane -D -t " + target.PaneId
                    };
                case TerminalTmuxAction.ToggleZoom:
                    return new[] { "resize-pane", "-Z", "-t", target.PaneId };
                case TerminalTmuxAction.RotatePanes:
                    return new[] { "rotate-window", "-t", target.WindowId };
                case TerminalTmuxAction.ChooseWindow:
                    return new[] { "choose-tree", "-Zw", "-t", target.PaneId };
                case TerminalTmuxAction.StashAgentInput:
                    return new[]
                    {
                        "run-shell", "-b", "-t", target.PaneId,
                        "~/.local/bin/cxt-l2-agent-action stash \"#{pane_id}\" \"#{pane_current_command}\""
                    };
                case TerminalTmuxAction.CopyMode:
                    return new[] { "copy-mode", "-t", target.PaneId };
                case TerminalTmuxAction.LastPane:
                    return new[] { "select-pane", "-l", "-t", target.PaneId };
                case TerminalTmuxAction.ResizeLeft:
                    return new[] { "resize-pane", "-L", "-t", target.PaneId, "5" };
                case TerminalTmuxAction.ResizeRight:
                    return new[] { "resize-pane", "-R", "-t", target.PaneId, "5" };
                case TerminalTmuxAction.EqualizeHorizontal:
                    return new[] { "select-layout", "-t", target.PaneId, "even-horizontal" };
                case TerminalTmuxAction.ResizeUp:
                    return new[] { "resize-pane", "-U", "-t", target.PaneId, "5" };
                case TerminalTmuxAction.ResizeDown:
                    return new[] { "resize-pane", "-D", "-t", target.PaneId, "5" };
                case TerminalTmuxAction.EqualizeVertical:
                    return new[] { "select-layout", "-t", target.PaneId, "even-vertical" };
                case TerminalTmuxAction.PreviousWindow:
                    if (!IsTmuxId(target.SessionId, '$')) throw new ArgumentException("A live tmux session is required.", "target");
                    return new[] { "previous-window", "-t", target.SessionId };
                case TerminalTmuxAction.NextWindow:
                    if (!IsTmuxId(target.SessionId, '$')) throw new ArgumentException("A live tmux session is required.", "target");
                    return new[] { "next-window", "-t", target.SessionId };
                case TerminalTmuxAction.LastWindow:
                    if (!IsTmuxId(target.SessionId, '$')) throw new ArgumentException("A live tmux session is required.", "target");
                    return new[] { "last-window", "-t", target.SessionId };
                default:
                    throw new ArgumentOutOfRangeException("action", action, "Unsupported tmux action.");
            }
        }

        internal static IList<string> BuildToggleOrientationArguments(
            TerminalClientTarget target,
            IList<string> paneLines)
        {
            if (target == null || !IsTmuxId(target.PaneId, '%'))
                throw new ArgumentException("A live tmux pane is required.", "target");
            if (paneLines == null || paneLines.Count < 2)
                return null;

            int firstTop = 0;
            int firstLeft = 0;
            bool sameTop = true;
            bool sameLeft = true;
            for (int i = 0; i < paneLines.Count; i++)
            {
                int top;
                int left;
                if (!TryParsePanePosition(paneLines[i], out top, out left))
                    return null;
                if (i == 0)
                {
                    firstTop = top;
                    firstLeft = left;
                }
                else
                {
                    sameTop = sameTop && top == firstTop;
                    sameLeft = sameLeft && left == firstLeft;
                }
            }

            // tmux calls a left-to-right row even-horizontal and a top-to-
            // bottom column even-vertical. Toggle a row into a column; toggle
            // a column or mixed/tiled layout into a row. Subsequent presses
            // then alternate predictably for every pane count >= 2.
            string layout = sameTop && !sameLeft
                ? "even-vertical"
                : "even-horizontal";
            return new[] { "select-layout", "-t", target.PaneId, layout };
        }

        private static bool TryParsePanePosition(string line, out int top, out int left)
        {
            top = 0;
            left = 0;
            string[] fields = (line ?? string.Empty).Split('\t');
            return fields.Length == 2 &&
                int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out top) && top >= 0 &&
                int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out left) && left >= 0;
        }

        internal static void ExecuteActiveAction(TerminalTmuxAction action)
        {
            TerminalClientTarget target = ResolveUniqueClientTarget(
                SplitLines(RunTmux(
                    "list-clients",
                    "-F",
                    "#{client_activity}\t#{client_name}\t#{session_id}\t#{pane_id}\t#{window_id}")),
                Environment.GetEnvironmentVariable("CXT_TMUX_TTY"));
            if (target == null)
                throw new InvalidOperationException("No attached tmux client with an active pane was found.");

            IList<string> arguments;
            if (action == TerminalTmuxAction.ToggleOrientation)
            {
                arguments = BuildToggleOrientationArguments(
                    target,
                    SplitLines(RunTmux(
                        "list-panes",
                        "-t",
                        target.PaneId,
                        "-F",
                        "#{pane_top}\t#{pane_left}")));
                if (arguments == null)
                {
                    RunTmux("display-message", "-t", target.PaneId, "L2 key6 needs at least two valid panes.");
                    Logger.Info("Direct tmux orientation toggle skipped because the target window contains fewer than two valid panes.");
                    return;
                }
            }
            else
            {
                arguments = BuildActiveActionArguments(action, target);
            }
            RunTmux(new List<string>(arguments).ToArray());
            Logger.Info(string.Format(
                CultureInfo.InvariantCulture,
                "Executed direct tmux action {0} for client {1}, session {2}, window {3}, pane {4}; Windows IME bypassed.",
                action,
                target.ClientName,
                target.SessionId,
                target.WindowId,
                target.PaneId));
        }

        internal static IList<string> BuildGuardedSwitchCommand(string clientName, TerminalSessionTarget target)
        {
            if (target == null || !IsTmuxId(target.SessionId, '$') ||
                target.SessionCreatedUnix <= 0 || target.ServerPid <= 0)
                throw new ArgumentException("An exact live tmux session identity is required.", "target");
            string quotedClient = QuoteTmuxCommandArgument(clientName);
            string identityCondition = "#{&&:#{==:#{session_id}," + target.SessionId +
                "},#{&&:#{==:#{session_created}," + target.SessionCreatedUnix.ToString(CultureInfo.InvariantCulture) +
                "},#{==:#{pid}," + target.ServerPid.ToString(CultureInfo.InvariantCulture) + "}}}";

            // if-shell -F evaluates a format synchronously inside the server.
            // Its selected command list is inserted into the same command queue
            // without yielding, so no other client can replace the session
            // between this identity check and switch-client. Do not replace it
            // with a shell-side display-message check followed by another tmux
            // process: that would reintroduce the server/PID reuse race.
            string onMatch = "switch-client -c " + quotedClient + " -t " +
                QuoteTmuxCommandArgument(target.SessionId) +
                " ; display-message -p CXT-TMUX-SWITCHED";
            // The agent-status helper revisions also come from GNU date inside
            // WSL. Capture the cutoff in that same clock domain immediately
            // before tmux executes the switch; Windows and WSL wall clocks can
            // temporarily diverge after sleep/resume or time correction.
            var command = new List<string>();
            command.Add("--exec");
            command.Add("bash");
            command.Add("-c");
            command.Add("cutoff=$(date +%s%N) || exit 70; printf '%s\\n' \"$cutoff\"; exec tmux if-shell -F -t \"$1\" \"$2\" \"$3\" \"$4\"");
            command.Add("cxt12e4-switch");
            command.Add(target.SessionId);
            command.Add(identityCondition);
            command.Add(onMatch);
            command.Add("display-message -p CXT-TMUX-IDENTITY-MISMATCH");
            return command;
        }

        internal static string QuoteTmuxCommandArgument(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new ArgumentException("A single-line tmux argument is required.", "value");
            // tmux command-string quoting, not Windows argv or shell quoting.
            // Close single quotes, emit an escaped quote, then reopen them.
            // All other characters (including $, ; and backslashes) stay literal.
            return "'" + value.Replace("'", "'\\''") + "'";
        }

        private static long SwitchClientWithCutoff(string clientName, TerminalSessionTarget target)
        {
            string output = RunWslCommand(BuildGuardedSwitchCommand(clientName, target), "tmux switch-client");
            return ParseGuardedSwitchResult(output);
        }

        internal static long ParseGuardedSwitchResult(string output)
        {
            string[] lines = SplitLines(output);
            long cutoff;
            if (lines.Length == 2 && string.Equals(lines[1], "CXT-TMUX-IDENTITY-MISMATCH", StringComparison.Ordinal))
                throw new InvalidOperationException("The tmux session identity changed before switch-client; no session was switched or acknowledged.");
            if (lines.Length != 2 ||
                !string.Equals(lines[1], "CXT-TMUX-SWITCHED", StringComparison.Ordinal) ||
                !long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out cutoff) ||
                cutoff <= 0)
                throw new InvalidOperationException("tmux switch-client did not confirm a successful guarded switch and valid WSL cutoff revision.");
            return cutoff;
        }

        private static string RunTmux(params string[] arguments)
        {
            return RunTmuxWithTimeout(ActionCommandTimeoutMs, arguments);
        }

        private static string RunTmuxWithTimeout(int timeoutMilliseconds, params string[] arguments)
        {
            var command = new List<string>();
            command.Add("--exec");
            command.Add("tmux");
            if (arguments != null)
                command.AddRange(arguments);

            return RunWslCommand(command, "tmux command", timeoutMilliseconds);
        }

        private static string RunWslCommand(IList<string> command, string operation)
        {
            return RunWslCommand(command, operation, ActionCommandTimeoutMs);
        }

        private static string RunWslCommand(IList<string> command, string operation, int timeoutMilliseconds)
        {
            var start = new ProcessStartInfo();
            start.FileName = "wsl.exe";
            start.Arguments = BuildArgumentString(command);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;

            using (Process process = Process.Start(start))
            {
                if (process == null)
                    throw new InvalidOperationException("Unable to start wsl.exe for " + operation + ".");
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try { process.Kill(); }
                    catch { }
                    try { process.WaitForExit(1000); }
                    catch { }
                    ObserveAsyncReadFailure(outputTask);
                    ObserveAsyncReadFailure(errorTask);
                    throw new TimeoutException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} exceeded {1}ms.",
                        operation,
                        timeoutMilliseconds));
                }
                string output = outputTask.Result;
                string error = errorTask.Result.Trim();
                if (process.ExitCode != 0)
                {
                    if (IsExplicitNoServerError(error))
                        throw new TmuxServerUnavailableException(error);
                    throw new InvalidOperationException(string.IsNullOrEmpty(error)
                        ? operation + " exited with code " + process.ExitCode + "."
                        : error);
                }
                return output;
            }
        }

        private static void ObserveAsyncReadFailure(System.Threading.Tasks.Task<string> task)
        {
            if (task == null)
                return;
            task.ContinueWith(
                completed => GC.KeepAlive(completed.Exception),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted |
                System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
        }

        internal static bool IsExplicitNoServerError(string error)
        {
            if (string.IsNullOrEmpty(error))
                return false;
            string normalized = error.ToLowerInvariant();
            bool endpointUnavailable = normalized.Contains("no such file or directory") ||
                normalized.Contains("connection refused");
            return normalized.Contains("no server running") ||
                endpointUnavailable &&
                (normalized.Contains("failed to connect to server") || normalized.Contains("error connecting to"));
        }

        private static string[] SplitLines(string value)
        {
            return (value ?? string.Empty).Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);
        }

        private static string BuildArgumentString(IList<string> arguments)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                    builder.Append(' ');
                builder.Append(QuoteArgument(arguments[i]));
            }
            return builder.ToString();
        }

        private static string QuoteArgument(string value)
        {
            value = value ?? string.Empty;
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return value;

            var builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }
                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(character);
            }
            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }
    }

    internal static class TerminalWindowActivator
    {
        internal static bool FocusWindow(IntPtr preferredWindow)
        {
            IntPtr selected = IsTerminalWindow(preferredWindow)
                ? preferredWindow
                : IntPtr.Zero;
            string[] processNames = { "WindowsTerminal", "wt" };
            for (int nameIndex = 0; selected == IntPtr.Zero && nameIndex < processNames.Length; nameIndex++)
            {
                Process[] processes = Process.GetProcessesByName(processNames[nameIndex]);
                for (int processIndex = 0; processIndex < processes.Length; processIndex++)
                {
                    using (processes[processIndex])
                    {
                        IntPtr candidate = processes[processIndex].MainWindowHandle;
                        if (candidate != IntPtr.Zero && NativeMethods.IsWindow(candidate))
                        {
                            selected = candidate;
                            break;
                        }
                    }
                }
                if (selected != IntPtr.Zero)
                    break;
            }

            if (selected == IntPtr.Zero)
            {
                Logger.Warn("tmux session switched, but no Windows Terminal window was available to focus.");
                return false;
            }

            // A bare SetForegroundWindow call is frequently rejected when the
            // long-press originates in another application (notably ChatGPT).
            // Use the same verified activation path as ChatGPT switching: it
            // restores the window, temporarily joins the relevant input queues,
            // and uses the private Alt fallback when Windows' foreground lock
            // still blocks the first request.
            bool focused = ForegroundWindowActivator.Activate(selected);
            if (!focused)
                Logger.Warn("tmux session switched, but Windows rejected the foreground activation request.");
            else
                Logger.Info("Windows Terminal foreground activation confirmed after tmux switch.");
            return focused;
        }

        private static bool IsTerminalWindow(IntPtr window)
        {
            if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
                return false;
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            if (processId == 0)
                return false;
            try
            {
                using (Process process = Process.GetProcessById(unchecked((int)processId)))
                    return IsTerminalProcessName(process.ProcessName);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsTerminalProcessName(string processName)
        {
            return string.Equals(processName, "WindowsTerminal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(processName, "wt", StringComparison.OrdinalIgnoreCase);
        }
    }
}
