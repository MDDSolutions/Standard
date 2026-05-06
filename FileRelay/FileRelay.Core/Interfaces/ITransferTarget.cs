namespace FileRelay.Core.Interfaces;

public interface ITransferTarget
{
    Task InitializeAsync(Guid transferId, string filename, long fileSizeBytes, TransferContext? context, CancellationToken ct);
    Task<Stream> OpenChunkWriterAsync(Guid transferId, int chunkIndex, long offset, CancellationToken ct);
    Task FinalizeAsync(Guid transferId, CancellationToken ct);
    Task AbortAsync(Guid transferId, CancellationToken ct);
}
