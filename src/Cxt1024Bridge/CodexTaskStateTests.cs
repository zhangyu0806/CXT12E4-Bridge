using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Cxt1024Bridge
{
    internal static class CodexTaskStateTests
    {
        private static int _checks;
        private const string Started = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\"}}";
        private const string Completed = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}";
        private const string Aborted = "{\"type\":\"event_msg\",\"payload\":{\"type\":\"turn_aborted\"}}";

        private static void Check(bool condition, string label)
        {
            _checks++;
            if (!condition) throw new InvalidOperationException("Codex task state test failed: " + label);
        }

        private static string Event(string kind, string more)
        { return "{\"type\":\"event_msg\",\"payload\":{\"type\":\"" + kind + "\"" + more + "}}\n"; }
        private static string Response(string more)
        { return "{\"type\":\"response_item\",\"payload\":{" + more + "}}\n"; }
        private static void State(string text, AiTaskState state, string label)
        { Check(ChatGptTaskStatusReader.ReadTextStateForTest(text) == state, label); }
        private static string At(string record, string timestamp)
        { return "{\"timestamp\":\"" + timestamp + "\"," + record.Substring(1); }
        private static string Meta(string source)
        { return "{\"type\":\"session_meta\",\"payload\":{\"source\":" + source + "}}\n"; }
        private static void BootState(string text, AiTaskState state, string label)
        { Check(CodexRolloutState.ReadText(text, DateTimeOffset.Parse("2026-10-22T00:38:01Z")).State == state, label); }

        internal static void Run()
        {
            _checks = 0;
            string started = Event("task_started", ",\"turn_id\":\"current\"");
            string completed = Event("task_complete", ",\"turn_id\":\"current\"");
            string stopped = Event("turn_aborted", ",\"reason\":\"interrupted\",\"turn_id\":\"current\"");
            string failed = Event("task_complete", ",\"error\":{\"codex_error_info\":\"server_overloaded\"},\"turn_id\":\"current\"");
            string final = Response("\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\"");
            State(started, AiTaskState.Thinking, "started is blue");
            State(completed, AiTaskState.Completed, "historical completion is green");
            State(started + completed, AiTaskState.Completed, "completion is green");
            State(completed + started, AiTaskState.Thinking, "new start resets completion");
            State(started + stopped, AiTaskState.Idle, "user stop is white");
            State(started + failed, AiTaskState.Error, "nonempty completion error is red");
            State(started + Event("task_complete", ",\"error\":null"), AiTaskState.Completed, "null error is successful");
            State(started + Event("task_complete", ",\"error\":\"\""), AiTaskState.Completed, "empty error is successful");
            foreach (string error in new[] { "0", "0.0", "false", "{}", "[]", "\"   \"" })
                State(started + Event("task_complete", ",\"error\":" + error), AiTaskState.Completed, "empty error value " + error);
            foreach (string error in new[] { "1", "true", "\"failed\"", "[\"failed\"]" })
                State(started + Event("task_complete", ",\"error\":" + error), AiTaskState.Error, "nonempty error value " + error);
            State(started + Event("turn_aborted", ",\"reason\":\"failed\""), AiTaskState.Error, "failed abort stays red");
            State(started + Event("turn_aborted", ""), AiTaskState.Error, "unknown abort stays red");
            State(started + final, AiTaskState.Completed, "assistant final_answer fallback");
            State(started + Response("\"type\":\"message\",\"role\":\"assistant\",\"channel\":\"final\""), AiTaskState.Completed, "assistant final channel fallback");
            State(started + Event("item_completed", ",\"item\":{\"type\":\"AgentMessage\",\"phase\":\"final_answer\"}"), AiTaskState.Completed, "completed AgentMessage fallback");
            State(started + final + failed, AiTaskState.Error, "terminal failure overrides final");
            State(started + failed + final, AiTaskState.Error, "repeated final cannot hide terminal failure");
            State(started + stopped + final, AiTaskState.Idle, "repeated final cannot hide user stop");
            State(started + final + started, AiTaskState.Thinking, "new start resets final fallback");
            State(started + Event("task_complete", ",\"turn_id\":\"older\""), AiTaskState.Thinking, "old completion cannot end current turn");
            State(started + Event("turn_aborted", ",\"turn_id\":\"older\",\"reason\":\"failed\""), AiTaskState.Thinking, "old abort cannot end current turn");
            State(started + Response("\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"turn_id\":\"older\""), AiTaskState.Thinking, "old final cannot end current turn");
            State(started + Response("\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"commentary\",\"status\":\"completed\""), AiTaskState.Thinking, "commentary completed item is still running");
            State(started + Response("\"type\":\"message\",\"role\":\"user\",\"channel\":\"final\""), AiTaskState.Thinking, "user message is not completion");
            State(started + Response("\"type\":\"function_call_output\",\"phase\":\"final_answer\",\"status\":\"completed\""), AiTaskState.Thinking, "tool output is not completion");
            State(started + Event("item_completed", ",\"item\":{\"type\":\"CommandExecution\",\"phase\":\"final_answer\",\"status\":\"completed\"}"), AiTaskState.Thinking, "tool item completion is not task completion");
            State(started + "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"output\":{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}}}\n", AiTaskState.Thinking, "nested marker object cannot change status");
            State(started + "{\"payload\": {\"turn_id\": \"current\", \"type\": \"task_complete\"}, \"type\": \"event_msg\"}\n", AiTaskState.Completed, "JSON spaces and reordered properties work");
            State(started + "{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"", AiTaskState.Thinking, "incomplete appended record is ignored");
            string request = Response("\"type\":\"function_call\",\"name\":\"functions.request_user_input\",\"call_id\":\"one\"");
            string second = Response("\"type\":\"custom_tool_call\",\"name\":\"functions.request_plugin_install\",\"call_id\":\"two\"");
            string answer = Response("\"type\":\"function_call_output\",\"call_id\":\"one\"");
            State(started + request, AiTaskState.WaitingForInput, "pending synchronous question is yellow");
            State(started + request + answer, AiTaskState.Thinking, "answered question returns blue");
            State(started + request + second + answer, AiTaskState.WaitingForInput, "another unanswered question stays yellow");
            State(started + request + started, AiTaskState.Thinking, "previous turn question cannot leak");
            State(started + request + stopped, AiTaskState.Idle, "stop clears pending requests");
            State(started + request + final, AiTaskState.WaitingForInput, "final preserves pending requests until answered");
            State(started + Response("\"type\":\"function_call\",\"name\":\"functions.request_user_input_async\",\"call_id\":\"one\""), AiTaskState.WaitingForInput, "asynchronous question waits for a user reply");
            string padding = Response("\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"commentary\",\"content\":\"" + new string('字', 100000) + "\"");
            State(started + padding, AiTaskState.Thinking, "large multibyte record across read blocks");
            string revision1, revision2;
            ChatGptTaskStatusReader.ReadTextStateForTest(started + completed, out revision1);
            ChatGptTaskStatusReader.ReadTextStateForTest(started + completed + padding, out revision2);
            Check(revision1 == revision2 && revision1.Length != 0, "completion revision survives tail growth");
            ChatGptTaskStatusReader.ReadTextStateForTest(started + completed + started.Replace("current", "next") + completed.Replace("current", "next"), out revision2);
            Check(revision1 != revision2, "new completion has distinct revision");
            AcknowledgementChecks();
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, started + padding + final, new UTF8Encoding(false));
                string evidence, revision;
                Check(ChatGptTaskStatusReader.ReadRolloutState(path, out evidence, out revision) == AiTaskState.Completed, "real file parser follows final after large UTF8 record");
                Check(revision == path + ":turn:current", "revision uses current turn identity when available");
                File.AppendAllText(path, failed, new UTF8Encoding(false));
                Check(ChatGptTaskStatusReader.ReadRolloutState(path, out evidence, out revision) == AiTaskState.Error, "real file parser observes appended terminal error");
            }
            finally { File.Delete(path); }
            string oldStart = At(started, "2026-10-22T00:26:00Z");
            string local = Meta("\"exec\"");
            string oldRequest = At(request, "2026-10-22T00:30:00Z");
            string settings = At(Event("thread_settings_applied", ""), "2026-10-22T02:00:00Z");
            BootState(local + oldStart, AiTaskState.Idle, "previous-boot local turn is interrupted white");
            BootState(local + oldStart + oldRequest, AiTaskState.Idle, "previous-boot pending question is interrupted white");
            BootState(local + oldStart + settings, AiTaskState.Idle, "settings-only writes do not revive interrupted work");
            BootState(Meta("\"vscode\"") + oldStart, AiTaskState.Idle, "desktop local source can be interrupted by reboot");
            BootState(Meta("\"cli\"") + oldStart, AiTaskState.Idle, "CLI local source can be interrupted by reboot");
            BootState(Meta("\"cloud\"") + oldStart, AiTaskState.Thinking, "cloud turn is not terminated by local reboot");
            BootState(Meta("{\"subagent\":\"parent\"}") + oldStart, AiTaskState.Thinking, "subagent metadata is not treated as local");
            BootState(oldStart, AiTaskState.Thinking, "unknown source has no reboot inference");
            BootState(local + started, AiTaskState.Thinking, "missing start timestamp has no reboot inference");
            BootState(local + At(started, "2026-10-22T00:26:00"), AiTaskState.Thinking, "zone-less time is not reboot evidence");
            BootState(local + At(started, "2026-10-22T00:40:00Z"), AiTaskState.Thinking, "slow task in current boot stays blue");
            BootState(local + oldStart + At(Response("\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"commentary\""), "2026-10-22T00:40:00Z"), AiTaskState.Thinking, "post-boot work activity prevents inference");
            BootState(local + oldStart + request, AiTaskState.WaitingForInput, "undated work activity prevents inference");
            BootState(local + oldStart + At(completed, "2026-10-22T00:30:00Z"), AiTaskState.Completed, "known completion stays green across reboot");
            BootState(local + oldStart + At(final, "2026-10-22T00:30:00Z"), AiTaskState.Completed, "known final stays green across reboot");
            BootState(local + oldStart + At(failed, "2026-10-22T00:30:00Z"), AiTaskState.Error, "known failure stays red across reboot");
            BootState(local + oldStart + At(stopped, "2026-10-22T00:30:00Z"), AiTaskState.Idle, "known user stop stays white across reboot");
            BootState(local + oldStart + At(Event("task_complete", ",\"turn_id\":\"older\""), "2026-10-22T00:40:00Z"), AiTaskState.Idle, "other-turn activity cannot revive local interrupted work");
            Check(CodexRolloutState.ReadText(local + oldStart, null).State == AiTaskState.Thinking, "unknown OS boot time never guesses interruption");
            PendingTurnChecks();
            CompletionRevisionChecks();
            RolloutOffsetChecks();
            AsyncQuestionChecks();
            EndedTurnWaitingChecks();
            Console.WriteLine("Codex task state tests: " + _checks + " passed.");
        }

        private static string Json(object value)
        { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 128 }.Serialize(value); }

        private static ChatGptTaskStatusSlot Completion(string thread, string revision)
        { return new ChatGptTaskStatusSlot { ThreadId = thread, State = AiTaskState.Completed, Evidence = "task_complete", CompletionRevision = revision }; }

        private static AiTaskState Displayed(ChatGptCompletionLatch latch, ChatGptTaskStatusSlot slot)
        { latch.Apply(new[] { slot }); return slot.State; }

        private static void AcknowledgementChecks()
        {
            string directory = Path.Combine(Path.GetTempPath(), "cxt-codex-ack-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "ack.json");
            try
            {
                var latch = new ChatGptCompletionLatch(path);
                var captured = Completion("task-a", "rollout:turn:one");
                Check(Displayed(latch, Completion("task-a", "rollout:turn:one")) == AiTaskState.Completed, "unviewed historical completion is green");
                Check(!latch.Acknowledge((ChatGptTaskStatusSlot)null), "missing navigation snapshot is not acknowledged");
                foreach (AiTaskState state in new[] { AiTaskState.Thinking, AiTaskState.WaitingForInput, AiTaskState.Error, AiTaskState.Idle })
                {
                    var busy = Completion("task-a", "rollout:turn:two"); busy.State = state;
                    Check(!latch.Acknowledge(busy), "opening a non-completed state cannot acknowledge a future completion: " + state);
                }
                Check(!latch.Acknowledge(Completion("task-a", "")), "missing revision cannot acknowledge current or future completion");
                Check(Displayed(latch, Completion("task-a", "rollout:turn:two")) == AiTaskState.Completed, "completion occurring after an earlier busy snapshot stays green");
                Check(latch.Acknowledge(captured), "successful navigation acknowledges its captured completion");
                Check(Displayed(latch, Completion("task-a", "rollout:turn:one")) == AiTaskState.Idle, "viewed completion becomes white");
                Check(Displayed(latch, Completion("task-a", "rollout:turn:two")) == AiTaskState.Completed, "new completion during navigation remains green");
                Check(Displayed(latch, Completion("task-b", "rollout:turn:one")) == AiTaskState.Completed, "slot replacement cannot acknowledge another thread");
                Check(!latch.Acknowledge(captured), "duplicate acknowledgement is idempotent");
                var restarted = new ChatGptCompletionLatch(path);
                Check(Displayed(restarted, Completion("task-a", "rollout:turn:one")) == AiTaskState.Idle, "viewed completion survives Bridge restart");
                Check(Displayed(restarted, Completion("task-a", "rollout:turn:two")) == AiTaskState.Completed, "unviewed completion survives Bridge restart as green");
                Check(restarted.Acknowledge(Completion("task-a", "rollout:turn:two")), "newer completion can be viewed independently");
                Check(!restarted.Acknowledge(captured), "old frozen chooser cannot overwrite newer acknowledgement");
                var again = new ChatGptCompletionLatch(path);
                Check(Displayed(again, Completion("task-a", "rollout:turn:two")) == AiTaskState.Idle, "newer acknowledgement remains persisted after stale chooser attempt");
                foreach (AiTaskState state in new[] { AiTaskState.Thinking, AiTaskState.WaitingForInput, AiTaskState.Error })
                {
                    var busy = Completion("task-a", "rollout:turn:one"); busy.State = state;
                    Check(Displayed(again, busy) == state, "viewed revision cannot suppress a current active or failed state: " + state);
                }
                string started = Event("task_started", ",\"turn_id\":\"stable\"");
                string final = Response("\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\"");
                string finalRevision, terminalRevision;
                ChatGptTaskStatusReader.ReadTextStateForTest(started + final, out finalRevision);
                ChatGptTaskStatusReader.ReadTextStateForTest(started + final + Event("task_complete", ",\"turn_id\":\"stable\""), out terminalRevision);
                Check(finalRevision == terminalRevision && finalRevision == ":turn:stable", "final fallback and terminal completion share one stable turn revision");
                Check(again.Acknowledge(Completion("task-c", finalRevision)), "fallback completion can be acknowledged");
                Check(Displayed(again, Completion("task-c", terminalRevision)) == AiTaskState.Idle, "later task_complete does not relight a viewed final answer");
                File.WriteAllText(path, "{broken", new UTF8Encoding(false));
                var corrupt = new ChatGptCompletionLatch(path);
                Check(Displayed(corrupt, Completion("task-a", "rollout:turn:one")) == AiTaskState.Completed, "corrupt cache never hides unverified completion");
                Check(corrupt.Acknowledge(Completion("task-a", "rollout:turn:one")), "a valid acknowledgement can replace corrupt cache atomically");
                string blocker = Path.Combine(directory, "file-not-directory");
                File.WriteAllText(blocker, "blocked");
                var cannotSave = new ChatGptCompletionLatch(Path.Combine(blocker, "ack.json"));
                Check(!cannotSave.Acknowledge(captured), "failed persistence cannot claim successful acknowledgement");
                Check(Displayed(cannotSave, Completion("task-a", "rollout:turn:one")) == AiTaskState.Completed, "failed persistence rolls back acknowledgement and keeps green");
                string rollout = Path.Combine(directory, "navigation.jsonl");
                string first = Event("task_started", ",\"turn_id\":\"first\"") + Event("task_complete", ",\"turn_id\":\"first\"");
                string next = Event("task_started", ",\"turn_id\":\"second\"") + Event("task_complete", ",\"turn_id\":\"second\"");
                File.WriteAllText(rollout, first, new UTF8Encoding(false));
                var target = new ChatGptThreadSlot { Id = "navigation-task", RolloutPath = rollout };
                ChatGptTaskStatusSlot opened;
                Check(ChatGptTaskStatusReader.OpenWithSnapshot(target, delegate(ChatGptThreadSlot frozen)
                {
                    Check(frozen.Id == "navigation-task", "navigation dispatch uses the captured thread identity");
                    File.AppendAllText(rollout, next, new UTF8Encoding(false));
                    return true;
                }, out opened), "successful navigation produces acknowledgement snapshot");
                Check(opened.CompletionRevision == rollout + ":turn:first", "navigation acknowledgement contains only the pre-dispatch completion");
                Check(again.Acknowledge(opened), "pre-dispatch completion can be acknowledged after navigation");
                Check(Displayed(again, ChatGptTaskStatusReader.ReadSlots(new[] { target })[0]) == AiTaskState.Completed,
                    "new completion written while navigation was in flight remains green");
                Check(!ChatGptTaskStatusReader.OpenWithSnapshot(target, delegate { return false; }, out opened) && opened == null,
                    "unsuccessful navigation produces no acknowledgement snapshot");
                bool threw = false;
                try { ChatGptTaskStatusReader.OpenWithSnapshot(target, delegate { throw new IOException("navigation failed"); }, out opened); }
                catch (IOException) { threw = true; }
                Check(threw && opened == null, "failed navigation cannot leave a successful acknowledgement snapshot");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private static string AsyncRequest(string call, int count, string name, string kind)
        {
            var questions = new List<object>();
            for (int i = 0; i < count; i++) questions.Add(new { title = "Question " + i });
            return Json(new { type = "response_item", payload = new { type = kind, name = name, call_id = call,
                arguments = Json(new { questions = questions }) } }) + "\n";
        }

        private static string AsyncOutput(string call, object output)
        { return Json(new { type = "response_item", payload = new { type = "function_call_output", call_id = call, output = Json(output) } }) + "\n"; }

        private static object[] ReplyContent(string call, int index)
        {
            string id = Json(new object[] { "request_user_input_async", call, index });
            string text = "<send_user_message_question_reply>" + Json(new[] { new { questionItemId = id, answer = "yes" } }) + "</send_user_message_question_reply>";
            return new object[] { new { type = "input_text", text = text } };
        }

        private static string UserReply(string call, int index, string role, string turn)
        { return Json(new { type = "response_item", payload = new { type = "message", role = role, content = ReplyContent(call, index), turn_id = turn } }) + "\n"; }

        private static void AsyncQuestionChecks()
        {
            string started = Event("task_started", ",\"turn_id\":\"current\"");
            string question = AsyncRequest("call_a", 1, "request_user_input_async", "function_call");
            string accepted = AsyncOutput("call_a", new { accepted = true });
            string reply = UserReply("call_a", 0, "user", "current");
            string displayed = Json(new { type = "event_msg", payload = new { type = "item_completed", item = new {
                type = "AgentMessage", id = "call_a", phase = "final_answer", delivery = "async", questions = new[] { new { title = "Question" } } } } }) + "\n";
            State(started + question, AiTaskState.WaitingForInput, "async question is yellow before tool acknowledgement");
            State(started + question + accepted, AiTaskState.WaitingForInput, "accepted only acknowledges display, remains yellow");
            State(started + question + displayed, AiTaskState.WaitingForInput, "actual async question AgentMessage is not task completion");
            State(started + question + displayed + accepted, AiTaskState.WaitingForInput, "displayed question and accepted acknowledgement remain yellow");
            State(started + question + displayed + accepted + reply, AiTaskState.Thinking, "actual question lifecycle is yellow until reply then blue");
            State(started + question + Event("item_completed", ",\"item\":{\"type\":\"AgentMessage\",\"phase\":\"final_answer\",\"delivery\":\"async\"}"), AiTaskState.WaitingForInput, "async delivery alone excludes final fallback");
            State(started + question + Event("item_completed", ",\"item\":{\"type\":\"AgentMessage\",\"phase\":\"final_answer\",\"questions\":[{}]}"), AiTaskState.WaitingForInput, "question list alone excludes final fallback");
            State(started + question + Response("\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"delivery\":\"async\""), AiTaskState.WaitingForInput, "async assistant message is a question not completion");
            State(started + question + Response("\"type\":\"message\",\"role\":\"assistant\",\"channel\":\"final\",\"questions\":[{}]"), AiTaskState.WaitingForInput, "assistant message questions exclude final fallback");
            State(started + question + accepted + reply, AiTaskState.Thinking, "matching user reply clears async waiting");
            State(started + question + accepted + reply + accepted, AiTaskState.Thinking, "late tool acknowledgement does not rearm answered question");
            State(started + question + accepted + reply + reply, AiTaskState.Thinking, "duplicate user replies are idempotent");
            State(started + question + UserReply("other", 0, "user", "current"), AiTaskState.WaitingForInput, "reply for another call cannot clear waiting");
            State(started + question + UserReply("call_a", 1, "user", "current"), AiTaskState.WaitingForInput, "wrong question index cannot clear waiting");
            State(started + question + UserReply("call_a", 0, "assistant", "current"), AiTaskState.WaitingForInput, "assistant text cannot supply a user reply");
            State(started + question + UserReply("call_a", 0, "user", "older"), AiTaskState.WaitingForInput, "other-turn user reply cannot clear current waiting");
            State(started + question + Json(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "yes" } } } }) + "\n", AiTaskState.WaitingForInput, "ordinary user text is not an async answer");
            State(started + question + AsyncOutput("call_a", ReplyContent("call_a", 0)), AiTaskState.WaitingForInput, "tool text containing reply IDs cannot answer question");
            State(started + question + AsyncOutput("other", new { accepted = false }), AiTaskState.WaitingForInput, "other tool failure cannot cancel question");
            State(started + question + AsyncOutput("call_a", new { accepted = false }), AiTaskState.Thinking, "explicit display rejection cancels question");
            State(started + question + AsyncOutput("call_a", new { error = "display failed" }), AiTaskState.Thinking, "explicit error cancels question");
            State(started + question + AsyncOutput("call_a", new { isError = true }), AiTaskState.Thinking, "explicit tool error result cancels question");
            State(started + question + AsyncOutput("call_a", new { accepted = "false" }), AiTaskState.WaitingForInput, "untyped accepted text cannot cancel waiting");
            string two = AsyncRequest("call_a", 2, "functions.request_user_input_async", "function_call");
            State(started + two + accepted + reply, AiTaskState.WaitingForInput, "one of two questions remains yellow");
            State(started + two + accepted + reply + reply, AiTaskState.WaitingForInput, "duplicate first answer does not answer second question");
            State(started + two + accepted + reply + UserReply("call_a", 1, "user", "current"), AiTaskState.Thinking, "all question indices answered clears waiting");
            string other = AsyncRequest("call_b", 1, "request_user_input_async", "custom_tool_call");
            State(started + question + other + reply, AiTaskState.WaitingForInput, "another async call remains pending");
            State(started + question + other + reply + UserReply("call_b", 0, "user", "current"), AiTaskState.Thinking, "independent async calls each require reply");
            string eventReply = Json(new { type = "event_msg", payload = new { type = "item_completed", item = new { type = "UserMessage", content = ReplyContent("call_a", 0) } } }) + "\n";
            State(started + question + eventReply, AiTaskState.Thinking, "UserMessage item completion carries user reply");
            State(started + question + reply + eventReply, AiTaskState.Thinking, "response and event duplicate reply is idempotent");
            State(started + question + Event("task_complete", ""), AiTaskState.WaitingForInput, "successful turn completion preserves unanswered async question");
            State(started + question + Response("\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\""), AiTaskState.WaitingForInput, "final fallback preserves unanswered async question");
            State(started + question + Event("turn_aborted", ",\"reason\":\"interrupted\""), AiTaskState.Idle, "user stop clears async waiting");
            State(started + question + Event("task_complete", ",\"error\":\"failed\""), AiTaskState.Error, "task failure clears async waiting");
            State(started + question + started, AiTaskState.Thinking, "new turn clears async waiting");
        }

        private static void EndedTurnWaitingChecks()
        {
            string started = Event("task_started", ",\"turn_id\":\"current\"");
            string question = AsyncRequest("call_a", 1, "functions.request_user_input_async", "function_call");
            string accepted = AsyncOutput("call_a", new { accepted = true });
            string final = Response("\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\"");
            string completed = Event("task_complete", ",\"turn_id\":\"current\"");
            string reply = UserReply("call_a", 0, "user", "current");
            foreach (string ending in new[] { final, completed, final + completed,
                Response("\"type\":\"message\",\"role\":\"assistant\",\"channel\":\"final\""),
                Event("item_completed", ",\"item\":{\"type\":\"AgentMessage\",\"phase\":\"final_answer\"}") })
            {
                string waiting = started + question + accepted + ending;
                ChatGptTaskStatusSlot pendingSlot = Slot(waiting);
                Check(pendingSlot.State == AiTaskState.WaitingForInput, "ended turn with outstanding question remains yellow");
                Check(pendingSlot.CompletionRevision.Length == 0, "waiting has no acknowledgeable completion revision");
                var latch = new ChatGptCompletionLatch();
                Check(!latch.Acknowledge(pendingSlot), "viewing unanswered task cannot pre-acknowledge completion");
                State(waiting + accepted, AiTaskState.WaitingForInput, "late accepted receipt does not answer ended-turn question");
                State(waiting + UserReply("other", 0, "user", "current"), AiTaskState.WaitingForInput, "unrelated reply cannot clear ended-turn question");
                State(waiting + UserReply("call_a", 0, "user", "older"), AiTaskState.WaitingForInput, "wrong-turn reply cannot clear ended-turn question");
                State(waiting + reply, AiTaskState.Completed, "same-turn reply exposes known completion without inventing new work");
                Check(Apply(latch, waiting + reply) == AiTaskState.Completed, "resolved completion remains unviewed after opening pending question");
                Check(Slot(waiting + reply).CompletionRevision.Length != 0, "resolved completion regains its revision");
                State(waiting + reply + reply, AiTaskState.Completed, "duplicate reply after completion is idempotent");
                State(waiting + AsyncOutput("call_a", new { accepted = false }), AiTaskState.Completed, "explicit request rejection after turn end removes waiting");
                State(waiting + Event("turn_aborted", ",\"reason\":\"interrupted\""), AiTaskState.Idle, "manual stop cancels ended-turn question");
                State(waiting + Event("task_complete", ",\"error\":\"failed\""), AiTaskState.Error, "explicit error cancels ended-turn question");
                State(waiting + Event("task_started", ",\"turn_id\":\"resumed\"") +
                    UserReply("call_a", 0, "user", "resumed"), AiTaskState.Thinking, "reply delivered in a resumed turn is blue");
            }
            string two = started + AsyncRequest("call_a", 2, "request_user_input_async", "function_call") + accepted + final + completed;
            State(two + reply, AiTaskState.WaitingForInput, "only one of two replies after completion remains yellow");
            Check(Slot(two + reply).CompletionRevision.Length == 0, "partially answered completion cannot be acknowledged");
            State(two + reply + UserReply("call_a", 1, "user", "current"), AiTaskState.Completed, "all ended-turn questions answered exposes completion");
            string independent = started + question + AsyncRequest("call_b", 1, "request_user_input_async", "custom_tool_call") + final + completed;
            State(independent + reply, AiTaskState.WaitingForInput, "other ended-turn call remains pending");
            State(independent + reply + UserReply("call_b", 0, "user", "current"), AiTaskState.Completed, "all ended-turn calls answered exposes completion");
            string synchronous = started + Request("sync", "request_user_input") + "\n" + completed;
            State(synchronous, AiTaskState.WaitingForInput, "success cannot silently dismiss synchronous request either");
            State(synchronous + Answer("sync"), AiTaskState.Completed, "matching synchronous output resolves ended-turn request");
            string local = Meta("\"cli\"") + At(started, "2026-10-22T00:20:00Z") +
                At(question, "2026-10-22T00:21:00Z") + At(accepted, "2026-10-22T00:21:01Z") +
                At(final, "2026-10-22T00:21:02Z") + At(completed, "2026-10-22T00:21:03Z");
            BootState(local, AiTaskState.WaitingForInput, "already-ended unanswered question is not a reboot-interrupted running task");
        }
        private static string Request(string id, string name)
        {
            return "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"name\":\"" +
                name + "\",\"call_id\":\"" + id + "\"}}";
        }

        private static string Answer(string id)
        {
            return "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"" +
                id + "\",\"output\":\"ok\"}}";
        }

        private static void PendingTurnChecks()
        {
            foreach (string name in new[] { "request_user_input", "request_plugin_install" })
            {
                string pending = Started + "\n" + Request("old", name);
                Check(ChatGptTaskStatusReader.ReadTextStateForTest(pending) == AiTaskState.WaitingForInput, "current request is pending: " + name);
                Check(ChatGptTaskStatusReader.ReadTextStateForTest(pending + "\n" + Answer("old")) == AiTaskState.Thinking, "answered current request: " + name);
                Check(ChatGptTaskStatusReader.ReadTextStateForTest(pending + "\n" + Started) == AiTaskState.Thinking, "new start alone ends the previous pending scope: " + name);
                string resumed = pending + "\n" + Aborted + "\n" + Started;
                Check(ChatGptTaskStatusReader.ReadTextStateForTest(resumed) == AiTaskState.Thinking, "aborted question cannot leak into next turn: " + name);
                Check(ChatGptTaskStatusReader.ReadTextStateForTest(pending + "\n" + Completed + "\n" + Started) == AiTaskState.Thinking, "completed question cannot leak into next turn: " + name);
                Check(ChatGptTaskStatusReader.ReadTextStateForTest(resumed + "\n" + Request("new", name)) == AiTaskState.WaitingForInput, "new turn can ask again: " + name);
            }
        }

        private static ChatGptTaskStatusSlot Slot(string text)
        {
            string revision;
            AiTaskState state = ChatGptTaskStatusReader.ReadTextStateForTest(text, out revision);
            return new ChatGptTaskStatusSlot
            {
                ThreadId = "synthetic-l1-task",
                State = state,
                Evidence = state == AiTaskState.Completed ? "task_complete" : "task_started",
                CompletionRevision = revision
            };
        }

        private static AiTaskState Apply(ChatGptCompletionLatch latch, string text)
        {
            ChatGptTaskStatusSlot slot = Slot(text);
            latch.Apply(new[] { slot });
            return slot.State;
        }

        private static void CompletionRevisionChecks()
        {
            var latch = new ChatGptCompletionLatch();
            string oldTurn = Started + "\n" + Completed;
            Check(Apply(latch, oldTurn) == AiTaskState.Completed, "initial historical completion remains green");
            Check(Apply(latch, oldTurn + "\nextra log text") == AiTaskState.Completed, "tail growth preserves the completed state");
            string fastTurn = oldTurn + "\n" + Started + "\n" + Completed;
            Check(Apply(latch, fastTurn) == AiTaskState.Completed, "completed turn between polls latches green");
            Check(Apply(latch, fastTurn) == AiTaskState.Completed, "completion stays green across polls");
            Check(latch.Acknowledge(Slot(fastTurn)), "successful view acknowledges exact completed revision");
            Check(Apply(latch, fastTurn) == AiTaskState.Idle, "same completion becomes white after viewing");
            latch.Apply(new[] { new ChatGptTaskStatusSlot
            {
                ThreadId = "synthetic-l1-task", State = AiTaskState.Idle, Evidence = "no-rollout"
            } });
            Check(Apply(latch, fastTurn) == AiTaskState.Idle, "restored rollout retains viewed completion");
            Check(Apply(latch, fastTurn + "\n" + Started) == AiTaskState.Thinking, "active transition positive control");
            Check(Apply(latch, fastTurn + "\n" + Started + "\n" + Completed) == AiTaskState.Completed, "sampled active completion still works");

            var firstTurnLatch = new ChatGptCompletionLatch();
            firstTurnLatch.Apply(new[] { new ChatGptTaskStatusSlot
            {
                ThreadId = "synthetic-l1-task", State = AiTaskState.Idle, Evidence = "no-task-marker"
            } });
            Check(Apply(firstTurnLatch, oldTurn) == AiTaskState.Completed, "first fast turn after a known idle baseline");
        }

        private static void RolloutOffsetChecks()
        {
            string fixturePath = Path.Combine(Path.GetTempPath(), "cxt12e4-l1-audit-" + Guid.NewGuid().ToString("N") + ".jsonl");
            try
            {
                // Put the completion pattern across a 64 KiB reverse-scan block
                // boundary, after UTF-8 text. Its byte offset must remain exact.
                string prefix = new string('\u4E2D', 100) + "\n";
                string fixture = prefix + Completed + "\n" + new string('x', 65536 - Completed.Length + 11);
                File.WriteAllText(fixturePath, fixture, new UTF8Encoding(false));
                string evidence;
                string revision;
                Check(ChatGptTaskStatusReader.ReadRolloutState(fixturePath, out evidence, out revision) == AiTaskState.Completed && evidence == "task_complete", "cross-block completion marker");
                string expectedSuffix = ":" + Encoding.UTF8.GetByteCount(prefix).ToString(CultureInfo.InvariantCulture);
                Check(revision.EndsWith(expectedSuffix, StringComparison.Ordinal), "completion revision uses exact UTF-8 byte offset");
                File.AppendAllText(fixturePath, "\nmore log text", new UTF8Encoding(false));
                string afterAppend;
                ChatGptTaskStatusReader.ReadRolloutState(fixturePath, out evidence, out afterAppend);
                Check(revision == afterAppend, "file tail growth preserves completion identity");

                string resumed = Started + "\n" + Request("cancelled", "request_user_input") + "\n" + Aborted + "\n" + Started;
                File.WriteAllText(fixturePath, resumed, new UTF8Encoding(false));
                Check(ChatGptTaskStatusReader.ReadRolloutState(fixturePath, out evidence, out revision) == AiTaskState.Thinking, "file reader scopes pending calls to current turn");
            }
            finally
            {
                if (File.Exists(fixturePath))
                    File.Delete(fixturePath);
            }
        }

    }
}
