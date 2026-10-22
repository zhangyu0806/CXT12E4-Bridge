using System;
using System.IO;
using System.Text;

namespace Cxt1024Bridge
{
    // Metadata-only local output. No commands, titles, paths, or conversation text.
    internal static class PassportStatusExport
    {
        private static readonly LatestWorkWorker Worker = new LatestWorkWorker();
        internal static int Code(AiTaskState state, bool present)
        {
            if (!present) return 0;
            switch (state)
            {
                case AiTaskState.Idle: return 1;
                case AiTaskState.Thinking: return 2;
                case AiTaskState.Completed: return 3;
                case AiTaskState.WaitingForInput: return 4;
                case AiTaskState.Error: return 5;
                default: return 0;
            }
        }

        internal static string Serialize(AiTaskState[] tasks, bool[] taskPresent,
            AiTaskState[] terminals, bool[] terminalPresent, long milliseconds, string[] taskIds = null)
        {
            if (tasks.Length != 6 || taskPresent.Length != 6 || terminals.Length != 6 || terminalPresent.Length != 6)
                throw new ArgumentException("Exactly six slots per bank are required.");
            var json = new StringBuilder();
            json.Append("{\"version\":1,\"observed_at_ms\":").Append(milliseconds);
            json.Append(",\"codex\":[");
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(Code(tasks[i], taskPresent[i]));
            }
            json.Append("],\"tmux\":[");
            for (int i = 0; i < 6; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(Code(terminals[i], terminalPresent[i]));
            }
            json.Append(']');
            if (taskIds != null)
            {
                if (taskIds.Length != 6) throw new ArgumentException("Six thread bindings required.");
                json.Append(",\"codex_threads\":[");
                for (int i = 0; i < 6; i++)
                {
                    if (i > 0) json.Append(',');
                    Guid id;
                    json.Append('"');
                    if (taskPresent[i] && Guid.TryParse(taskIds[i], out id)) json.Append(id.ToString("D"));
                    json.Append('"');
                }
                json.Append(']');
            }
            return json.Append('}').ToString();
        }

        internal static void Write(AiTaskState[] tasks, bool[] taskPresent,
            AiTaskState[] terminals, bool[] terminalPresent, string[] taskIds = null)
        {
            var tasksCopy = (AiTaskState[])tasks.Clone();
            var presentCopy = (bool[])taskPresent.Clone();
            var terminalsCopy = (AiTaskState[])terminals.Clone();
            var terminalPresentCopy = (bool[])terminalPresent.Clone();
            var idsCopy = taskIds == null ? null : (string[])taskIds.Clone();
            Worker.Submit(delegate { WriteCore(tasksCopy, presentCopy, terminalsCopy, terminalPresentCopy, idsCopy); });
        }

        private static void WriteCore(AiTaskState[] tasks, bool[] taskPresent,
            AiTaskState[] terminals, bool[] terminalPresent, string[] taskIds)
        {
            // Export failures must never block physical lighting updates.
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cxt1024Bridge");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "passport-status-v1.json");
                string staging = path + ".tmp";
                long milliseconds = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                File.WriteAllText(staging, Serialize(tasks, taskPresent, terminals, terminalPresent, milliseconds, taskIds), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(staging, path, null);
                else File.Move(staging, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
