using FileRelay.Core.Models;

namespace FileRelay.Core.Interfaces;

public interface ITransferStateStore
{
    Task<TransferState> GetOrCreateAsync(TransferNegotiateRequest request, int serverChunkSizeMB);
    Task<TransferState?> GetAsync(Guid transferId);
    Task ConfirmChunkAsync(Guid transferId, int chunkIndex, string chunkHash);
    Task<IReadOnlyList<int>> GetMissingChunksAsync(Guid transferId);

    /// <summary>
    /// Atomically claims run <paramref name="runIndex"/> for the given chunk.
    /// Succeeds only when the stored run index is exactly <paramref name="runIndex"/> - 1
    /// (i.e. this is the next expected run). Returns false if another request has already
    /// claimed this run, preventing any duplicate or replayed upload from proceeding.
    /// </summary>
    Task<bool> TryClaimChunkAsync(Guid transferId, int chunkIndex, int runIndex);

    /// <summary>
    /// Returns the highest claimed run index for every chunk that has been attempted at
    /// least once. Used by negotiate to tell resuming clients which run index to use next
    /// (claimed + 1) for any chunk that was claimed but not confirmed.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> GetClaimedRunIndexesAsync(Guid transferId);

    /// <summary>
    /// Atomically marks the transfer complete. Returns true if this call was the one that
    /// flipped the flag; false if it was already complete (concurrent finalizer got there
    /// first). Callers that receive false should skip finalization — another request owns it.
    /// </summary>
    Task<bool> MarkCompleteAsync(Guid transferId);

    Task PruneCompletedAsync(TimeSpan retention);
    Task<IReadOnlyList<TransferState>> GetInactiveIncompleteTransfersAsync(TimeSpan inactivityThreshold);
    Task DeleteTransferStateAsync(Guid transferId);
}
