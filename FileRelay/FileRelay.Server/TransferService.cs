using System.Security.Cryptography;
using FileRelay.Core;
using FileRelay.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace FileRelay.Server;

public class TransferService
{
    private readonly ChunkedTransferOptions _options;
    private readonly ILogger<TransferService> _logger;

    public TransferService(ChunkedTransferOptions options, ILogger<TransferService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<TransferNegotiateResponse> NegotiateAsync(TransferNegotiateRequest request, CancellationToken ct)
    {
        var state = await _options.StateStore.GetOrCreateAsync(request, _options.ChunkSizeMB);
        var missing = await _options.StateStore.GetMissingChunksAsync(state.TransferId);

        if (missing.Count == state.TotalChunks)
        {
            // New transfer — initialize targets
            foreach (var target in _options.Targets)
                await target.InitializeAsync(state.TransferId, state.Filename, state.FileSizeBytes, state.Context, ct);
        }

        return new TransferNegotiateResponse
        {
            TransferId = state.TransferId,
            ChunkSizeMB = _options.ChunkSizeMB,
            TotalChunks = state.TotalChunks,
            ChunksNeeded = missing
        };
    }

    public async Task<TransferStatusResponse> GetStatusAsync(Guid transferId, CancellationToken ct)
    {
        var state = await _options.StateStore.GetAsync(transferId)
            ?? throw new KeyNotFoundException($"Transfer {transferId} not found.");

        var missing = await _options.StateStore.GetMissingChunksAsync(transferId);

        return new TransferStatusResponse
        {
            TransferId = state.TransferId,
            Filename = state.Filename,
            ChunksTotal = state.TotalChunks,
            ChunksConfirmed = state.TotalChunks - missing.Count,
            ChunksNeeded = missing,
            IsComplete = state.IsComplete
        };
    }

    public async Task<ChunkUploadResult> UploadChunkAsync(HttpContext context, Guid transferId, int chunkIndex, CancellationToken ct)
    {
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature != null && !sizeFeature.IsReadOnly)
            sizeFeature.MaxRequestBodySize = null;

        var state = await _options.StateStore.GetAsync(transferId);
        if (state == null) return ChunkUploadResult.NotFound();
        if (state.IsComplete) return ChunkUploadResult.AlreadyConfirmed();

        var missing = await _options.StateStore.GetMissingChunksAsync(transferId);
        if (!missing.Contains(chunkIndex)) return ChunkUploadResult.AlreadyConfirmed();

        var contentLength = context.Request.ContentLength;
        if (contentLength == null || contentLength < 32)
            return ChunkUploadResult.BadRequest("Content-Length required and must be at least 32.");

        var dataLength = contentLength.Value - 32;
        var offset = ChunkMath.ChunkOffset(chunkIndex, state.ChunkSizeBytes);

        var writers = new List<Stream>(_options.Targets.Count);
        try
        {
            foreach (var target in _options.Targets)
                writers.Add(await target.OpenChunkWriterAsync(transferId, chunkIndex, offset, ct));

            string actualHash;
            byte[] clientHash;
            using (var sha = SHA256.Create())
                (actualHash, clientHash) = await TeeWithHash(context.Request.Body, writers, sha, dataLength, ct);

            foreach (var w in writers) await w.DisposeAsync();
            writers.Clear();

            var expectedHash = $"sha256:{Convert.ToBase64String(clientHash)}";
            if (actualHash != expectedHash)
                return ChunkUploadResult.BadRequest($"Hash mismatch: expected {expectedHash}, got {actualHash}.");

            await _options.StateStore.ConfirmChunkAsync(transferId, chunkIndex);

            var remainingMissing = await _options.StateStore.GetMissingChunksAsync(transferId);
            if (remainingMissing.Count == 0)
            {
                _ = Task.Run(() => CompleteTransferAsync(state, CancellationToken.None), CancellationToken.None);
                return ChunkUploadResult.Ok(isComplete: true);
            }

            return ChunkUploadResult.Ok(isComplete: false);
        }
        finally
        {
            foreach (var w in writers) await w.DisposeAsync();
        }
    }

    private async Task CompleteTransferAsync(TransferState state, CancellationToken ct)
    {
        try
        {
            foreach (var target in _options.Targets)
                await target.FinalizeAsync(state.TransferId, ct);

            await _options.StateStore.MarkCompleteAsync(state.TransferId);

            if (_options.OnComplete != null)
            {
                var completed = new CompletedTransfer
                {
                    TransferId = state.TransferId,
                    Filename = state.Filename,
                    FileSizeBytes = state.FileSizeBytes,
                    FileHash = state.FileHash,
                    CompletedAt = DateTime.UtcNow
                };
                await _options.OnComplete.OnCompleteAsync(completed, ct);
            }

            _logger.LogInformation("Transfer {TransferId} ({Filename}) complete.", state.TransferId, state.Filename);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing transfer {TransferId}.", state.TransferId);
        }
    }

    private async Task<(string ActualHash, byte[] ClientHash)> TeeWithHash(
        Stream source, List<Stream> destinations, SHA256 sha, long dataLength, CancellationToken ct)
    {
        var buffer = new byte[81920];
        var remaining = dataLength;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), ct);
            if (read == 0) break;
            sha.TransformBlock(buffer, 0, read, null, 0);
            foreach (var dest in destinations)
                await dest.WriteAsync(buffer, 0, read, ct);
            remaining -= read;

            if (_options.SimulatedWanDelayPerBufferMs > 0)
                await Task.Delay(_options.SimulatedWanDelayPerBufferMs, ct);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var actualHash = $"sha256:{Convert.ToBase64String(sha.Hash!)}";

        var clientHash = new byte[32];
        await source.ReadExactlyAsync(clientHash, ct);
        return (actualHash, clientHash);
    }
}

public class ChunkUploadResult
{
    public int StatusCode { get; private init; }
    public string? Error { get; private init; }
    public bool IsComplete { get; private init; }

    public static ChunkUploadResult Ok(bool isComplete) => new() { StatusCode = 200, IsComplete = isComplete };
    public static ChunkUploadResult AlreadyConfirmed() => new() { StatusCode = 409, Error = "Chunk already confirmed." };
    public static ChunkUploadResult NotFound() => new() { StatusCode = 404, Error = "Transfer not found." };
    public static ChunkUploadResult BadRequest(string error) => new() { StatusCode = 400, Error = error };
}
