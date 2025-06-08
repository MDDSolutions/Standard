using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    /// <summary>
    /// BatchProcessor provides a thread-safe, background batch processing engine for collections of items.
    /// It supports parallel processing, progress reporting, and both synchronous and asynchronous batch queuing.
    /// </summary>
    /// <typeparam name="TBatch">
    /// The batch type, which must implement <see cref="IBatch{TItem}"/> and provide a collection of items to process.
    /// </typeparam>
    /// <typeparam name="TItem">
    /// The type of item to process within each batch.
    /// </typeparam>
    /// <remarks>
    /// <para>
    /// Usage:
    /// <list type="number">
    ///   <item>Instantiate <c>BatchProcessor</c> and set <see cref="WorkAction"/> to define how each item is processed.</item>
    ///   <item>Optionally set <see cref="ProgressReporter"/> to receive progress updates (see thread safety note below).</item>
    ///   <item>Enqueue batches using <see cref="EnqueueBatchAsync"/> (await for completion) or <see cref="EnqueueBatch"/> (fire-and-forget).</item>
    ///   <item>setting ReportInterval to 0 ensures that <see cref="ProgressReporter"/> is fired for every item processed</item>
    /// </list>
    /// </para>
    /// <para>
    /// Progress Reporting:
    /// <br/>
    /// The <see cref="ProgressReporter"/> delegate is invoked from worker threads and receives the processor instance.
    /// It can access thread-safe properties such as <see cref="CurrentBatch"/>, <see cref="CurrentBatchSize"/>, and <see cref="CurrentBatchItemProcessedCount"/>.
    /// <b>Do not mutate the batch or its items during processing.</b> If you update UI or shared state, ensure your code is thread-safe.
    /// </para>
    /// <para>
    /// Thread Safety:
    /// <br/>
    /// - All internal state is managed for thread safety.
    /// - <see cref="WorkAction"/> and <see cref="ProgressReporter"/> may be called from multiple threads in parallel.
    /// - If <typeparamref name="TItem"/> is mutable or shared, ensure your processing logic is thread-safe.
    /// </para>
    /// <para>
    /// Disposal:
    /// <br/>
    /// Call <see cref="Dispose"/> or <see cref="Stop"/> to stop processing and release resources.
    /// </para>
    /// </remarks>
    public class BatchProcessor<TBatch, TItem> : IDisposable
        where TBatch : class, IBatch<TItem>
    {
        private readonly BlockingCollection<BatchWork> _queue = new BlockingCollection<BatchWork>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _worker;
        private int _degreeOfParallelism = Environment.ProcessorCount;
        private readonly object _workerLock = new object();
        private int _idleTimeout = 1000;
        private BatchWork work = null;
        private int lastReportTick = 0;

        // Properties to expose current state
        public TBatch CurrentBatch => work?.Batch;
        public int BatchesInQueue => _queue.Count;
        private int currentBatchSize;
        public int CurrentBatchSize => Interlocked.CompareExchange(ref currentBatchSize, 0, 0);
        private int currentBatchItemProcessedCount;
        public int CurrentBatchItemProcessedCount => Interlocked.CompareExchange(ref currentBatchItemProcessedCount, 0, 0);
        public BatchStats BatchStats { get; set; }


        // Properties to configure on setup
        public Action<TItem, CancellationToken> WorkAction { get; set; }
        public event Action<BatchProcessor<TBatch, TItem>, BatchProgressType, TItem> ProgressEvent;
        public int ReportInterval { get; set; } = 1000;

        public int DegreeOfParallelism
        {
            get => _degreeOfParallelism;
            set => _degreeOfParallelism = value > 0 ? value : Environment.ProcessorCount;
        }

        public int IdleTimeout
        {
            get => _idleTimeout;
            set => _idleTimeout = value > 0 ? value : 1000;
        }

        private class BatchWork
        {
            public TBatch Batch { get; set; }
            public TaskCompletionSource<object> Completion { get; } = new TaskCompletionSource<object>();
        }
        private void EnsureWorker()
        {
            lock (_workerLock)
            {
                if (_worker == null || _worker.IsCompleted)
                {
                    _worker = Task.Run(() => ConsumerLoop(), _cts.Token);
                }
            }
        }
        private void ConsumerLoop()
        {
            int processed = 0;
            while (!_cts.IsCancellationRequested)
            {
                work = null;
                Interlocked.Exchange(ref currentBatchItemProcessedCount, 0);
                Interlocked.Exchange(ref currentBatchSize, 0);
                try
                {
                    if (!_queue.TryTake(out work, _idleTimeout, _cts.Token))
                    {
                        // Timed out waiting for work, exit the consumer
                        lock (_workerLock)
                        {
                            _worker = null;
                        }
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    // If a batch was being processed, mark it as canceled
                    if (work != null)
                        work.Completion.SetCanceled();
                    break;
                }

                if (work != null)
                {
                    try
                    {
                        var items = work.Batch.Items.ToList();
                        Interlocked.Exchange(ref currentBatchSize, items.Count);
                        Interlocked.Exchange(ref currentBatchItemProcessedCount, 0);

                        ProgressEvent?.Invoke(this, BatchProgressType.BatchStarting, default);

                        BatchStats = new BatchStats();

                        Parallel.For(0, items.Count,
                            new ParallelOptions { MaxDegreeOfParallelism = _degreeOfParallelism, CancellationToken = _cts.Token },
                            () => (ThreadId: Thread.CurrentThread.ManagedThreadId, Count: 0, StartTick: Environment.TickCount),
                            (idx, state, local) =>
                            {
                                WorkAction(items[idx], _cts.Token);
                                local.Count++;
                                Interlocked.Increment(ref currentBatchItemProcessedCount);
                                if (ReportInterval == 0 || Environment.TickCount - Interlocked.CompareExchange(ref lastReportTick, 0, 0) >= ReportInterval)
                                {
                                    ProgressEvent?.Invoke(this, BatchProgressType.BatchInProgress, items[idx]); // Report progress at intervals
                                    Interlocked.Exchange(ref lastReportTick, Environment.TickCount);
                                }
                                return local;
                            },
                            local =>
                            {
                                BatchStats.ThreadStats.Add(new BatchThreadStats(local.ThreadId, local.Count, local.StartTick, Environment.TickCount));
                            });

                        work.Completion.SetResult(null);
                        ProgressEvent?.Invoke(this, BatchProgressType.BatchCompleted, default); // Final progress report for the batch
                        processed++;
                    }
                    catch (OperationCanceledException)
                    {
                        work.Completion.SetCanceled();
                    }
                    catch (Exception ex)
                    {
                        work.Completion.SetException(ex);
                    }
                }
            }
        }
        public Task EnqueueBatchAsync(TBatch batch)
        {
            var work = new BatchWork { Batch = batch };
            _queue.Add(work);
            EnsureWorker();
            return work.Completion.Task;
        }
        public void EnqueueBatch(TBatch batch)
        {
            var work = new BatchWork { Batch = batch };
            _queue.Add(work);
            EnsureWorker();
        }

        public void Stop()
        {
            _queue.CompleteAdding();
            _cts.Cancel();
            lock (_workerLock)
            {
                _worker?.Wait();
            }
        }

        public void Dispose()
        {
            Stop();
            _queue.Dispose();
            _cts.Dispose();
        }
    }
    public enum BatchProgressType
    {
        BatchStarting,
        BatchInProgress,
        BatchCompleted
    }
    public class BatchStats
    {
        public int TotalElapsed => ThreadStats.IsEmpty ? 0 : ThreadStats.Max(ts => ts.StopTick) - ThreadStats.Min(ts => ts.StartTick);
        public int TotalItemsProcessed => ThreadStats.IsEmpty ? 0 : ThreadStats.Sum(ts => ts.Count);
        public ConcurrentBag<BatchThreadStats> ThreadStats { get; set; } = new ConcurrentBag<BatchThreadStats>();
        public IEnumerable<BatchThreadStats> DistinctThreads => ThreadStats.IsEmpty ? null : ThreadStats.GroupBy(ts => ts.ThreadId)
            .Select(g => new BatchThreadStats(g.Key, g.Sum(ts => ts.Count), g.Min(ts => ts.StartTick), g.Max(ts => ts.StopTick)));
        public string Report()
        {
            var sb = new StringBuilder();
            var distinctThreads = DistinctThreads.ToList();
            sb.AppendLine($"Total threads used: {distinctThreads.Count} Total Items Processed: {TotalItemsProcessed} TotalElapsed: {TotalElapsed:N0} ms {TotalItemsProcessed / (Convert.ToDouble(TotalElapsed) / 1000):N1} deletions per second");
            foreach (var thread in distinctThreads.OrderBy(ts => ts.ThreadId))
            {
                sb.AppendLine($"Thread {thread.ThreadId} processed {thread.Count} items in {thread.StopTick - thread.StartTick:N0} ms ({thread.Count / (Convert.ToDouble(thread.StopTick - thread.StartTick) / 1000):N1} items/sec)");
            }
            return sb.ToString();
        }
    }
    public class BatchThreadStats
    {
        public int ThreadId { get; set; }
        public int Count { get; set; }
        public int StartTick { get; set; }
        public int StopTick { get; set; }
        public BatchThreadStats(int threadId, int count, int startTick, int stopTick)
        {
            ThreadId = threadId;
            Count = count;
            StartTick = startTick;
            StopTick = stopTick;
        }
    }
    public interface IBatch<TItem>
    {
        IList<TItem> Items { get; }
    }
}