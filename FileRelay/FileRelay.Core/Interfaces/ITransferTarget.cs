namespace FileRelay.Core.Interfaces;

public interface ITransferTarget
{
    Task InitializeAsync(Guid transferId, string filename, long fileSizeBytes, TransferContext? context, CancellationToken ct);
    Task<Stream> OpenChunkWriterAsync(Guid transferId, int chunkIndex, long offset, CancellationToken ct);

    /// <summary>
    /// Verifies the assembled file against the expected hash ("sha256:base64").
    /// Called after all chunks are written but before FinalizeAsync.
    /// Throws InvalidDataException if the hash does not match.
    /// </summary>
    Task VerifyAsync(Guid transferId, string expectedHash, CancellationToken ct);

    Task FinalizeAsync(Guid transferId, CancellationToken ct);
    Task AbortAsync(Guid transferId, CancellationToken ct);
    Task<bool> IsPartialIntactAsync(Guid transferId, string filename, long expectedSizeBytes, TransferContext? context, CancellationToken ct);
}
