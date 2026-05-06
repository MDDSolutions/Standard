using System.Net.Http.Json;
using FileRelay.Core;
using FileRelay.Core.Models;

namespace FileRelay.Client;

public class FileRelayClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public FileRelayClient(Uri baseUri)
    {
        _http = new HttpClient { BaseAddress = baseUri };
        _ownsHttp = true;
    }

    public FileRelayClient(HttpClient http)
    {
        _http = http;
        _ownsHttp = false;
    }

    public async Task UploadFileAsync(FileInfo file, UploadOptions? options = null, CancellationToken ct = default)
    {
        options ??= new UploadOptions();

        var request = new TransferNegotiateRequest
        {
            Filename = file.Name,
            FileSizeBytes = file.Length,
            ChunkSizeMB = options.PreferredChunkSizeMB,
            Context = options.Context
        };

        int attempt = 0;
        while (true)
        {
            try
            {
                var negotiate = await NegotiateAsync(request, ct);
                if (negotiate.ChunksNeeded.Count == 0) return;

                var chunkSizeBytes = (long)negotiate.ChunkSizeMB * 1024 * 1024;
                await UploadChunksAsync(file, negotiate, chunkSizeBytes, options, ct);
                return;
            }
            catch (HttpRequestException) when (attempt < options.MaxRetries && !ct.IsCancellationRequested)
            {
                attempt++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }

    private async Task<TransferNegotiateResponse> NegotiateAsync(TransferNegotiateRequest request, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("transfer/negotiate", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TransferNegotiateResponse>(ct)
            ?? throw new InvalidOperationException("Empty negotiate response.");
    }

    private async Task UploadChunksAsync(FileInfo file, TransferNegotiateResponse negotiate, long chunkSizeBytes, UploadOptions options, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        long bytesConfirmed = 0;
        long bytesInFlight = 0;
        int chunksConfirmed = 0;
        var semaphore = new SemaphoreSlim(options.ParallelConnections);

        void FireProgress()
        {
            var confirmed = Interlocked.Read(ref bytesConfirmed);
            var inFlight = Interlocked.Read(ref bytesInFlight);
            var totalSent = confirmed + inFlight;
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            var rate = elapsed > 0.5 ? totalSent / 1_048_576.0 / elapsed : 0;
            var remaining = file.Length - totalSent;
            var eta = rate > 0 ? TimeSpan.FromSeconds(remaining / 1_048_576.0 / rate) : (TimeSpan?)null;
            options.OnProgress!(new UploadProgress
            {
                ChunksDone         = Volatile.Read(ref chunksConfirmed),
                ChunksTotal        = negotiate.TotalChunks,
                BytesConfirmed     = confirmed,
                BytesInFlight      = inFlight,
                BytesTotal         = file.Length,
                TransferRateMBps   = rate,
                EstimatedRemaining = eta
            });
        }

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = options.OnProgress != null
            ? Task.Run(async () =>
            {
                while (!progressCts.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(options.ProgressIntervalSeconds), progressCts.Token); }
                    catch (OperationCanceledException) { break; }
                    FireProgress();
                }
            }, CancellationToken.None)
            : Task.CompletedTask;

        var tasks = negotiate.ChunksNeeded.Select(async chunkIndex =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var chunkBytes = ChunkMath.ChunkLength(chunkIndex, file.Length, chunkSizeBytes);
                void onBytesSent(long n) => Interlocked.Add(ref bytesInFlight, n);
                await UploadChunkAsync(file, negotiate.TransferId, chunkIndex, chunkSizeBytes, onBytesSent, ct);
                Interlocked.Add(ref bytesConfirmed, chunkBytes);
                Interlocked.Add(ref bytesInFlight, -chunkBytes);
                Interlocked.Increment(ref chunksConfirmed);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        await progressCts.CancelAsync();
        await progressTask;

        options.OnProgress?.Invoke(new UploadProgress
        {
            ChunksDone         = negotiate.TotalChunks,
            ChunksTotal        = negotiate.TotalChunks,
            BytesConfirmed     = file.Length,
            BytesInFlight      = 0,
            BytesTotal         = file.Length,
            TransferRateMBps   = (DateTime.UtcNow - started).TotalSeconds is > 0 and var s ? file.Length / 1_048_576.0 / s : 0,
            EstimatedRemaining = TimeSpan.Zero
        });
    }

    private async Task UploadChunkAsync(FileInfo file, Guid transferId, int chunkIndex, long chunkSizeBytes, Action<long> onBytesSent, CancellationToken ct)
    {
        var offset = ChunkMath.ChunkOffset(chunkIndex, chunkSizeBytes);
        var length = ChunkMath.ChunkLength(chunkIndex, file.Length, chunkSizeBytes);

        using var content = new ChunkContent(file.FullName, offset, length, onBytesSent);
        var response = await _http.PostAsync($"transfer/{transferId}/chunk/{chunkIndex}", content, ct);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
