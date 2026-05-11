using System.Collections.Concurrent;
using System.Security.Cryptography;
using FileRelay.Core;
using FileRelay.Core.Interfaces;
using FileRelay.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace FileRelay.Server;

public class TransferService
{
    private readonly FileRelayOptions _options;
    private readonly ILogger<TransferService> _logger;
    private readonly BandwidthLimiter? _serverThrottle;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _completionLocks = new();

    public TransferService(FileRelayOptions options, ILogger<TransferService> logger)
    {
        _options = options;
        _logger = logger;
        _serverThrottle = options.ServerReceiveMBps > 0
            ? new BandwidthLimiter(options.ServerReceiveMBps)
            : null;
    }

    public async Task<TransferNegotiateResponse> NegotiateAsync(
        TransferNegotiateRequest request, IReadOnlyList<ITransferTarget> targets, CancellationToken ct)
    {
        TransferPathValidator.ThrowIfInvalid(request.Filename, request.Context);

        var state = await _options.StateStore.GetOrCreateAsync(request, _options.ChunkSizeMB);
        var missing = await _options.StateStore.GetMissingChunksAsync(state.TransferId);

        // If resuming, verify the .partial file is still intact on every target.
        // A missing or wrong-size partial means confirmed chunk data is gone — we must restart
        // rather than write only the remaining chunks into a fresh empty file and assemble garbage.
        if (missing.Count < state.TotalChunks)
        {
            foreach (var target in targets)
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
        foreach (var target in targets)
            await target.InitializeAsync(state.TransferId, state.Filename, state.FileSizeBytes, state.Context, ct);

        // Build sparse ChunkRunIndexes — only chunks where the next expected run is > 1 (i.e., a prior
        // attempt was made). Omitted chunks are implicitly run index 1.
        var claimedRunIndexes = await _options.StateStore.GetClaimedRunIndexesAsync(state.TransferId);
        IReadOnlyDictionary<int, int>? chunkRunIndexes = null;
        if (claimedRunIndexes.Count > 0)
        {
            var d = new Dictionary<int, int>();
            foreach (var kvp in claimedRunIndexes)
                if (kvp.Value >= 1)
                    d[kvp.Key] = kvp.Value + 1; // next expected = claimed + 1
            if (d.Count > 0) chunkRunIndexes = d;
        }

        return new TransferNegotiateResponse
        {
            TransferId      = state.TransferId,
            ChunkSizeMB     = _options.ChunkSizeMB,
            TotalChunks     = state.TotalChunks,
            ChunksNeeded    = missing,
            ChunkRunIndexes = chunkRunIndexes,
            HttpChunkPort   = _options.AllowHttpChunks && _options.HttpPort > 0
                                  ? _options.HttpPort : null
        };
    }

    public async Task<TransferStatusResponse> GetStatusAsync(Guid transferId, string appId, CancellationToken ct)
    {
        var state = await _options.StateStore.GetAsync(transferId)
            ?? throw new KeyNotFoundException($"Transfer {transferId} not found.");
        if (state.AppId != appId)
            throw new KeyNotFoundException($"Transfer {transferId} not found.");

        var missing = await _options.StateStore.GetMissingChunksAsync(transferId);

        return new TransferStatusResponse
        {
            TransferId      = state.TransferId,
            Filename        = state.Filename,
            ChunksTotal     = state.TotalChunks,
            ChunksConfirmed = state.TotalChunks - missing.Count,
            ChunksNeeded    = missing,
            IsComplete      = state.IsComplete
        };
    }

    public async Task<ChunkUploadResult> UploadChunkAsync(
        HttpContext context, Guid transferId, int chunkIndex, string appId, CancellationToken ct)
    {
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature != null && !sizeFeature.IsReadOnly)
            sizeFeature.MaxRequestBodySize = null;

        var state = await _options.StateStore.GetAsync(transferId);
        if (state == null) return ChunkUploadResult.NotFound();
        if (state.AppId != appId) return ChunkUploadResult.NotFound();
        if (state.IsComplete) return ChunkUploadResult.AlreadyConfirmed();

        var targets = GetTargets(state.AppId);

        if (state.ConfirmedChunks.Contains(chunkIndex)) return ChunkUploadResult.AlreadyConfirmed();

        long expectedDataLength;
        try
        {
            expectedDataLength = ChunkMath.ChunkLength(chunkIndex, state.FileSizeBytes, state.ChunkSizeBytes);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ChunkUploadResult.BadRequest(ex.Message);
        }
        catch (OverflowException ex)
        {
            return ChunkUploadResult.BadRequest(ex.Message);
        }

        var macKeys = context.Items.TryGetValue("ChunkMacKeys", out var mk) && mk is string[] keys
            ? keys
            : Array.Empty<string>();
        var macLength = macKeys.Length > 0 ? 32 : 0;

        var contentLength = context.Request.ContentLength;
        if (contentLength == null || contentLength < 32 + macLength)
            return ChunkUploadResult.BadRequest($"Content-Length required and must be at least {32 + macLength}.");

        var expectedContentLength = expectedDataLength + 32 + macLength;
        if (contentLength.Value != expectedContentLength)
            return ChunkUploadResult.BadRequest(
                $"Invalid chunk length: expected {expectedDataLength} data bytes plus {32 + macLength} trailer bytes, got {contentLength.Value} total bytes.");

        var runIndex = context.Items.TryGetValue("RunIndex", out var ri) && ri is int r ? r : 1;
        if (!await _options.StateStore.TryClaimChunkAsync(transferId, chunkIndex, runIndex))
            return ChunkUploadResult.ChunkClaimConflict();

        var dataLength = expectedDataLength;
        var offset = ChunkMath.ChunkOffset(chunkIndex, state.ChunkSizeBytes);

        var writers = new List<Stream>(targets.Count);
        try
        {
            try
            {
                foreach (var target in targets)
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
            byte[]? clientHashMac;
            using (var sha = SHA256.Create())
                (actualHash, clientHash, clientHashMac) = await TeeWithHash(
                    context.Request.Body, writers, sha, dataLength, macLength > 0, ct);

            foreach (var w in writers) await w.DisposeAsync();
            writers.Clear();

            var expectedHash = $"sha256:{Convert.ToBase64String(clientHash)}";
            if (actualHash != expectedHash)
                return ChunkUploadResult.BadRequest($"Hash mismatch: expected {expectedHash}, got {actualHash}.");

            if (macKeys.Length > 0 &&
                (clientHashMac == null || !macKeys.Any(key => ChunkToken.ValidateHashMac(
                    clientHashMac, key, appId, transferId, chunkIndex, runIndex, dataLength, clientHash))))
            {
                return ChunkUploadResult.BadRequest("Chunk hash HMAC mismatch.");
            }

            await _options.StateStore.ConfirmChunkAsync(transferId, chunkIndex, actualHash);

            var remainingMissing = await _options.StateStore.GetMissingChunksAsync(transferId);
            if (remainingMissing.Count > 0)
                return ChunkUploadResult.Ok(isComplete: false);

            // --- Last chunk: all completion work blocks the response from here ---

            bool finalized;
            try
            {
                finalized = await FinalizeTransferAsync(state, targets, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transfer {TransferId} ({Filename}): finalization failed.", state.TransferId, state.Filename);
                return ChunkUploadResult.InternalError("Transfer finalization failed.");
            }

            if (!finalized)
                return ChunkUploadResult.Ok(isComplete: true); // concurrent request finalized it

            var completed = new CompletedTransfer
            {
                TransferId    = state.TransferId,
                AppId         = state.AppId,
                Filename      = state.Filename,
                FileSizeBytes = state.FileSizeBytes,
                FileHash      = state.FileHash,
                CompletedAt   = DateTime.UtcNow,
                Context       = state.Context
            };

            if (_options.OnComplete != null)
            {
                bool valid;
                try { valid = await _options.OnComplete.OnValidateAsync(completed, ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Transfer {TransferId}: OnValidateAsync threw.", state.TransferId);
                    return ChunkUploadResult.InternalError("Transfer validation failed.");
                }
                if (!valid)
                {
                    _logger.LogWarning("Transfer {TransferId} ({Filename}): rejected by OnValidateAsync.", state.TransferId, state.Filename);
                    return ChunkUploadResult.ValidationFailed();
                }

                // Fire-and-forget — runs after the 200 is sent.
                _ = Task.Run(() => FireAndForgetCompleteAsync(completed), CancellationToken.None);
            }

            _logger.LogInformation("Transfer {TransferId} ({Filename}, app={AppId}) complete.", state.TransferId, state.Filename, state.AppId);
            return ChunkUploadResult.Ok(isComplete: true);
        }
        finally
        {
            foreach (var w in writers) await w.DisposeAsync();
        }
    }

    private IReadOnlyList<ITransferTarget> GetTargets(string appId)
        => _options.Users.FirstOrDefault(u => u.AppId == appId)?.Targets
            ?? Array.Empty<ITransferTarget>();

    private async Task<bool> FinalizeTransferAsync(
        TransferState state, IReadOnlyList<ITransferTarget> targets, CancellationToken ct)
    {
        var gate = _completionLocks.GetOrAdd(state.TransferId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var latestState = await _options.StateStore.GetAsync(state.TransferId)
                ?? throw new KeyNotFoundException($"Transfer {state.TransferId} not found.");
            if (latestState.IsComplete)
                return false;

            if (!string.IsNullOrEmpty(state.FileHash))
                foreach (var target in targets)
                    await target.VerifyAsync(state.TransferId, state.FileHash, ct);

            foreach (var target in targets)
                await target.FinalizeAsync(state.TransferId, ct);

            return await _options.StateStore.MarkCompleteAsync(state.TransferId);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
                _completionLocks.TryRemove(state.TransferId, out _);
        }
    }

    private async Task FireAndForgetCompleteAsync(CompletedTransfer completed)
    {
        try { await _options.OnComplete!.OnCompleteAsync(completed, CancellationToken.None); }
        catch (Exception ex) { _logger.LogError(ex, "OnCompleteAsync failed for transfer {TransferId}.", completed.TransferId); }
    }

    private async Task<(string ActualHash, byte[] ClientHash, byte[]? ClientHashMac)> TeeWithHash(
        Stream source, List<Stream> destinations, SHA256 sha, long dataLength, bool readHashMac, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024]; // match Kestrel MaxFrameSize
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

        byte[]? clientHashMac = null;
        if (readHashMac)
        {
            clientHashMac = new byte[32];
            await source.ReadExactlyAsync(clientHashMac, ct);
        }

        return (actualHash, clientHash, clientHashMac);
    }
}

public class ChunkUploadResult
{
    public int StatusCode { get; private init; }
    public string? Error { get; private init; }
    public bool IsComplete { get; private init; }

    public static ChunkUploadResult Ok(bool isComplete)       => new() { StatusCode = 200, IsComplete = isComplete };
    public static ChunkUploadResult AlreadyConfirmed()        => new() { StatusCode = 409, Error = "Chunk already confirmed." };
    public static ChunkUploadResult ChunkClaimConflict()      => new() { StatusCode = 409, Error = "Chunk run index conflict. Another request owns this run." };
    public static ChunkUploadResult NotFound()                => new() { StatusCode = 404, Error = "Transfer not found." };
    public static ChunkUploadResult BadRequest(string err)    => new() { StatusCode = 400, Error = err };
    public static ChunkUploadResult ValidationFailed()        => new() { StatusCode = 422, Error = "Transfer rejected by server." };
    public static ChunkUploadResult InternalError(string err) => new() { StatusCode = 500, Error = err };
}
