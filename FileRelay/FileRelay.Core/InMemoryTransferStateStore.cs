using System.Collections.Concurrent;
using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;

namespace FileRelay.Core;

public class InMemoryTransferStateStore : ITransferStateStore
{
    private readonly ConcurrentDictionary<Guid, TransferState> _states = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);

    // (transferId, chunkIndex) → highest claimed run index
    private readonly Dictionary<(Guid, int), int> _claimedRunIndexes = new();
    private readonly object _claimLock = new();

    public async Task<TransferState> GetOrCreateAsync(TransferNegotiateRequest request, int serverChunkSizeMB)
    {
        await _createLock.WaitAsync();
        try
        {
            var existing = _states.Values.FirstOrDefault(s =>
                !s.IsComplete &&
                s.AppId == request.AppId &&
                s.Filename == request.Filename &&
                s.FileSizeBytes == request.FileSizeBytes &&
                ContextEquals(s.Context, request.Context));

            if (existing != null) return existing;

            var chunkSizeBytes = (long)serverChunkSizeMB * 1024 * 1024;
            var state = new TransferState
            {
                TransferId     = Guid.NewGuid(),
                AppId          = request.AppId,
                Filename       = request.Filename,
                FileSizeBytes  = request.FileSizeBytes,
                FileHash       = request.FileHash,
                Context        = request.Context,
                ChunkSizeBytes = chunkSizeBytes,
                TotalChunks    = ChunkMath.TotalChunks(request.FileSizeBytes, chunkSizeBytes),
                CreatedAt      = DateTime.UtcNow,
                LastActivityAt = DateTime.UtcNow
            };
            _states[state.TransferId] = state;
            return state;
        }
        finally
        {
            _createLock.Release();
        }
    }

    public Task<TransferState?> GetAsync(Guid transferId)
        => Task.FromResult(_states.TryGetValue(transferId, out var s) ? s : null);

    public Task ConfirmChunkAsync(Guid transferId, int chunkIndex, string chunkHash)
    {
        if (_states.TryGetValue(transferId, out var state))
        {
            lock (state.ConfirmedChunks)
            {
                state.ConfirmedChunks.Add(chunkIndex);
                state.LastActivityAt = DateTime.UtcNow;
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<int>> GetMissingChunksAsync(Guid transferId)
    {
        if (!_states.TryGetValue(transferId, out var state))
            return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

        List<int> missing;
        lock (state.ConfirmedChunks)
            missing = Enumerable.Range(1, state.TotalChunks)
                .Where(i => !state.ConfirmedChunks.Contains(i))
                .ToList();

        return Task.FromResult<IReadOnlyList<int>>(missing);
    }

    public Task<bool> TryClaimChunkAsync(Guid transferId, int chunkIndex, int runIndex)
    {
        lock (_claimLock)
        {
            var key = (transferId, chunkIndex);
            _claimedRunIndexes.TryGetValue(key, out var current); // 0 if not present
            if (runIndex != current + 1) return Task.FromResult(false);
            _claimedRunIndexes[key] = runIndex;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyDictionary<int, int>> GetClaimedRunIndexesAsync(Guid transferId)
    {
        lock (_claimLock)
        {
            var result = _claimedRunIndexes
                .Where(kvp => kvp.Key.Item1 == transferId)
                .ToDictionary(kvp => kvp.Key.Item2, kvp => kvp.Value);
            return Task.FromResult<IReadOnlyDictionary<int, int>>(result);
        }
    }

    public Task<bool> MarkCompleteAsync(Guid transferId)
    {
        if (!_states.TryGetValue(transferId, out var state)) return Task.FromResult(false);
        lock (state)
        {
            if (state.IsComplete) return Task.FromResult(false);
            state.IsComplete = true;
            return Task.FromResult(true);
        }
    }

    public Task PruneCompletedAsync(TimeSpan retention)
    {
        var cutoff = DateTime.UtcNow - retention;
        foreach (var id in _states.Keys.ToList())
            if (_states.TryGetValue(id, out var s) && s.IsComplete && s.CreatedAt < cutoff)
                _states.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TransferState>> GetInactiveIncompleteTransfersAsync(TimeSpan inactivityThreshold)
    {
        var cutoff = DateTime.UtcNow - inactivityThreshold;
        var result = _states.Values
            .Where(s => !s.IsComplete && s.LastActivityAt < cutoff)
            .ToList();
        return Task.FromResult<IReadOnlyList<TransferState>>(result);
    }

    public Task DeleteTransferStateAsync(Guid transferId)
    {
        _states.TryRemove(transferId, out _);
        lock (_claimLock)
        {
            foreach (var key in _claimedRunIndexes.Keys.Where(k => k.Item1 == transferId).ToList())
                _claimedRunIndexes.Remove(key);
        }
        return Task.CompletedTask;
    }

    private static bool ContextEquals(TransferContext? a, TransferContext? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Equals(b);
    }
}
