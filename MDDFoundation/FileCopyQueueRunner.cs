using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    public class FileCopyQueueRunner
    {
        public static FileCopyQueueRunner Default { get; set; } = new FileCopyQueueRunner();
        public FileCopyQueueRunner(Func<FileCopyProgress, Task<FileCopyProgress>>? executor = null)
        {
            if (executor == null)
                CopyExecutor = FileCopyExtensions.CopyToAsync;
            else
                CopyExecutor = executor;
        }
        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
            where T : class
        {
            public static readonly ReferenceEqualityComparer<T> Instance = new();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private sealed class QueueJob
        {
            public FileCopyProgress CopyProgress { get; set; } = null!;
            public int Priority { get; set; }
            public CancellationTokenSource LinkedCts { get; set; } = null!;
            public Action<FileCopyProgress>? OriginalCallback { get; set; }
        }

        private readonly object _gate = new();

        private readonly ConcurrentPriorityQueue<QueueJob> _queue = new();

        private readonly ConcurrentDictionary<FileCopyProgress, QueueJob> _pending =
            new(ReferenceEqualityComparer<FileCopyProgress>.Instance);

        private readonly ConcurrentDictionary<FileCopyProgress, QueueJob> _inFlight =
            new(ReferenceEqualityComparer<FileCopyProgress>.Instance);

        private int _maxParallelOperations = 1;
        private int _runningWorkerCount = 0;

        // 1 = idle, 0 = not idle
        private int _idleState = 1;

        /// <summary>
        /// Required. Point this at your actual copy method.
        /// Example: FileCopyQueueRunner.CopyExecutor = FileHelper.CopyToAsync;
        /// </summary>
        public Func<FileCopyProgress, Task<FileCopyProgress>> CopyExecutor { get; set; }

        /// <summary>
        /// Defaults to deduping on SourceFile.FullName, case-insensitive.
        /// Return true if the incoming job should be considered a duplicate of an existing job.
        /// </summary>
        public Func<FileCopyProgress, FileCopyProgress, bool> DedupePredicate { get; set; } = DefaultDedupePredicate;

        /// <summary>
        /// Optional sink for exceptions thrown by event subscribers or per-job callbacks.
        /// The queue continues running even if this fires.
        /// </summary>
        public Action<Exception>? ListenerExceptionSink { get; set; }

        public event Action<FileCopyProgress>? Started;
        public event Action<FileCopyProgress>? Progress;
        public event Action<FileCopyProgress>? Completed;
        public event Action? QueueIdle;

        public int MaxParallelOperations
        {
            get => Volatile.Read(ref _maxParallelOperations);
            set
            {
                if (value < 0) value = 0;
                Interlocked.Exchange(ref _maxParallelOperations, value);
                EnsureWorkerThreads();
                CheckForIdleTransition();
            }
        }

        public int PendingCount => _pending.Count;
        public int InFlightCount => _inFlight.Count;

        public List<FileCopyProgress> QueueItems
        {
            get
            {
                lock (_gate)
                {
                    return _queue.Values.Select(x => x.CopyProgress).ToList();
                }
            }
        }

        public List<FileCopyProgress> InFlightItems
        {
            get
            {
                lock (_gate)
                {
                    return _inFlight.Values.Select(x => x.CopyProgress).ToList();
                }
            }
        }

        public List<FileCopyProgress> AllPendingItems
        {
            get
            {
                lock (_gate)
                {
                    var r = new List<FileCopyProgress>();
                    r.AddRange(_inFlight.Values.Select(x => x.CopyProgress));
                    r.AddRange(_queue.Values.Select(x => x.CopyProgress));
                    return r;
                }
            }
        }

        public int RunningWorkerCount => Volatile.Read(ref _runningWorkerCount);

        public bool IsIdle => PendingCount == 0 && InFlightCount == 0;

        public bool Enqueue(FileCopyProgress copyProgress, int priority = 0)
        {
            if (copyProgress == null) throw new ArgumentNullException(nameof(copyProgress));

            if (CopyExecutor == null)
                throw new InvalidOperationException(
                    $"{nameof(FileCopyQueueRunner)}.{nameof(CopyExecutor)} must be assigned before jobs can be enqueued.");

            QueueJob job;

            lock (_gate)
            {
                if (IsDuplicateUnsafe(copyProgress))
                    return false;

                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(copyProgress.Token);

                job = new QueueJob
                {
                    CopyProgress = copyProgress,
                    Priority = priority,
                    LinkedCts = linkedCts
                };

                job.OriginalCallback = copyProgress.Callback;
                copyProgress.Callback = p =>
                {
                    InvokeSafely(() => job.OriginalCallback?.Invoke(p), "FileCopyProgress.Callback", p);
                    InvokeSafely(() => Progress?.Invoke(p), nameof(Progress), p);
                };

                // Replace the job token with the linked token so queue-level Cancel() works.
                copyProgress.Token = linkedCts.Token;

                if (!_pending.TryAdd(copyProgress, job))
                {
                    linkedCts.Dispose();
                    return false;
                }

                _queue.Enqueue(job, priority);
                Interlocked.Exchange(ref _idleState, 0);
            }

            EnsureWorkerThreads();
            return true;
        }

        public bool TryRemove(FileCopyProgress copyProgress)
        {
            if (copyProgress == null) return false;

            if (!_pending.TryRemove(copyProgress, out var job))
                return false;

            job.LinkedCts.Cancel();

            // Best-effort removal from the underlying priority queue.
            _queue.TryRemove(job);

            CleanupJobCallback(job);
            DisposeJob(job);

            CheckForIdleTransition();
            return true;
        }

        public void Cancel()
        {
            // Cancel everything pending
            foreach (var kvp in _pending.ToArray())
            {
                if (_pending.TryRemove(kvp.Key, out var pendingJob))
                {
                    pendingJob.LinkedCts.Cancel();
                    _queue.TryRemove(pendingJob);
                    CleanupJobCallback(pendingJob);
                    DisposeJob(pendingJob);
                }
            }

            // Cancel everything already running
            foreach (var runningJob in _inFlight.Values.ToArray())
            {
                try
                {
                    runningJob.LinkedCts.Cancel();
                }
                catch
                {
                    // Nothing to do here. Underlying copy should observe token.
                }
            }

            CheckForIdleTransition();
        }

        private static bool DefaultDedupePredicate(FileCopyProgress incoming, FileCopyProgress existing)
        {
            var a = incoming?.SourceFile?.FullName;
            var b = existing?.SourceFile?.FullName;

            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return ReferenceEquals(incoming, existing);

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsDuplicateUnsafe(FileCopyProgress incoming)
        {
            var predicate = DedupePredicate ?? DefaultDedupePredicate;

            foreach (var existing in _pending.Keys)
            {
                if (predicate(incoming, existing))
                    return true;
            }

            foreach (var existing in _inFlight.Keys)
            {
                if (predicate(incoming, existing))
                    return true;
            }

            return false;
        }

        private void EnsureWorkerThreads()
        {
            var desired = MaxParallelOperations;
            if (desired <= 0) return;
            if (_pending.IsEmpty) return;

            while (true)
            {
                var current = Volatile.Read(ref _runningWorkerCount);
                if (current >= desired) break;

                if (Interlocked.CompareExchange(ref _runningWorkerCount, current + 1, current) == current)
                {
                    _ = Task.Run(WorkerLoopAsync);
                }
            }
        }

        private async Task WorkerLoopAsync()
        {
            try
            {
                while (true)
                {
                    // Respect current throttle. Running workers above the new limit retire naturally.
                    if (Volatile.Read(ref _runningWorkerCount) > MaxParallelOperations && MaxParallelOperations >= 0)
                        return;

                    if (MaxParallelOperations == 0)
                        return;

                    if (!_queue.TryDequeue(out var job))
                        return;

                    // It may already have been removed/cancelled.
                    if (!_pending.TryRemove(job.CopyProgress, out var liveJob))
                        continue;

                    // If we somehow dequeued a stale wrapper, use the live one.
                    job = liveJob;

                    if (job.LinkedCts.IsCancellationRequested)
                    {
                        CleanupJobCallback(job);
                        DisposeJob(job);
                        CheckForIdleTransition();
                        continue;
                    }

                    if (!_inFlight.TryAdd(job.CopyProgress, job))
                    {
                        CleanupJobCallback(job);
                        DisposeJob(job);
                        CheckForIdleTransition();
                        continue;
                    }

                    InvokeSafely(() => Started?.Invoke(job.CopyProgress), nameof(Started), job.CopyProgress);

                    try
                    {
                        if (CopyExecutor == null)
                            throw new InvalidOperationException(
                                $"{nameof(FileCopyQueueRunner)}.{nameof(CopyExecutor)} must be assigned before jobs can run.");

                        await CopyExecutor(job.CopyProgress).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (job.LinkedCts.IsCancellationRequested)
                    {
                        // Expected path for cancellation.
                    }
                    catch (Exception ex)
                    {
                        // Prefer the FileCopyProgress object to carry the failure state,
                        // but still make noise if the underlying method throws.
                        ReportListenerException(
                            new Exception($"Unhandled exception while copying '{job.CopyProgress?.SourceFile?.FullName}'.", ex));
                    }
                    finally
                    {
                        _inFlight.TryRemove(job.CopyProgress, out _);

                        InvokeSafely(() => Completed?.Invoke(job.CopyProgress), nameof(Completed), job.CopyProgress);

                        CleanupJobCallback(job);
                        DisposeJob(job);
                        CheckForIdleTransition();
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _runningWorkerCount);

                // In case work arrived while this worker was winding down.
                EnsureWorkerThreads();
                CheckForIdleTransition();
            }
        }

        private void CleanupJobCallback(QueueJob job)
        {
            try
            {
                if (job.CopyProgress != null)
                    job.CopyProgress.Callback = job.OriginalCallback;
            }
            catch (Exception ex)
            {
                ReportListenerException(new Exception("Failed restoring original FileCopyProgress callback.", ex));
            }
        }

        private void DisposeJob(QueueJob job)
        {
            try
            {
                job.LinkedCts.Dispose();
            }
            catch (Exception ex)
            {
                ReportListenerException(new Exception("Failed disposing linked cancellation token source.", ex));
            }
        }

        private void CheckForIdleTransition()
        {
            if (!IsIdle) return;

            if (Interlocked.Exchange(ref _idleState, 1) == 0)
            {
                InvokeSafely(() => QueueIdle?.Invoke(), nameof(QueueIdle), null);
            }
        }

        private void InvokeSafely(Action action, string source, FileCopyProgress? copyProgress)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                var wrapped = new Exception(
                    copyProgress == null
                        ? $"Exception thrown by subscriber/callback during '{source}'."
                        : $"Exception thrown by subscriber/callback during '{source}' for '{copyProgress.SourceFile?.FullName}'.",
                    ex);

                ReportListenerException(wrapped);
            }
        }

        private void ReportListenerException(Exception ex)
        {
            try
            {
                Trace.TraceError(ex.ToString());
                Debug.WriteLine(ex);
                ListenerExceptionSink?.Invoke(ex);
            }
            catch
            {
                // Last ditch: never let the queue die because the error reporter also failed.
            }
        }
    }
}