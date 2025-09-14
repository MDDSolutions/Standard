using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public class RichLogEntry
    {
        public DateTime Timestamp { get; set; }
        public string Source { get; set; }
        public byte Severity { get; set; }
        public string Message { get; set; }
        public string AssemblyName { get; set; }
        public string ClassName { get; set; }
        public string MethodName { get; set; }
        public string Details { get; set; }
        override public string ToString() => $"Log Entry: {Timestamp:HH:mm:ss} [{Severity}] {Source} - {Message}";

    }

    public class RichLog
    {
        private readonly List<RichLogEntry> _entries = new List<RichLogEntry>();
        private readonly ConcurrentQueue<RichLogEntry> _flushQueue = new ConcurrentQueue<RichLogEntry>();
        private readonly object _syncRoot = new object();

        private readonly string _logFilePath;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _flushTask;
        private readonly AutoResetEvent _flushSignal = new AutoResetEvent(false);

        // Subscribers with filters
        private readonly List<(Func<RichLogEntry, bool> Filter, EventHandler<RichLogEntry> Handler)> _subscribers = new List<(Func<RichLogEntry, bool>, EventHandler<RichLogEntry>)>();
        public string LogName { get; set; }
        public RichLog(string name, string logFilePath)
        {
            LogName = name;
            if (!string.IsNullOrWhiteSpace(logFilePath))
            {
                // if logFilePath is relative, make it relative to the executing assembly location
                if (!Path.IsPathRooted(logFilePath))
                {
                    var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                    logFilePath = Path.Combine(exeDir, logFilePath);
                }
                _logFilePath = logFilePath;
            }
            _activeLogs.Add(this);
        }
        private static readonly List<RichLog> _activeLogs = new List<RichLog>();
        public static IReadOnlyList<RichLog> ActiveLogs { get; } = _activeLogs.AsReadOnly();

        public void Entry(RichLogEntry entry, int skipframes = 1)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            if (entry.AssemblyName == null || entry.ClassName == null || entry.MethodName == null)
            {
                var frame = new StackFrame(skipframes, false);
                var method = frame.GetMethod();
                entry.AssemblyName = method?.DeclaringType?.Assembly.GetName().Name;
                entry.ClassName = method?.DeclaringType?.FullName;
                entry.MethodName = method?.Name;
            }

            lock (_syncRoot)
            {
                _entries.Add(entry);
            }
            _flushQueue.Enqueue(entry);
            _flushSignal.Set();
            // Notify subscribers that match
            lock (_subscribers)
            {
                foreach (var sub in _subscribers)
                {
                    if (sub.Filter(entry))
                    {
                        sub.Handler?.Invoke(this, entry);
                    }
                }
            }
            EnsureFlushLoopRunning();
        }

        public void Entry(string source, byte severity, string message, string details, int skipframes = 1)
        {
            var frame = new StackFrame(skipframes, false);
            var method = frame.GetMethod();

            var entry = new RichLogEntry
            {
                Timestamp = DateTime.Now,
                Source = source,
                Severity = severity,
                Message = message,
                AssemblyName = method?.DeclaringType?.Assembly.GetName().Name,
                ClassName = method?.DeclaringType?.FullName,
                MethodName = method?.Name,
                Details = details
            };

            Entry(entry);
        }

        private void EnsureFlushLoopRunning()
        {
            if (_logFilePath != null && (_flushTask == null || _flushTask.IsCompleted))
            {
                _cts = new CancellationTokenSource();
                _flushTask = Task.Run(() => FlushLoop(_cts.Token));
            }
        }

        private async Task FlushLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    _flushSignal.WaitOne();

                    FlushPending();

                    if (_flushQueue.IsEmpty)
                    {
                        break; // exit until restarted
                    }

                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on stop
            }
        }

        private void FlushPending()
        {
            var sb = new StringBuilder();

            while (_flushQueue.TryDequeue(out var entry))
            {
                sb.AppendLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.ff} [{entry.Severity}] {entry.Source} - {entry.Message} ({entry.AssemblyName}.{entry.ClassName}.{entry.MethodName})");
            }

            if (sb.Length > 0)
            {
                File.AppendAllText(_logFilePath, sb.ToString());
            }
        }

        public void Stop()
        {
            _cts.Cancel();
            _flushSignal.Set();
            if (_flushTask != null) _flushTask.Wait();
            FlushPending();
        }

        // ============ Query API ============
        public IEnumerable<RichLogEntry> Query(
            byte? minSeverity = null,
            DateTime? from = null,
            DateTime? to = null,
            string source = null,
            string assembly = null,
            string className = null,
            string method = null,
            string keyword = null,
            Type entryType = null)
        {
            lock (_syncRoot)
            {
                return _entries.Where(e =>
                    (!minSeverity.HasValue || e.Severity >= minSeverity.Value) &&
                    (!from.HasValue || e.Timestamp >= from.Value) &&
                    (!to.HasValue || e.Timestamp <= to.Value) &&
                    (source == null || e.Source == source) &&
                    (assembly == null || e.AssemblyName == assembly) &&
                    (className == null || e.ClassName == className) &&
                    (method == null || e.MethodName == method) &&
                    (keyword == null || e.Message.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0) &&
                    (entryType == null || entryType.IsAssignableFrom(e.GetType()))
                ).ToList();
            }
        }
        public IEnumerable<TEntry> Query<TEntry>(
            byte? minSeverity = null,
            DateTime? from = null,
            DateTime? to = null,
            string source = null,
            string assembly = null,
            string className = null,
            string method = null,
            string keyword = null)
            where TEntry : RichLogEntry
        {
            return Query(minSeverity, from, to, source, assembly, className, method, keyword, typeof(TEntry))
                .Cast<TEntry>()
                .ToList();
        }



        // ============ Subscription API ============

        public void Subscribe(Func<RichLogEntry, bool> filter, EventHandler<RichLogEntry> handler)
        {
            lock (_subscribers)
            {
                _subscribers.Add((filter, handler));
            }
        }
        public void Subscribe<TEntry>(Func<TEntry, bool> filter, EventHandler<TEntry> handler) where TEntry : RichLogEntry
        {
            Func<RichLogEntry, bool> wrappedFilter = e => e is TEntry te && (filter?.Invoke(te) ?? true);

            EventHandler<RichLogEntry> wrappedHandler = (s, e) =>
            {
                if (e is TEntry te)
                    handler?.Invoke(s, te);
            };

            lock (_subscribers)
            {
                _subscribers.Add((wrappedFilter, wrappedHandler));
            }
        }


        public void Unsubscribe(EventHandler<RichLogEntry> handler)
        {
            lock (_subscribers)
            {
                _subscribers.RemoveAll(s => s.Handler == handler);
            }
        }
    }
}
