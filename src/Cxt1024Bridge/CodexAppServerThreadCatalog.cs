using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Cxt1024Bridge
{
    // Uses the stable stdio JSON-RPC surface documented for Codex App Server.
    // The desktop app owns a different app-server process, so its live runtime
    // status is intentionally still read from the shared rollout log. This
    // client replaces only the private SQLite thread-catalog query and keeps a
    // local-catalog fallback when Codex is unavailable or changes version.
    internal sealed class CodexAppServerThreadCatalog : IDisposable
    {
        private const int RequestTimeoutMilliseconds = 5000;
        private const int MinimumFetchCount = 32;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);

        private readonly object _gate = new object();
        private readonly object _outputGate = new object();
        private readonly Queue<string> _outputLines = new Queue<string>();
        private readonly AutoResetEvent _outputAvailable = new AutoResetEvent(false);
        private readonly JavaScriptSerializer _json;
        private Process _process;
        private Thread _outputThread;
        private int _nextRequestId = 1;
        private List<ChatGptThreadSlot> _cache;
        private DateTime _cacheBuiltUtc;
        private string _binaryPath;
        private string _lastStandardError = string.Empty;
        private bool _disposed;

        internal CodexAppServerThreadCatalog()
        {
            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = int.MaxValue;
        }

        internal static void PrepareStdioEncoding()
        {
            try { Console.InputEncoding = new UTF8Encoding(false); }
            catch { }
        }

        internal string BinaryPath
        {
            get
            {
                lock (_gate)
                    return _binaryPath ?? string.Empty;
            }
        }

        internal List<ChatGptThreadSlot> ReadRecent(int limit)
        {
            if (limit < 1)
                throw new ArgumentOutOfRangeException("limit");

            lock (_gate)
            {
                ThrowIfDisposed();
                if (_cache != null && DateTime.UtcNow - _cacheBuiltUtc <= CacheLifetime)
                    return CloneFirst(_cache, limit);

                try
                {
                    EnsureConnectedLocked();
                    int fetchCount = Math.Max(MinimumFetchCount, limit);
                    var parameters = new Dictionary<string, object>
                    {
                        { "cursor", null },
                        { "limit", fetchCount },
                        { "sortKey", "recency_at" },
                        { "sortDirection", "desc" },
                        { "sourceKinds", new object[] { "cli", "vscode", "appServer" } },
                        { "useStateDbOnly", true }
                    };
                    Dictionary<string, object> result = SendRequestLocked(
                        "thread/list",
                        parameters,
                        RequestTimeoutMilliseconds);
                    _cache = ParseThreadListResult(result);
                    _cacheBuiltUtc = DateTime.UtcNow;
                    return CloneFirst(_cache, limit);
                }
                catch
                {
                    ResetProcessLocked();
                    throw;
                }
            }
        }

        internal static List<ChatGptThreadSlot> ParseThreadListResponseForTest(string json)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            var response = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (response == null)
                throw new InvalidOperationException("Codex App Server test response is not an object.");
            object resultValue;
            if (!response.TryGetValue("result", out resultValue))
                throw new InvalidOperationException("Codex App Server test response has no result.");
            var result = resultValue as Dictionary<string, object>;
            if (result == null)
                throw new InvalidOperationException("Codex App Server test result is not an object.");
            return ParseThreadListResult(result);
        }

        private void EnsureConnectedLocked()
        {
            if (_process != null && !_process.HasExited)
                return;

            ResetProcessLocked();
            _binaryPath = FindCodexBinary();
            var start = new ProcessStartInfo
            {
                FileName = _binaryPath,
                Arguments = "app-server --listen stdio://",
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            _process = new Process();
            _process.StartInfo = start;
            _process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _lastStandardError = args.Data;
            };
            // .NET Framework builds Process.StandardInput from
            // Console.InputEncoding. The default UTF-8 instance writes a BOM,
            // which is not legal at the beginning of an App Server JSONL frame.
            // Keep the same UTF-8 code page but explicitly disable its preamble.
            PrepareStdioEncoding();
            if (!_process.Start())
                throw new InvalidOperationException("Codex App Server process did not start.");
            _process.BeginErrorReadLine();
            StartOutputReaderLocked(_process);
            _nextRequestId = 1;

            var initialize = new Dictionary<string, object>
            {
                {
                    "clientInfo",
                    new Dictionary<string, object>
                    {
                        { "name", "cxt12e4_bridge" },
                        { "title", "CXT 12E4 Bridge" },
                        { "version", "1.1.0" }
                    }
                }
            };
            SendRequestLocked("initialize", initialize, RequestTimeoutMilliseconds);
            WriteMessageLocked(new Dictionary<string, object>
            {
                { "method", "initialized" },
                { "params", new Dictionary<string, object>() }
            });
        }

        private Dictionary<string, object> SendRequestLocked(
            string method,
            Dictionary<string, object> parameters,
            int timeoutMilliseconds)
        {
            if (_process == null || _process.HasExited)
                throw new InvalidOperationException("Codex App Server is not running.");

            int requestId = _nextRequestId++;
            WriteMessageLocked(new Dictionary<string, object>
            {
                { "method", method },
                { "id", requestId },
                { "params", parameters ?? new Dictionary<string, object>() }
            });

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                int remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                string line = ReadLineLocked(remaining);
                if (line == null)
                    throw new InvalidOperationException("Codex App Server closed its output stream.");
                if (line.Length == 0)
                    continue;

                Dictionary<string, object> message;
                try
                {
                    message = _json.DeserializeObject(line) as Dictionary<string, object>;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Codex App Server returned invalid JSON.", ex);
                }
                if (message == null || !MatchesRequestId(message, requestId))
                    continue;

                object errorValue;
                if (message.TryGetValue("error", out errorValue) && errorValue != null)
                    throw new InvalidOperationException("Codex App Server error: " + _json.Serialize(errorValue));

                object resultValue;
                if (!message.TryGetValue("result", out resultValue))
                    return new Dictionary<string, object>();
                var result = resultValue as Dictionary<string, object>;
                if (result == null)
                    throw new InvalidOperationException("Codex App Server result is not an object.");
                return result;
            }

            string detail = string.IsNullOrWhiteSpace(_lastStandardError)
                ? string.Empty
                : " Last stderr: " + _lastStandardError;
            throw new TimeoutException("Codex App Server request timed out." + detail);
        }

        private void WriteMessageLocked(Dictionary<string, object> message)
        {
            // Process.StandardInput can emit a UTF-8 BOM on its first write under
            // .NET Framework. App Server expects the first byte to begin JSON, so
            // write explicit BOM-free UTF-8 JSONL to the underlying pipe.
            byte[] bytes = new UTF8Encoding(false).GetBytes(_json.Serialize(message) + "\n");
            _process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            _process.StandardInput.BaseStream.Flush();
        }

        private string ReadLineLocked(int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                lock (_outputGate)
                {
                    if (_outputLines.Count > 0)
                        return _outputLines.Dequeue();
                }
                if (_process == null || _process.HasExited)
                    return null;
                int remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (!_outputAvailable.WaitOne(remaining))
                    break;
            }
            string detail = string.IsNullOrWhiteSpace(_lastStandardError)
                ? string.Empty
                : " Last stderr: " + _lastStandardError;
            throw new TimeoutException("Codex App Server output timed out." + detail);
        }

        private void StartOutputReaderLocked(Process process)
        {
            lock (_outputGate)
                _outputLines.Clear();
            _outputThread = new Thread(delegate()
            {
                try
                {
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        lock (_outputGate)
                            _outputLines.Enqueue(line);
                        _outputAvailable.Set();
                    }
                }
                catch
                {
                    // Process shutdown closes the redirected stream.
                }
                finally
                {
                    _outputAvailable.Set();
                }
            });
            _outputThread.IsBackground = true;
            _outputThread.Name = "CXT Codex App Server stdout";
            _outputThread.Start();
        }

        private static bool MatchesRequestId(Dictionary<string, object> message, int requestId)
        {
            object idValue;
            if (!message.TryGetValue("id", out idValue) || idValue == null)
                return false;
            try
            {
                return Convert.ToInt64(idValue, CultureInfo.InvariantCulture) == requestId;
            }
            catch
            {
                return false;
            }
        }

        private static List<ChatGptThreadSlot> ParseThreadListResult(Dictionary<string, object> result)
        {
            var threads = new List<ChatGptThreadSlot>();
            object dataValue;
            if (result == null || !result.TryGetValue("data", out dataValue) || dataValue == null)
                return threads;

            var items = dataValue as IEnumerable;
            if (items == null)
                return threads;
            foreach (object itemValue in items)
            {
                var item = itemValue as Dictionary<string, object>;
                if (item == null)
                    continue;
                string id = GetString(item, "id");
                Guid parsed;
                if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out parsed))
                    continue;
                string title = GetString(item, "name");
                if (string.IsNullOrWhiteSpace(title))
                    title = GetString(item, "preview");
                if (string.IsNullOrWhiteSpace(title))
                    title = id;
                long recency = GetLong(item, "recencyAt");
                if (recency == 0)
                    recency = GetLong(item, "updatedAt");
                if (recency > 0 && recency < 100000000000L)
                    recency *= 1000;

                string runtimeStatus = string.Empty;
                object statusValue;
                if (item.TryGetValue("status", out statusValue))
                {
                    var status = statusValue as Dictionary<string, object>;
                    if (status != null)
                        runtimeStatus = GetString(status, "type");
                }
                threads.Add(new ChatGptThreadSlot
                {
                    Id = id,
                    Title = title,
                    RecencyAtMilliseconds = recency,
                    RolloutPath = ChatGptThreadCatalog.NormalizeFileSystemPath(GetString(item, "path")),
                    RuntimeStatus = runtimeStatus
                });
            }
            return threads;
        }

        private static string GetString(Dictionary<string, object> value, string name)
        {
            object field;
            return value.TryGetValue(name, out field) && field != null
                ? Convert.ToString(field, CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static long GetLong(Dictionary<string, object> value, string name)
        {
            object field;
            if (!value.TryGetValue(name, out field) || field == null)
                return 0;
            try
            {
                return Convert.ToInt64(field, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static List<ChatGptThreadSlot> CloneFirst(IList<ChatGptThreadSlot> source, int limit)
        {
            int count = Math.Min(limit, source == null ? 0 : source.Count);
            var result = new List<ChatGptThreadSlot>(count);
            for (int i = 0; i < count; i++)
                result.Add(source[i].Clone());
            return result;
        }

        private static string FindCodexBinary()
        {
            string configured = Environment.GetEnvironmentVariable("CXT_CODEX_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return Path.GetFullPath(configured);

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenAI",
                "Codex",
                "bin");
            string selected = null;
            DateTime selectedWrite = DateTime.MinValue;
            if (Directory.Exists(root))
            {
                string[] candidates = Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories);
                for (int i = 0; i < candidates.Length; i++)
                {
                    DateTime write = File.GetLastWriteTimeUtc(candidates[i]);
                    if (selected == null || write > selectedWrite)
                    {
                        selected = candidates[i];
                        selectedWrite = write;
                    }
                }
            }
            if (selected != null)
                return selected;

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] directories = path.Split(Path.PathSeparator);
            for (int i = 0; i < directories.Length; i++)
            {
                string directory = directories[i].Trim().Trim('"');
                if (directory.Length == 0)
                    continue;
                string candidate = Path.Combine(directory, "codex.exe");
                if (File.Exists(candidate) && candidate.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) < 0)
                    return candidate;
            }
            throw new FileNotFoundException(
                "No runnable Codex CLI was found. Set CXT_CODEX_PATH to codex.exe if it is installed elsewhere.");
        }

        private void ResetProcessLocked()
        {
            Process process = _process;
            _process = null;
            if (process == null)
                return;
            try { process.StandardInput.Close(); }
            catch { }
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch { }
            try { process.WaitForExit(1000); }
            catch { }
            _outputAvailable.Set();
            Thread outputThread = _outputThread;
            _outputThread = null;
            if (outputThread != null && outputThread != Thread.CurrentThread)
            {
                try { outputThread.Join(500); }
                catch { }
            }
            lock (_outputGate)
                _outputLines.Clear();
            process.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("CodexAppServerThreadCatalog");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                ResetProcessLocked();
                _outputAvailable.Dispose();
            }
        }
    }

    internal sealed class HybridChatGptThreadCatalog : IDisposable
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
        private readonly object _gate = new object();
        private readonly CodexAppServerThreadCatalog _appServer = new CodexAppServerThreadCatalog();
        private DateTime _retryAfterUtc;
        private int _lastMode;
        private bool _disposed;

        internal List<ChatGptThreadSlot> ReadRecent(int limit)
        {
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException("HybridChatGptThreadCatalog");
                if (DateTime.UtcNow >= _retryAfterUtc)
                {
                    try
                    {
                        List<ChatGptThreadSlot> official = _appServer.ReadRecent(limit);
                        if (official.Count > 0)
                        {
                            RepairMissingRolloutPaths(official, limit);
                            LogMode(1, "Codex App Server thread/list");
                            return official;
                        }
                    }
                    catch (Exception ex)
                    {
                        _retryAfterUtc = DateTime.UtcNow.Add(RetryDelay);
                        Logger.Warn("Codex App Server catalog unavailable; using local read-only fallback for 30 seconds. " + ex.Message);
                    }
                }
                LogMode(2, "local state database");
                return ChatGptThreadCatalog.ReadRecent(limit);
            }
        }

        private static void RepairMissingRolloutPaths(IList<ChatGptThreadSlot> official, int limit)
        {
            bool needsRepair = false;
            for (int i = 0; i < official.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(official[i].RolloutPath) ||
                    !ChatGptThreadCatalog.RolloutFileExists(official[i].RolloutPath))
                {
                    needsRepair = true;
                    break;
                }
            }
            if (!needsRepair)
                return;

            List<ChatGptThreadSlot> local = ChatGptThreadCatalog.ReadRecent(Math.Max(64, limit));
            var localById = new Dictionary<string, ChatGptThreadSlot>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < local.Count; i++)
                localById[local[i].Id] = local[i];
            for (int i = 0; i < official.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(official[i].RolloutPath) &&
                    ChatGptThreadCatalog.RolloutFileExists(official[i].RolloutPath))
                    continue;
                ChatGptThreadSlot replacement;
                if (localById.TryGetValue(official[i].Id, out replacement))
                    official[i].RolloutPath = replacement.RolloutPath;
            }
        }

        private void LogMode(int mode, string description)
        {
            if (_lastMode == mode)
                return;
            _lastMode = mode;
            Logger.Info("ChatGPT task catalog source: " + description + ".");
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _appServer.Dispose();
            }
        }
    }
}
