using System.Collections.Concurrent;
using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;

namespace FileRelay.Core;

public class InMemoryTransferStateStore : ITransferStateStore
{
    private readonly ConcurrentDictionary<Guid, TransferState> _states = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public async Task<TransferState> GetOrCreateAsync(TransferNegotiateRequest request, int serverChunkSizeMB)
    {
        await _createLock.WaitAsync();
        try
        {
            var existing = _states.Values.FirstOrDefault(s =>
                !s.IsComplete &&
                s.Filename == request.Filename &&
                s.FileSizeBytes == request.FileSizeBytes &&
                ContextEquals(s.Context, request.Context));

            if (existing != null) return existing;

            var chunkSizeBytes = (long)serverChunkSizeMB * 1024 * 1024;
            var state = new TransferState
            {
                TransferId = Guid.NewGuid(),
                Filename = request.Filename,
                FileSizeBytes = request.FileSizeBytes,
                FileHash = request.FileHash,
                Context = request.Context,
                ChunkSizeBytes = chunkSizeBytes,
                TotalChunks = ChunkMath.TotalChunks(request.FileSizeBytes, chunkSizeBytes),
                CreatedAt = DateTime.UtcNow
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

    public Task ConfirmChunkAsync(Guid transferId, int chunkIndex)
    {
        if (_states.TryGetValue(transferId, out var state))
            lock (state.ConfirmedChunks)
                state.ConfirmedChunks.Add(chunkIndex);
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

    public Task MarkCompleteAsync(Guid transferId)
    {
        if (_states.TryGetValue(transferId, out var state))
            state.IsComplete = true;
        return Task.CompletedTask;
    }

    private static bool ContextEquals(TransferContext? a, TransferContext? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Equals(b);
    }
}
