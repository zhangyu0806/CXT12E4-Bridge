using System;
using System.Collections.Generic;

namespace Cxt1024Bridge
{
    // Missing paths are normal for chats from another machine. Share one retry
    // interval across all IDs so a catalog page cannot trigger one full scan per
    // missing chat. New local rollouts can still be found on the next retry.
    internal sealed class CodexRolloutPathCache
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MissingRetryInterval = TimeSpan.FromSeconds(1);
        private readonly object _gate = new object();
        private readonly Func<IList<string>, Dictionary<string, string>> _scan;
        private readonly Func<string, bool> _exists;
        private readonly Func<DateTime> _utcNow;
        private Dictionary<string, string> _paths;
        private string _rootsKey;
        private DateTime _builtUtc;

        internal CodexRolloutPathCache(Func<IList<string>, Dictionary<string, string>> scan,
            Func<string, bool> exists, Func<DateTime> utcNow)
        {
            if (scan == null || exists == null || utcNow == null) throw new ArgumentNullException();
            _scan = scan;
            _exists = exists;
            _utcNow = utcNow;
        }

        internal string Find(string threadId, IList<string> roots)
        {
            if (string.IsNullOrWhiteSpace(threadId) || roots == null || roots.Count == 0)
                return string.Empty;
            string rootsKey = string.Join("|", new List<string>(roots).ToArray());
            lock (_gate)
            {
                TimeSpan age = _utcNow() - _builtUtc;
                if (_paths == null || !string.Equals(_rootsKey, rootsKey, StringComparison.OrdinalIgnoreCase) ||
                    age < TimeSpan.Zero || age >= RefreshInterval)
                    Rebuild(roots, rootsKey);

                string path = ExistingPath(threadId);
                if (path.Length != 0) return path;
                if (_utcNow() - _builtUtc >= MissingRetryInterval)
                {
                    Rebuild(roots, rootsKey);
                    return ExistingPath(threadId);
                }
                return string.Empty;
            }
        }

        private string ExistingPath(string threadId)
        {
            string path;
            return _paths.TryGetValue(threadId, out path) && !string.IsNullOrWhiteSpace(path) && _exists(path)
                ? path : string.Empty;
        }

        private void Rebuild(IList<string> roots, string rootsKey)
        {
            // Publish only a completed scan; failed I/O must not cache a miss.
            Dictionary<string, string> paths = _scan(roots);
            _paths = paths;
            _rootsKey = rootsKey;
            _builtUtc = _utcNow();
        }
    }
}
