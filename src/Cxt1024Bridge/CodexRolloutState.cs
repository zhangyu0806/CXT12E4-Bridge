using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace Cxt1024Bridge
{
    // Read actual JSON records, never marker-looking text inside messages or tools.
    internal sealed class CodexRolloutState
    {
        internal AiTaskState State = AiTaskState.Idle;
        internal string Evidence = "no-task-marker";
        internal string CompletionRevision = string.Empty;

        private sealed class Record
        {
            internal Dictionary<string, object> Value;
            internal long Offset;
        }

        internal static CodexRolloutState ReadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new CodexRolloutState { Evidence = "no-rollout" };
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
                return Read(stream, path, WindowsBootTime());
        }

        internal static CodexRolloutState ReadText(string text)
        {
            return ReadText(text, null);
        }

        internal static CodexRolloutState ReadText(string text, DateTimeOffset? bootTime)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text ?? string.Empty)))
                return Read(stream, string.Empty, bootTime);
        }

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        private static DateTimeOffset? WindowsBootTime()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            try { return DateTimeOffset.UtcNow.AddMilliseconds(-(double)GetTickCount64()); }
            catch (DllNotFoundException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static CodexRolloutState Read(Stream stream, string path, DateTimeOffset? bootTime)
        {
            var records = new List<Record>();
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 128 };
            bool local = IsLocalRollout(stream, json);
            // The latest start establishes the active turn. Terminal records for
            // another turn cannot override it, even when delivered late.
            foreach (Record record in ReadBackwards(stream, json))
            {
                records.Add(record);
                var payload = Object(record.Value, "payload");
                if (Text(record.Value, "type") == "event_msg" && Text(payload, "type") == "task_started")
                    break;
            }
            records.Reverse();
            var result = new CodexRolloutState();
            var pending = new HashSet<string>(StringComparer.Ordinal);
            var asyncPending = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            string activeTurn = string.Empty;
            bool authoritativeEnd = false;
            DateTimeOffset? started = null, activity = null;
            bool unknownTime = false;
            foreach (Record record in records)
            {
                var obj = record.Value;
                var payload = Object(obj, "payload");
                if (payload == null) continue;
                string outer = Text(obj, "type"), kind = Text(payload, "type");
                string turn = TurnId(payload);
                if (turn.Length == 0) turn = TurnId(obj);
                if (outer == "event_msg" && kind == "task_started")
                {
                    activeTurn = turn;
                    authoritativeEnd = false;
                    started = Timestamp(obj);
                    activity = started;
                    unknownTime = !started.HasValue;
                    result.State = AiTaskState.Thinking;
                    result.Evidence = "task_started";
                    result.CompletionRevision = string.Empty;
                    pending.Clear();
                    asyncPending.Clear();
                    continue;
                }
                if (activeTurn.Length != 0 && turn.Length != 0 && activeTurn != turn) continue;
                if (outer == "response_item" || (outer == "event_msg" && kind != "thread_settings_applied"))
                {
                    DateTimeOffset? timestamp = Timestamp(obj);
                    if (!timestamp.HasValue) unknownTime = true;
                    else if (!activity.HasValue || timestamp.Value > activity.Value) activity = timestamp;
                }
                bool complete = false;
                if (outer == "event_msg")
                {
                    if (kind == "task_complete")
                    {
                        authoritativeEnd = true;
                        if (HasError(payload))
                        {
                            result.State = AiTaskState.Error;
                            result.Evidence = "task_complete-error";
                            result.CompletionRevision = string.Empty;
                            pending.Clear();
                            asyncPending.Clear();
                        }
                        else complete = true;
                    }
                    else if (kind == "turn_aborted")
                    {
                        authoritativeEnd = true;
                        result.State = Text(payload, "reason") == "interrupted" ? AiTaskState.Idle : AiTaskState.Error;
                        result.Evidence = result.State == AiTaskState.Idle ? "turn_interrupted" : "turn_aborted";
                        result.CompletionRevision = string.Empty;
                        pending.Clear();
                        asyncPending.Clear();
                    }
                    else if (kind == "item_completed")
                    {
                        var item = Object(payload, "item");
                        complete = !authoritativeEnd && Text(item, "type") == "AgentMessage" && Text(item, "phase") == "final_answer" && !IsQuestionDelivery(item);
                    }
                }
                else if (outer == "response_item")
                {
                    complete = !authoritativeEnd && kind == "message" && Text(payload, "role") == "assistant" &&
                        (Text(payload, "phase") == "final_answer" || Text(payload, "channel") == "final") && !IsQuestionDelivery(payload);
                    if (result.State == AiTaskState.Thinking || result.State == AiTaskState.WaitingForInput)
                    {
                        string call = Text(payload, "call_id"), name = Text(payload, "name");
                        int dot = name.LastIndexOf('.');
                        if (dot >= 0) name = name.Substring(dot + 1);
                        if ((kind == "function_call" || kind == "custom_tool_call") && call.Length != 0 &&
                            (name == "request_user_input" || name == "request_plugin_install")) pending.Add(call);
                        else if ((kind == "function_call" || kind == "custom_tool_call") && call.Length != 0 && name == "request_user_input_async")
                        {
                            var questions = new HashSet<int>();
                            for (int i = 0; i < (int)payload["question_count"]; i++) questions.Add(i);
                            asyncPending[call] = questions;
                        }
                        else if (kind == "function_call_output" || kind == "custom_tool_call_output")
                        {
                            pending.Remove(call);
                            // accepted:true means the question is displayed, not answered.
                            if ((bool)payload["async_request_failed"]) asyncPending.Remove(call);
                        }
                    }
                }
                object replyValue;
                if (payload.TryGetValue("question_replies", out replyValue))
                {
                    foreach (KeyValuePair<string, int> reply in (List<KeyValuePair<string, int>>)replyValue)
                    {
                        HashSet<int> questions;
                        if (!asyncPending.TryGetValue(reply.Key, out questions)) continue;
                        questions.Remove(reply.Value);
                        if (questions.Count == 0) asyncPending.Remove(reply.Key);
                    }
                }
                if (complete)
                {
                    result.State = AiTaskState.Completed;
                    result.Evidence = kind == "task_complete" ? "task_complete" : "assistant-final";
                    string completedTurn = activeTurn.Length == 0 ? turn : activeTurn;
                    result.CompletionRevision = completedTurn.Length == 0
                        ? path + ":" + record.Offset.ToString(CultureInfo.InvariantCulture)
                        : path + ":turn:" + completedTurn;
                    pending.Clear();
                    asyncPending.Clear();
                }
            }
            if ((result.State == AiTaskState.Thinking || result.State == AiTaskState.WaitingForInput) &&
                (pending.Count != 0 || asyncPending.Count != 0))
            {
                result.State = AiTaskState.WaitingForInput;
                result.Evidence = "pending-user-request";
            }
            // An OS reboot ended this local process. UI settings written later
            // do not resume it. Silence within one boot never implies completion.
            if (local && bootTime.HasValue && !unknownTime && started.HasValue && activity.HasValue &&
                started.Value < bootTime.Value && activity.Value < bootTime.Value &&
                (result.State == AiTaskState.Thinking || result.State == AiTaskState.WaitingForInput))
            {
                result.State = AiTaskState.Idle;
                result.Evidence = "local-turn-interrupted-by-reboot";
                result.CompletionRevision = string.Empty;
            }
            return result;
        }

        private static bool IsLocalRollout(Stream stream, JavaScriptSerializer json)
        {
            stream.Position = 0;
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true))
            {
                string line = reader.ReadLine();
                if (line == null) return false;
                try
                {
                    var meta = json.DeserializeObject(line) as Dictionary<string, object>;
                    string source = Text(Object(meta, "payload"), "source");
                    return Text(meta, "type") == "session_meta" && (source == "cli" || source == "vscode" || source == "exec");
                }
                catch (ArgumentException) { return false; }
                catch (InvalidOperationException) { return false; }
            }
        }

        private static DateTimeOffset? Timestamp(Dictionary<string, object> obj)
        {
            string text = Text(obj, "timestamp");
            // Zone-less or absent timestamps are not reliable reboot evidence.
            if (!text.EndsWith("Z", StringComparison.Ordinal) &&
                !(text.Length >= 6 && text[text.Length - 3] == ':' &&
                  (text[text.Length - 6] == '+' || text[text.Length - 6] == '-'))) return null;
            DateTimeOffset value;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
                ? (DateTimeOffset?)value : null;
        }

        private static bool HasError(Dictionary<string, object> payload)
        {
            object value;
            if (!payload.TryGetValue("error", out value) || value == null) return false;
            string text = value as string;
            if (text != null) return !string.IsNullOrWhiteSpace(text);
            if (value is bool) return (bool)value;
            if (value is int || value is long || value is double || value is decimal)
                return Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0;
            var collection = value as System.Collections.ICollection;
            return collection == null || collection.Count != 0;
        }

        private static string TurnId(Dictionary<string, object> obj)
        {
            string value = Text(obj, "turn_id");
            return value.Length == 0 ? Text(obj, "turnId") : value;
        }

        private static string Text(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as string ?? string.Empty : string.Empty;
        }

        private static Dictionary<string, object> Object(Dictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static bool HasQuestions(Dictionary<string, object> obj)
        {
            object value;
            var questions = obj != null && obj.TryGetValue("questions", out value) ? value as System.Collections.ICollection : null;
            return questions != null && questions.Count != 0;
        }

        private static bool IsQuestionDelivery(Dictionary<string, object> obj)
        {
            object value;
            return Text(obj, "delivery") == "async" || (obj != null && obj.TryGetValue("has_questions", out value) && value is bool && (bool)value);
        }

        private static object Decode(object value, JavaScriptSerializer json)
        {
            string text = value as string;
            if (text == null) return value;
            try { return json.DeserializeObject(text); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static int QuestionCount(Dictionary<string, object> payload, JavaScriptSerializer json)
        {
            object raw;
            var arguments = payload.TryGetValue("arguments", out raw) ? Decode(raw, json) as Dictionary<string, object> : null;
            var questions = arguments != null && arguments.TryGetValue("questions", out raw) ? raw as object[] : null;
            // Older records without arguments still require one real reply.
            return questions != null && questions.Length != 0 ? questions.Length : 1;
        }

        private static bool AsyncRequestFailed(Dictionary<string, object> payload, JavaScriptSerializer json)
        {
            object raw;
            var output = payload.TryGetValue("output", out raw) ? Decode(raw, json) as Dictionary<string, object> : null;
            if (HasError(payload)) return true;
            if (output == null) return false;
            object accepted, isError;
            return (output.TryGetValue("accepted", out accepted) && accepted is bool && !(bool)accepted) ||
                (output.TryGetValue("isError", out isError) && isError is bool && (bool)isError) || HasError(output);
        }

        private static List<KeyValuePair<string, int>> QuestionReplies(object content, JavaScriptSerializer json)
        {
            var replies = new List<KeyValuePair<string, int>>();
            var values = content as object[];
            if (values == null) return replies;
            foreach (object value in values)
            {
                var part = value as Dictionary<string, object>;
                if (Text(part, "type") != "input_text" && Text(part, "type") != "text") continue;
                string text = Text(part, "text");
                const string open = "<send_user_message_question_reply>", close = "</send_user_message_question_reply>";
                int next = 0;
                while (next < text.Length)
                {
                    int begin = text.IndexOf(open, next, StringComparison.Ordinal);
                    if (begin < 0) break;
                    begin += open.Length;
                    int end = text.IndexOf(close, begin, StringComparison.Ordinal);
                    if (end < 0) break;
                    next = end + close.Length;
                    var answers = Decode(text.Substring(begin, end - begin), json) as object[];
                    if (answers == null) continue;
                    foreach (object answer in answers)
                    {
                        var entry = answer as Dictionary<string, object>;
                        var id = Decode(Text(entry, "questionItemId"), json) as object[];
                        if (id == null || id.Length != 3 || !(id[0] is string) || (string)id[0] != "request_user_input_async" ||
                            !(id[1] is string) || string.IsNullOrEmpty((string)id[1]) || !(id[2] is int) || (int)id[2] < 0) continue;
                        replies.Add(new KeyValuePair<string, int>((string)id[1], (int)id[2]));
                    }
                }
            }
            return replies;
        }

        private static Record Parse(byte[] buffer, int start, int count, long offset, JavaScriptSerializer json)
        {
            if (count == 0) return null;
            try
            {
                var obj = json.DeserializeObject(Encoding.UTF8.GetString(buffer, start, count)) as Dictionary<string, object>;
                string outer = Text(obj, "type");
                var payload = Object(obj, "payload");
                if ((outer != "event_msg" && outer != "response_item") || payload == null) return null;
                // Retain status metadata only, not prompts, answers or tool output.
                var slim = new Dictionary<string, object>();
                foreach (string key in new[] { "type", "turn_id", "turnId", "reason", "role", "phase", "channel", "name", "call_id", "delivery" })
                {
                    object value;
                    if (payload.TryGetValue(key, out value)) slim[key] = value;
                }
                slim["error"] = HasError(payload);
                slim["has_questions"] = HasQuestions(payload);
                var item = Object(payload, "item");
                if (item != null)
                    slim["item"] = new Dictionary<string, object> { { "type", Text(item, "type") }, { "phase", Text(item, "phase") },
                        { "delivery", Text(item, "delivery") }, { "has_questions", HasQuestions(item) } };
                string kind = Text(payload, "type");
                if (outer == "response_item" && (kind == "function_call" || kind == "custom_tool_call") &&
                    (Text(payload, "name") == "request_user_input_async" || Text(payload, "name").EndsWith(".request_user_input_async", StringComparison.Ordinal)))
                    slim["question_count"] = QuestionCount(payload, json);
                if (outer == "response_item" && (kind == "function_call_output" || kind == "custom_tool_call_output"))
                    slim["async_request_failed"] = AsyncRequestFailed(payload, json);
                object content;
                if (outer == "response_item" && kind == "message" && Text(payload, "role") == "user" && payload.TryGetValue("content", out content))
                    slim["question_replies"] = QuestionReplies(content, json);
                else if (outer == "event_msg" && kind == "item_completed" && Text(item, "type") == "UserMessage" && item.TryGetValue("content", out content))
                    slim["question_replies"] = QuestionReplies(content, json);
                return new Record { Offset = offset, Value = new Dictionary<string, object>
                {
                    { "type", outer }, { "timestamp", Text(obj, "timestamp") },
                    { "turn_id", TurnId(obj) }, { "payload", slim }
                } };
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static IEnumerable<Record> ReadBackwards(Stream stream, JavaScriptSerializer json)
        {
            long position = stream.Length;
            byte[] suffix = new byte[0];
            while (position > 0)
            {
                int size = (int)Math.Min(65536, position);
                long start = position - size;
                var bytes = new byte[size + suffix.Length];
                stream.Position = start;
                int count = 0;
                while (count < size)
                {
                    int read = stream.Read(bytes, count, size - count);
                    if (read == 0) break;
                    count += read;
                }
                // A truncate/replacement race is an incomplete observation.
                if (count != size) yield break;
                Buffer.BlockCopy(suffix, 0, bytes, size, suffix.Length);
                int end = bytes.Length;
                for (int i = bytes.Length - 1; i >= 0; i--)
                {
                    if (bytes[i] != (byte)'\n') continue;
                    Record record = Parse(bytes, i + 1, end - i - 1, start + i + 1, json);
                    if (record != null) yield return record;
                    end = i;
                }
                if (start == 0)
                {
                    Record record = Parse(bytes, 0, end, 0, json);
                    if (record != null) yield return record;
                }
                else
                {
                    suffix = new byte[end];
                    Buffer.BlockCopy(bytes, 0, suffix, 0, end);
                }
                position = start;
            }
        }
    }
}
