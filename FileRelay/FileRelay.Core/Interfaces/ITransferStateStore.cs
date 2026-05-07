using FileRelay.Core.Models;

namespace FileRelay.Core.Interfaces;

public interface ITransferStateStore
{
    Task<TransferState> GetOrCreateAsync(TransferNegotiateRequest request, int serverChunkSizeMB);
    Task<TransferState?> GetAsync(Guid transferId);
    Task ConfirmChunkAsync(Guid transferId, int chunkIndex, string chunkHash);
    Task<IReadOnlyList<int>> GetMissingChunksAsync(Guid transferId);
    Task MarkCompleteAsync(Guid transferId);
    Task PruneCompletedAsync(TimeSpan retention);
    Task<IReadOnlyList<TransferState>> GetInactiveIncompleteTransfersAsync(TimeSpan inactivityThreshold);
    Task DeleteTransferStateAsync(Guid transferId);
}
