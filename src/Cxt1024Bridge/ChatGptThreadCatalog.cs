using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Cxt1024Bridge
{
    internal sealed class ChatGptThreadSlot
    {
        internal string Id;
        internal string Title;
        internal long RecencyAtMilliseconds;
        internal string RolloutPath;
        internal string RuntimeStatus;

        internal ChatGptThreadSlot Clone()
        {
            return new ChatGptThreadSlot
            {
                Id = Id,
                Title = Title,
                RecencyAtMilliseconds = RecencyAtMilliseconds,
                RolloutPath = RolloutPath,
                RuntimeStatus = RuntimeStatus
            };
        }
    }

    // Keeps the six physical task keys stable. Opening an already assigned task
    // changes its catalog recency but must never move it to another physical key.
    // A genuinely new/unassigned foreground task may replace only the least-recent
    // assigned slot.
    internal sealed class ChatGptTaskSlotRegistry
    {
        private const int SlotCount = 6;
        private const int RefreshCandidateCount = 32;
        private readonly object _gate = new object();
        private readonly ChatGptThreadSlot[] _slots = new ChatGptThreadSlot[SlotCount];
        private readonly Func<int, List<ChatGptThreadSlot>> _readRecent;
        private bool _initialized;

        internal ChatGptTaskSlotRegistry()
            : this(ChatGptThreadCatalog.ReadRecent)
        {
        }

        internal ChatGptTaskSlotRegistry(Func<int, List<ChatGptThreadSlot>> readRecent)
        {
            if (readRecent == null)
                throw new ArgumentNullException("readRecent");
            _readRecent = readRecent;
        }

        internal List<ChatGptThreadSlot> Snapshot()
        {
            List<ChatGptThreadSlot> candidates = _readRecent(RefreshCandidateCount);
            lock (_gate)
            {
                RefreshLocked(candidates);
                var result = new List<ChatGptThreadSlot>(SlotCount);
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i] != null)
                        result.Add(_slots[i].Clone());
                return result;
            }
        }

        internal ChatGptThreadSlot GetSlot(int slot)
        {
            if (slot < 1 || slot > SlotCount)
                throw new ArgumentOutOfRangeException("slot");
            Snapshot();
            lock (_gate)
                return _slots[slot - 1] == null ? null : _slots[slot - 1].Clone();
        }

        internal static ChatGptThreadSlot[] ReconcileForTest(
            ChatGptThreadSlot[] assigned,
            IList<ChatGptThreadSlot> candidates,
            bool initialized)
        {
            var registry = new ChatGptTaskSlotRegistry();
            for (int i = 0; i < Math.Min(SlotCount, assigned == null ? 0 : assigned.Length); i++)
                registry._slots[i] = assigned[i] == null ? null : assigned[i].Clone();
            registry._initialized = initialized;
            registry.RefreshLocked(candidates == null
                ? new List<ChatGptThreadSlot>()
                : new List<ChatGptThreadSlot>(candidates));
            var result = new ChatGptThreadSlot[SlotCount];
            for (int i = 0; i < result.Length; i++)
                result[i] = registry._slots[i] == null ? null : registry._slots[i].Clone();
            return result;
        }

        private void RefreshLocked(IList<ChatGptThreadSlot> candidates)
        {
            var byId = new Dictionary<string, ChatGptThreadSlot>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < candidates.Count; i++)
            {
                ChatGptThreadSlot candidate = candidates[i];
                if (candidate != null && !string.IsNullOrWhiteSpace(candidate.Id))
                    byId[candidate.Id] = candidate;
            }

            var assignedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _slots.Length; i++)
            {
                ChatGptThreadSlot current = _slots[i];
                if (current == null)
                    continue;
                ChatGptThreadSlot updated;
                if (byId.TryGetValue(current.Id, out updated))
                    _slots[i] = updated.Clone();
                assignedIds.Add(current.Id);
            }

            // Initial assignment follows the current recent-task order. Empty slots
            // are also filled without disturbing any established physical mapping.
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] != null)
                    continue;
                ChatGptThreadSlot next = FindNewestUnassigned(candidates, assignedIds);
                if (next == null)
                    break;
                _slots[i] = next.Clone();
                assignedIds.Add(next.Id);
            }

            if (_initialized && AllSlotsFilled())
            {
                ChatGptThreadSlot newestOutside = FindNewestUnassigned(candidates, assignedIds);
                int oldestIndex = FindOldestSlotIndex();
                if (newestOutside != null && oldestIndex >= 0 &&
                    newestOutside.RecencyAtMilliseconds > _slots[oldestIndex].RecencyAtMilliseconds)
                {
                    Logger.Info(
                        "ChatGPT stable task slot " + (oldestIndex + 1) + " adopted " +
                        ShortId(newestOutside.Id) + " (" + newestOutside.Title + "); previous task was " +
                        ShortId(_slots[oldestIndex].Id) + ".");
                    _slots[oldestIndex] = newestOutside.Clone();
                }
            }
            _initialized = true;
        }

        private static ChatGptThreadSlot FindNewestUnassigned(
            IList<ChatGptThreadSlot> candidates,
            HashSet<string> assignedIds)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                ChatGptThreadSlot candidate = candidates[i];
                if (candidate != null && !string.IsNullOrWhiteSpace(candidate.Id) &&
                    !assignedIds.Contains(candidate.Id))
                    return candidate;
            }
            return null;
        }

        private bool AllSlotsFilled()
        {
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i] == null)
                    return false;
            return true;
        }

        private int FindOldestSlotIndex()
        {
            int selected = -1;
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i] != null && (selected < 0 ||
                    _slots[i].RecencyAtMilliseconds < _slots[selected].RecencyAtMilliseconds))
                    selected = i;
            return selected;
        }

        private static string ShortId(string id)
        {
            return string.IsNullOrEmpty(id) ? "unknown" : id.Substring(0, Math.Min(8, id.Length));
        }
    }

    internal static class ChatGptThreadCatalog
    {
        private const int SqliteOk = 0;
        private const int SqliteRow = 100;
        private const int SqliteDone = 101;
        private const int SqliteOpenReadOnly = 0x00000001;
        private static readonly object RolloutCacheGate = new object();
        private static Dictionary<string, string> _rolloutCache;
        private static string _rolloutCacheKey;
        private static DateTime _rolloutCacheBuiltUtc;

        internal static List<ChatGptThreadSlot> ReadRecent(int limit)
        {
            if (limit < 1)
                throw new ArgumentOutOfRangeException("limit");
            string databasePath = FindStateDatabase();
            if (databasePath == null)
                return new List<ChatGptThreadSlot>();

            IntPtr database = IntPtr.Zero;
            IntPtr statement = IntPtr.Zero;
            try
            {
                int openResult = sqlite3_open_v2(databasePath, out database, SqliteOpenReadOnly, IntPtr.Zero);
                if (openResult != SqliteOk)
                    throw new InvalidOperationException("Unable to open the ChatGPT thread catalog: " + GetError(database));
                sqlite3_busy_timeout(database, 500);

                HashSet<string> columns = ReadThreadColumns(database);
                string sql = BuildReadRecentSql(columns, limit);
                int prepareResult = sqlite3_prepare_v2(database, sql, -1, out statement, IntPtr.Zero);
                if (prepareResult != SqliteOk)
                    throw new InvalidOperationException("Unable to query the ChatGPT thread catalog: " + GetError(database));

                var result = new List<ChatGptThreadSlot>();
                while (true)
                {
                    int step = sqlite3_step(statement);
                    if (step == SqliteDone)
                        break;
                    if (step != SqliteRow)
                        throw new InvalidOperationException("Unable to read the ChatGPT thread catalog: " + GetError(database));
                    string id = ReadUtf8Column(statement, 0);
                    Guid parsed;
                    if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out parsed))
                        continue;
                    string rolloutPath = NormalizeFileSystemPath(ReadUtf8Column(statement, 3));
                    if (string.IsNullOrWhiteSpace(rolloutPath) || !RolloutFileExists(rolloutPath))
                        rolloutPath = FindRolloutPath(id);
                    result.Add(new ChatGptThreadSlot
                    {
                        Id = id,
                        Title = ReadUtf8Column(statement, 1),
                        RecencyAtMilliseconds = sqlite3_column_int64(statement, 2),
                        RolloutPath = rolloutPath
                    });
                }
                return result;
            }
            finally
            {
                if (statement != IntPtr.Zero)
                    sqlite3_finalize(statement);
                if (database != IntPtr.Zero)
                    sqlite3_close(database);
            }
        }

        private static string FindStateDatabase()
        {
            List<string> homes = CodexHomeCandidates();
            for (int i = 0; i < homes.Count; i++)
            {
                string database = FindStateDatabaseInHome(homes[i]);
                if (!string.IsNullOrWhiteSpace(database))
                    return database;
            }
            return null;
        }

        private static string FindStateDatabaseInHome(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return null;
            string[] files = Directory.GetFiles(root, "state_*.sqlite", SearchOption.TopDirectoryOnly);
            string selected = null;
            int selectedVersion = -1;
            bool selectedExact = false;
            DateTime selectedWriteUtc = DateTime.MinValue;
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                int separator = name.LastIndexOf('_');
                int version;
                if (separator < 0 || !int.TryParse(name.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
                    continue;
                bool exact = string.Equals(
                    Path.GetFileName(files[i]),
                    "state_" + version.ToString(CultureInfo.InvariantCulture) + ".sqlite",
                    StringComparison.OrdinalIgnoreCase);
                DateTime writeUtc = File.GetLastWriteTimeUtc(files[i]);
                if (version > selectedVersion ||
                    (version == selectedVersion && exact && !selectedExact) ||
                    (version == selectedVersion && exact == selectedExact && writeUtc > selectedWriteUtc))
                {
                    selected = files[i];
                    selectedVersion = version;
                    selectedExact = exact;
                    selectedWriteUtc = writeUtc;
                }
            }
            return selected;
        }

        internal static string NormalizeFileSystemPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            string trimmed = path.Trim();
            const string extendedUncPrefix = @"\\?\UNC\";
            const string extendedPrefix = @"\\?\";
            if (trimmed.StartsWith(extendedUncPrefix, StringComparison.OrdinalIgnoreCase))
                return @"\\" + trimmed.Substring(extendedUncPrefix.Length);
            if (trimmed.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring(extendedPrefix.Length);
            return trimmed;
        }

        internal static bool RolloutFileExists(string path)
        {
            string normalized = NormalizeFileSystemPath(path);
            return !string.IsNullOrWhiteSpace(normalized) && File.Exists(normalized);
        }

        internal static List<string> BuildCodexHomeCandidatesForTest(string codexHome, string userProfile)
        {
            return BuildCodexHomeCandidates(codexHome, userProfile);
        }

        internal static List<string> CodexHomeCandidatesForProbe()
        {
            return CodexHomeCandidates();
        }

        private static List<string> CodexHomeCandidates()
        {
            return BuildCodexHomeCandidates(
                Environment.GetEnvironmentVariable("CODEX_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        private static List<string> BuildCodexHomeCandidates(string codexHome, string userProfile)
        {
            var result = new List<string>();
            AddCodexHomeCandidate(result, codexHome);
            if (!string.IsNullOrWhiteSpace(userProfile))
                AddCodexHomeCandidate(result, Path.Combine(userProfile, ".codex"));
            return result;
        }

        private static void AddCodexHomeCandidate(List<string> result, string path)
        {
            if (result == null || string.IsNullOrWhiteSpace(path))
                return;
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(expanded);
            }
            catch
            {
                return;
            }
            for (int i = 0; i < result.Count; i++)
                if (string.Equals(result[i], fullPath, StringComparison.OrdinalIgnoreCase))
                    return;
            result.Add(fullPath);
        }

        internal static string BuildReadRecentSqlForTest(params string[] columnNames)
        {
            return BuildReadRecentSql(
                new HashSet<string>(columnNames ?? new string[0], StringComparer.OrdinalIgnoreCase),
                6);
        }

        private static HashSet<string> ReadThreadColumns(IntPtr database)
        {
            IntPtr statement = IntPtr.Zero;
            try
            {
                int prepareResult = sqlite3_prepare_v2(database, "PRAGMA table_info(threads)", -1, out statement, IntPtr.Zero);
                if (prepareResult != SqliteOk)
                    throw new InvalidOperationException("Unable to inspect the ChatGPT thread catalog schema: " + GetError(database));

                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    int step = sqlite3_step(statement);
                    if (step == SqliteDone)
                        break;
                    if (step != SqliteRow)
                        throw new InvalidOperationException("Unable to read the ChatGPT thread catalog schema: " + GetError(database));
                    string name = ReadUtf8Column(statement, 1);
                    if (!string.IsNullOrWhiteSpace(name))
                        columns.Add(name);
                }
                return columns;
            }
            finally
            {
                if (statement != IntPtr.Zero)
                    sqlite3_finalize(statement);
            }
        }

        private static string BuildReadRecentSql(HashSet<string> columns, int limit)
        {
            if (columns == null || !columns.Contains("id"))
                throw new InvalidOperationException("ChatGPT thread catalog is missing required id column.");

            string titleExpression = CoalesceTextExpression(columns, "name", "title", "preview");
            string recencyExpression = TimestampMillisecondsExpression(
                columns,
                "recency_at_ms",
                "recency_at",
                "updated_at_ms",
                "updated_at",
                "created_at_ms",
                "created_at");
            string updatedExpression = TimestampMillisecondsExpression(
                columns,
                "updated_at_ms",
                "updated_at",
                "created_at_ms",
                "created_at");
            string rolloutExpression = columns.Contains("rollout_path")
                ? "COALESCE(rollout_path,'')"
                : "''";
            string whereClause = BuildWhereClause(columns);

            return "SELECT id, " + titleExpression + ", " + recencyExpression + ", " +
                rolloutExpression + " " +
                "FROM threads " +
                whereClause +
                "ORDER BY " + recencyExpression + " DESC, " + updatedExpression + " DESC, id DESC LIMIT " +
                limit.ToString(CultureInfo.InvariantCulture);
        }

        private static string BuildWhereClause(HashSet<string> columns)
        {
            var clauses = new List<string>();
            if (columns.Contains("archived"))
                clauses.Add("archived = 0");
            if (columns.Contains("thread_source"))
                clauses.Add("(thread_source IS NULL OR thread_source <> 'subagent')");
            return clauses.Count == 0
                ? string.Empty
                : "WHERE " + string.Join(" AND ", clauses.ToArray()) + " ";
        }

        private static string CoalesceTextExpression(HashSet<string> columns, params string[] candidates)
        {
            var parts = new List<string>();
            for (int i = 0; i < candidates.Length; i++)
                if (columns.Contains(candidates[i]))
                    parts.Add("NULLIF(" + candidates[i] + ",'')");
            parts.Add("id");
            return "COALESCE(" + string.Join(", ", parts.ToArray()) + ")";
        }

        private static string TimestampMillisecondsExpression(
            HashSet<string> columns,
            string millisecondColumn,
            string secondColumn,
            params string[] fallbackColumns)
        {
            if (columns.Contains(millisecondColumn))
                return millisecondColumn;
            if (columns.Contains(secondColumn))
                return "(" + secondColumn + " * 1000)";
            for (int i = 0; i + 1 < fallbackColumns.Length; i += 2)
            {
                if (columns.Contains(fallbackColumns[i]))
                    return fallbackColumns[i];
                if (columns.Contains(fallbackColumns[i + 1]))
                    return "(" + fallbackColumns[i + 1] + " * 1000)";
            }
            return "0";
        }

        private static string FindRolloutPath(string threadId)
        {
            List<string> sessionsRoots = CodexSessionRoots();
            if (sessionsRoots.Count == 0)
                return string.Empty;
            string cacheKey = string.Join("|", sessionsRoots.ToArray());
            lock (RolloutCacheGate)
            {
                if (_rolloutCache == null ||
                    !string.Equals(_rolloutCacheKey, cacheKey, StringComparison.OrdinalIgnoreCase) ||
                    DateTime.UtcNow - _rolloutCacheBuiltUtc > TimeSpan.FromSeconds(30))
                {
                    RebuildRolloutCache(sessionsRoots, cacheKey);
                }
                string path;
                if (_rolloutCache.TryGetValue(threadId, out path))
                    return path;

                // A newly created task can appear in SQLite before the periodic cache
                // refresh. Refresh once immediately when an ID is missing.
                RebuildRolloutCache(sessionsRoots, cacheKey);
                return _rolloutCache.TryGetValue(threadId, out path) ? path : string.Empty;
            }
        }

        private static List<string> CodexSessionRoots()
        {
            List<string> homes = CodexHomeCandidates();
            var roots = new List<string>();
            for (int i = 0; i < homes.Count; i++)
            {
                string sessionsRoot = Path.Combine(homes[i], "sessions");
                if (Directory.Exists(sessionsRoot))
                    roots.Add(sessionsRoot);
            }
            return roots;
        }

        private static void RebuildRolloutCache(IList<string> sessionsRoots, string cacheKey)
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var writeTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            for (int rootIndex = 0; rootIndex < sessionsRoots.Count; rootIndex++)
            {
                string[] files = Directory.GetFiles(sessionsRoots[rootIndex], "rollout-*.jsonl", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(files[i]);
                    if (name.Length < 36)
                        continue;
                    string possibleId = name.Substring(name.Length - 36);
                    Guid parsed;
                    if (!Guid.TryParse(possibleId, out parsed))
                        continue;
                    DateTime writeUtc = File.GetLastWriteTimeUtc(files[i]);
                    DateTime existingWriteUtc;
                    if (!writeTimes.TryGetValue(possibleId, out existingWriteUtc) ||
                        writeUtc > existingWriteUtc)
                    {
                        cache[possibleId] = files[i];
                        writeTimes[possibleId] = writeUtc;
                    }
                }
            }
            _rolloutCache = cache;
            _rolloutCacheKey = cacheKey;
            _rolloutCacheBuiltUtc = DateTime.UtcNow;
        }

        private static string ReadUtf8Column(IntPtr statement, int column)
        {
            IntPtr value = sqlite3_column_text(statement, column);
            if (value == IntPtr.Zero)
                return string.Empty;
            int length = sqlite3_column_bytes(statement, column);
            if (length <= 0)
                return string.Empty;
            var bytes = new byte[length];
            Marshal.Copy(value, bytes, 0, length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        private static string GetError(IntPtr database)
        {
            if (database == IntPtr.Zero)
                return "unknown SQLite error";
            IntPtr message = sqlite3_errmsg(database);
            if (message == IntPtr.Zero)
                return "unknown SQLite error";
            int length = 0;
            while (Marshal.ReadByte(message, length) != 0)
                length++;
            var bytes = new byte[length];
            Marshal.Copy(message, bytes, 0, length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_open_v2(string filename, out IntPtr database, int flags, IntPtr virtualFileSystem);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_prepare_v2(IntPtr database, string sql, int byteCount, out IntPtr statement, IntPtr tail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr database);
    }
}
