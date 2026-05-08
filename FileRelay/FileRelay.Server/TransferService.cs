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
    private readonly BandwidthLimiter? _serverThrottle;

    public TransferService(ChunkedTransferOptions options, ILogger<TransferService> logger)
    {
        _options = options;
        _logger = logger;
        _serverThrottle = options.ServerReceiveMBps > 0
            ? new BandwidthLimiter(options.ServerReceiveMBps)
            : null;
    }

    public async Task<TransferNegotiateResponse> NegotiateAsync(TransferNegotiateRequest request, CancellationToken ct)
    {
        var state = await _options.StateStore.GetOrCreateAsync(request, _options.ChunkSizeMB);
        var missing = await _options.StateStore.GetMissingChunksAsync(state.TransferId);

        // If resuming, verify the .partial file is still intact on every target.
        // A missing or wrong-size partial means confirmed chunk data is gone — we must restart
        // rather than write only the remaining chunks into a fresh empty file and assemble garbage.
        if (missing.Count < state.TotalChunks)
        {
            foreach (var target in _options.Targets)
            {
                if (!await target.IsPartialIntactAsync(state.TransferId, state.Filename, state.FileSizeBytes, state.Context, ct))
                {
                    _logger.LogWarning("Transfer {TransferId} ({Filename}): partial file missing or corrupt, restarting transfer.", state.TransferId, state.Filename);
                    await _options.StateStore.DeleteTransferStateAsync(state.TransferId);
                    state = await _options.StateStore.GetOrCreateAsync(request, _options.ChunkSizeMB);
                    missing = await _options.StateStore.GetMissingChunksAsync(state.TransferId);
                    break;
                }
            }
        }

        // Idempotent: registers the in-memory path mapping and creates the .partial only if absent.
        foreach (var target in _options.Targets)
            await target.InitializeAsync(state.TransferId, state.Filename, state.FileSizeBytes, state.Context, ct);

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
            try
            {
                foreach (var target in _options.Targets)
                    writers.Add(await target.OpenChunkWriterAsync(transferId, chunkIndex, offset, ct));
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                _logger.LogWarning("Transfer {TransferId}: partial file missing, deleting state.", transferId);
                await _options.StateStore.DeleteTransferStateAsync(transferId);
                return ChunkUploadResult.NotFound();
            }

            string actualHash;
            byte[] clientHash;
            using (var sha = SHA256.Create())
                (actualHash, clientHash) = await TeeWithHash(context.Request.Body, writers, sha, dataLength, ct);

            foreach (var w in writers) await w.DisposeAsync();
            writers.Clear();

            var expectedHash = $"sha256:{Convert.ToBase64String(clientHash)}";
            if (actualHash != expectedHash)
                return ChunkUploadResult.BadRequest($"Hash mismatch: expected {expectedHash}, got {actualHash}.");

            await _options.StateStore.ConfirmChunkAsync(transferId, chunkIndex, actualHash);

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
            if (!string.IsNullOrEmpty(state.FileHash))
            {
                foreach (var target in _options.Targets)
                    await target.VerifyAsync(state.TransferId, state.FileHash, ct);
            }

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
            if (_serverThrottle != null)
                await _serverThrottle.AcquireAsync(read, 50, ct);
            sha.TransformBlock(buffer, 0, read, null, 0);
            foreach (var dest in destinations)
                await dest.WriteAsync(buffer, 0, read, ct);
            remaining -= read;
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
