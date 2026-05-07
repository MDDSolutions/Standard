namespace FileRelay.Core;

/// <summary>
/// Token-bucket bandwidth limiter. Shared across all concurrent uploads in a process
/// to enforce a smooth aggregate throughput target. Lower priority numbers are served first.
/// </summary>
public sealed class BandwidthLimiter : IDisposable
{
    private readonly long _bytesPerSecond;
    private readonly long _refillAmount;   // bytes added every 50 ms
    private readonly long _bucketCapacity; // max burst = 1 second of target rate
    private long _available;

    private readonly object _gate = new();
    private readonly List<PendingAcquire> _waiters = new List<PendingAcquire>();
    private readonly Timer _refillTimer;
    private bool _disposed;

    public int ParallelConnections { get; }

    public BandwidthLimiter(double targetMBps, int parallelConnections = 4)
    {
        _bytesPerSecond = (long)(targetMBps * 1024 * 1024);
        _refillAmount   = (long)(_bytesPerSecond * 0.05); // 50 ms worth of bytes
        _bucketCapacity = _bytesPerSecond;                // cap burst at 1 second
        _available      = _bucketCapacity;
        ParallelConnections = parallelConnections;
        _refillTimer = new Timer(_ => Refill(), null, 50, 50);
    }

    /// <summary>
    /// Acquires the right to send <paramref name="bytes"/> bytes. Waits if the bucket is
    /// empty, yielding to higher-priority (lower number) callers first.
    /// </summary>
    public Task AcquireAsync(int bytes, byte priority = 50, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_waiters.Count == 0 && _available >= bytes)
            {
                _available -= bytes;
                return Task.CompletedTask;
            }

            var pending = new PendingAcquire(bytes, priority, ct);

            if (ct.CanBeCanceled)
                ct.Register(() =>
                {
                    lock (_gate)
                    {
                        if (_waiters.Remove(pending))
                            pending.Tcs.TrySetCanceled(ct);
                    }
                });

            InsertSorted(pending);
            return pending.Tcs.Task;
        }
    }

    private void Refill()
    {
        List<TaskCompletionSource<bool>>? toSignal = null;

        lock (_gate)
        {
            _available = Math.Min(_available + _refillAmount, _bucketCapacity);

            for (var i = 0; i < _waiters.Count; )
            {
                var w = _waiters[i];

                if (w.Ct.IsCancellationRequested)
                {
                    _waiters.RemoveAt(i);
                    w.Tcs.TrySetCanceled(w.Ct);
                    continue;
                }

                if (_available >= w.Bytes)
                {
                    _available -= w.Bytes;
                    _waiters.RemoveAt(i);
                    (toSignal ??= new()).Add(w.Tcs);
                }
                else
                {
                    break; // highest-priority waiter can't be satisfied — stop here
                }
            }
        }

        if (toSignal != null)
            foreach (var tcs in toSignal)
                tcs.TrySetResult(true);
    }

    private void InsertSorted(PendingAcquire pending)
    {
        // Stable insert: after any existing waiters with the same priority (FIFO within a priority).
        var i = _waiters.Count;
        while (i > 0 && _waiters[i - 1].Priority > pending.Priority)
            i--;
        _waiters.Insert(i, pending);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refillTimer.Dispose();
        lock (_gate)
        {
            foreach (var w in _waiters)
                w.Tcs.TrySetCanceled();
            _waiters.Clear();
        }
    }

    private sealed class PendingAcquire
    {
        public readonly int Bytes;
        public readonly byte Priority;
        public readonly CancellationToken Ct;
        public readonly TaskCompletionSource<bool> Tcs;

        public PendingAcquire(int bytes, byte priority, CancellationToken ct)
        {
            Bytes = bytes;
            Priority = priority;
            Ct = ct;
            Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
