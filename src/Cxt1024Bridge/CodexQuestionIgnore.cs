using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Cxt1024Bridge
{
    internal sealed class CodexPendingQuestion
    {
        internal string TurnId = "", CallId = "", Title = "";
        internal int Index;
        internal bool Ignored;
        internal string Key { get { return Identity(TurnId, CallId, Index); } }
        internal static string Identity(string turn, string call, int index)
        {
            return turn.Length.ToString(CultureInfo.InvariantCulture) + ":" + turn +
                call.Length.ToString(CultureInfo.InvariantCulture) + ":" + call + ":" + index.ToString(CultureInfo.InvariantCulture);
        }
    }

    // Separate from completion acknowledgement. Only explicit Bridge UI actions write this store.
    internal sealed class CodexQuestionIgnoreStore
    {
        internal sealed class Entry
        {
            public string ThreadId { get; set; }
            public string TurnId { get; set; }
            public string CallId { get; set; }
            public int Index { get; set; }
        }
        internal sealed class Document
        {
            public int Version { get; set; }
            public List<Entry> Entries { get; set; }
        }
        private readonly object _gate = new object();
        private readonly string _path;
        private List<Entry> _entries = new List<Entry>();
        internal string LoadError { get; private set; }
        private static readonly Lazy<CodexQuestionIgnoreStore> DefaultStore = new Lazy<CodexQuestionIgnoreStore>(() =>
            new CodexQuestionIgnoreStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cxt1024Bridge", "codex-ignored-questions.json")));
        internal static CodexQuestionIgnoreStore Default { get { return DefaultStore.Value; } }
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 32 }; }
        internal CodexQuestionIgnoreStore(string path)
        {
            _path = path; LoadError = "";
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var doc = Json().Deserialize<Document>(File.ReadAllText(path, Encoding.UTF8));
                if (doc == null || doc.Version != 1 || doc.Entries == null) throw new InvalidDataException("忽略记录格式不受支持");
                foreach (var entry in doc.Entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.ThreadId) || string.IsNullOrEmpty(entry.TurnId) || string.IsNullOrEmpty(entry.CallId) || entry.Index < 0)
                        throw new InvalidDataException("忽略记录缺少提问身份");
                }
                _entries = doc.Entries;
            }
            catch (Exception ex) { LoadError = "忽略记录读取失败；未应用记录：" + ex.Message; }
        }
        internal HashSet<string> ForThread(string thread)
        {
            lock (_gate)
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in _entries) if (entry.ThreadId == thread) result.Add(CodexPendingQuestion.Identity(entry.TurnId, entry.CallId, entry.Index));
                return result;
            }
        }
        internal void Set(string thread, CodexPendingQuestion question, bool ignore)
        {
            if (string.IsNullOrEmpty(thread) || question == null || string.IsNullOrEmpty(question.TurnId) || string.IsNullOrEmpty(question.CallId) || question.Index < 0)
                throw new ArgumentException("无效的提问身份");
            lock (_gate)
            {
                if (LoadError.Length != 0) throw new IOException(LoadError);
                var next = new List<Entry>();
                foreach (var entry in _entries)
                    if (entry.ThreadId != thread || (entry.TurnId == question.TurnId && (entry.CallId != question.CallId || entry.Index != question.Index))) next.Add(entry);
                if (ignore) next.Add(new Entry { ThreadId = thread, TurnId = question.TurnId, CallId = question.CallId, Index = question.Index });
                Save(next); // Do not alter in-memory LEDs if persistence fails.
                _entries = next;
            }
        }
        private void Save(List<Entry> entries)
        {
            if (string.IsNullOrEmpty(_path)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
            string temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, Json().Serialize(new Document { Version = 1, Entries = entries }), new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal sealed class CodexQuestionRow
    {
        internal string ThreadId, TaskTitle;
        internal int Key;
        internal CodexPendingQuestion Question;
        internal string Identity { get { return ThreadId + ":" + Question.Key; } }
    }
    internal sealed class CodexQuestionService
    {
        private readonly CodexQuestionIgnoreStore _store;
        private readonly Func<IList<ChatGptThreadSlot>> _threads;
        internal CodexQuestionService(CodexQuestionIgnoreStore store, Func<IList<ChatGptThreadSlot>> threads) { _store = store; _threads = threads; }
        internal List<CodexQuestionRow> Read()
        {
            if (_store.LoadError.Length != 0) throw new IOException(_store.LoadError);
            var result = new List<CodexQuestionRow>();
            var threads = _threads(); int[] keys = { 1, 2, 3, 5, 6, 7 };
            for (int i = 0; i < Math.Min(threads.Count, keys.Length); i++)
            {
                var thread = threads[i];
                if (thread == null || string.IsNullOrEmpty(thread.Id)) continue;
                var state = CodexRolloutState.ReadFile(ChatGptThreadCatalog.NormalizeFileSystemPath(thread.RolloutPath), _store.ForThread(thread.Id), true);
                foreach (var question in state.Questions)
                    result.Add(new CodexQuestionRow { ThreadId = thread.Id, TaskTitle = string.IsNullOrEmpty(thread.Title) ? "未命名任务" : thread.Title, Key = keys[i], Question = question });
            }
            return result;
        }
        internal void Set(CodexQuestionRow selected, bool ignore)
        {
            // Re-read the exact question, not the former slot or a title match.
            if (selected == null) throw new ArgumentException("请选择具体提问");
            var current = Read().Find(row => row.Identity == selected.Identity);
            if (current == null) throw new InvalidOperationException("提问已结束或已更新，请刷新后重试");
            _store.Set(current.ThreadId, current.Question, ignore);
        }
    }
}
