using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Cxt1024Bridge
{
    internal static class CodexQuestionIgnoreTests
    {
        private static int _checks;
        private static void Check(bool value, string label) { _checks++; if (!value) throw new InvalidOperationException("Question ignore: " + label); }
        private static string Json(object value) { return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 32 }.Serialize(value); }
        private static string Start(string turn) { return Json(new { type = "event_msg", payload = new { type = "task_started", turn_id = turn } }) + "\n"; }
        private static string Question(string call, int count)
        {
            var questions = new List<object>(); for (int i = 0; i < count; i++) questions.Add(new { title = "同名问题 " + i });
            return Json(new { type = "response_item", payload = new { type = "function_call", name = "functions.request_user_input_async", call_id = call, arguments = Json(new { questions = questions }) } }) + "\n";
        }
        private static HashSet<string> Ignore(params int[] indices)
        {
            var set = new HashSet<string>(); foreach (int index in indices) set.Add(CodexPendingQuestion.Identity("turn", "call", index)); return set;
        }
        internal static void Run()
        {
            _checks = 0; string text = Start("turn") + Question("call", 2);
            var one = CodexRolloutState.ReadText(text, null, Ignore(0), true);
            Check(one.State == AiTaskState.WaitingForInput && one.Questions.Count == 2, "another question remains yellow");
            Check(one.Questions[0].Ignored && !one.Questions[1].Ignored && one.Questions[0].Title == "同名问题 0", "exact index and title");
            Check(CodexRolloutState.ReadText(text, null, Ignore(0, 1), true).State == AiTaskState.Thinking, "all ignored returns running blue");
            Check(CodexRolloutState.ReadText(text, null, Ignore(1), true).State == AiTaskState.WaitingForInput, "restoring one returns yellow");
            Check(CodexRolloutState.ReadText(text + Start("next") + Question("call", 1), null, Ignore(0)).State == AiTaskState.WaitingForInput, "new turn is unaffected");
            Check(CodexRolloutState.ReadText(Start("turn") + Question("other", 1), null, Ignore(0)).State == AiTaskState.WaitingForInput, "new call is unaffected");
            string end = Json(new { type = "event_msg", payload = new { type = "task_complete", turn_id = "turn" } }) + "\n";
            var completed = CodexRolloutState.ReadText(text + end, null, Ignore(0, 1), true);
            Check(completed.State == AiTaskState.Completed && completed.CompletionRevision.Length != 0, "ignore reveals real completion, does not acknowledge it");
            Check(CodexRolloutState.ReadText(text + end, null, Ignore(0)).CompletionRevision.Length == 0, "partial ignore cannot acknowledge completion");
            string sync = Json(new { type = "response_item", payload = new { type = "function_call", name = "request_user_input", call_id = "sync" } }) + "\n";
            Check(CodexRolloutState.ReadText(text + sync, null, Ignore(0, 1), true).State == AiTaskState.WaitingForInput, "cannot ignore synchronous permissions/input");
            foreach (var stopped in new[] { "{\"type\":\"turn_aborted\",\"reason\":\"interrupted\"}", "{\"type\":\"task_complete\",\"error\":\"failed\"}" })
            {
                var result = CodexRolloutState.ReadText(text + "{\"type\":\"event_msg\",\"payload\":" + stopped + "}\n", null, Ignore(0), true);
                Check(result.Questions.Count == 0 && result.State != AiTaskState.WaitingForInput, "stopped/error removes manageable questions");
            }
            string reply = "<send_user_message_question_reply>" + Json(new[] { new { questionItemId = Json(new object[] { "request_user_input_async", "call", 0 }), answer = "yes" } }) + "</send_user_message_question_reply>";
            string answered = Json(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = reply } } } }) + "\n";
            Check(CodexRolloutState.ReadText(Start("turn") + Question("call", 1) + answered, null, Ignore(0), true).Questions.Count == 0, "answered questions disappear");
            Check(CodexRolloutState.ReadText(Start("") + Question("call", 1), null, Ignore(0), true).Questions.Count == 0, "missing turn identity cannot be managed");
            Check(CodexPendingQuestion.Identity("ab", "c", 0) != CodexPendingQuestion.Identity("a", "bc", 0), "identity is unambiguous");
            string folder = Path.Combine(Path.GetTempPath(), "cxt-question-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string path = Path.Combine(folder, "ignore.json"), log = Path.Combine(folder, "rollout.jsonl");
                File.WriteAllText(log, Start("turn") + Question("call", 1), new UTF8Encoding(false));
                var threads = new List<ChatGptThreadSlot> { new ChatGptThreadSlot { Id = "one", Title = "同名任务", RolloutPath = log }, new ChatGptThreadSlot { Id = "two", Title = "同名任务", RolloutPath = log } };
                var store = new CodexQuestionIgnoreStore(path); var service = new CodexQuestionService(store, () => threads);
                var rows = service.Read(); Check(rows.Count == 2 && rows[0].Key == 1 && rows[1].Key == 2, "fixed task key labels");
                service.Set(rows[0], true); service.Set(rows[0], true);
                var restart = new CodexQuestionIgnoreStore(path);
                Check(restart.ForThread("one").Count == 1 && restart.ForThread("two").Count == 0, "idempotent persistent ignore bound to thread");
                Check(!File.ReadAllText(path).Contains("同名"), "question and task text never persisted");
                var reopened = new CodexQuestionService(restart, () => threads);
                Check(reopened.Read()[0].Question.Ignored && !reopened.Read()[1].Question.Ignored, "restart round trip");
                reopened.Set(rows[0], false); Check(restart.ForThread("one").Count == 0, "restore persists");
                File.WriteAllText(log, Start("next") + Question("call", 1));
                bool rejected = false; try { reopened.Set(rows[0], true); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && restart.ForThread("one").Count == 0, "stale panel cannot affect new turn");
                threads.Clear(); rejected = false; try { reopened.Set(rows[0], true); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "removed task cannot be changed");
                File.WriteAllText(path, "broken"); var corrupt = new CodexQuestionIgnoreStore(path);
                Check(corrupt.LoadError.Length != 0 && corrupt.ForThread("one").Count == 0, "corrupt store fails without clearing waiting");
                rejected = false; try { corrupt.Set("one", rows[0].Question, true); } catch (IOException) { rejected = true; }
                Check(rejected && File.ReadAllText(path) == "broken", "do not overwrite corrupt store");
                string parentFile = Path.Combine(folder, "not-directory"); File.WriteAllText(parentFile, "x");
                var failing = new CodexQuestionIgnoreStore(Path.Combine(parentFile, "ignore.json"));
                rejected = false; try { failing.Set("one", rows[0].Question, true); } catch (IOException) { rejected = true; }
                Check(rejected && failing.ForThread("one").Count == 0, "failed write does not change memory");
            }
            finally { Directory.Delete(folder, true); }
            Console.WriteLine("PASS " + _checks + " explicit question-ignore checks (no UI/input)");
        }
    }
}
