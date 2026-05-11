using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FileRelay.Core;
using FileRelay.Core.Models;

namespace FileRelay.Client;

public class FileRelayClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ChunkTokenHandler? _chunkTokenHandler;
    private ChunkTokenHandler? _httpChunkTokenHandler;  // handler for the plain-HTTP data-path client
    private readonly string? _appId;
    private volatile string? _currentApiKey;

    // Internal throttle management — shared across concurrent UploadFileAsync calls.
    private int _activeUploads;
    private BandwidthLimiter? _sharedLimiter;
    private readonly object _limiterLock = new();

    /// <summary>Number of concurrent uploads currently in progress on this client.</summary>
    public int ActiveUploads => Interlocked.CompareExchange(ref _activeUploads, 0, 0);

    /// <summary>
    /// Maximum number of parallel chunk connections per upload.
    /// Takes effect the next time the first concurrent upload starts (when ActiveUploads was zero).
    /// </summary>
    public int ParallelConnections { get; set; } = 4;

    /// <summary>
    /// Bandwidth cap in MB/s across all concurrent uploads. 0 = unlimited.
    /// Takes effect the next time the first concurrent upload starts (when ActiveUploads was zero).
    /// </summary>
    public double ThrottleMBps { get; set; } = 0;

    public FileRelayClient(Uri baseUri, string? appId = null, string? apiKey = null,
        bool allowUntrustedCertificate = false)
    {
        _appId         = appId;
        _currentApiKey = apiKey;

        var inner = new HttpClientHandler();
        if (allowUntrustedCertificate)
        {
            try { inner.ServerCertificateCustomValidationCallback = (_, _, _, _) => true; }
            catch (PlatformNotSupportedException) { }
        }

        _chunkTokenHandler = appId is { Length: > 0 } && apiKey is { Length: > 0 }
            ? new ChunkTokenHandler(appId, apiKey, inner)
            : null;

        _http = new HttpClient(_chunkTokenHandler ?? (HttpMessageHandler)inner)
        {
            BaseAddress = baseUri,
        };
        if (appId is { Length: > 0 })
            _http.DefaultRequestHeaders.Add("X-App-Id", appId);
        if (apiKey is { Length: > 0 })
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

        string? fileHash = null;
        if (options.ComputeFileHash)
        {
            using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
            var hashBytes = await ComputeSha256Async(fs, ct).ConfigureAwait(false);
            fileHash = $"sha256:{Convert.ToBase64String(hashBytes)}";
        }

        var request = new TransferNegotiateRequest
        {
            Filename      = file.Name,
            FileSizeBytes = file.Length,
            FileHash      = fileHash,
            ChunkSizeMB   = options.PreferredChunkSizeMB,
            Context       = options.Context
        };

        var limiter = AcquireLimiter();
        int attempt = 0;
        HttpClient? ownedChunkHttp = null;
        try
        {
            while (true)
            {
                try
                {
                    var negotiate = await NegotiateAsync(request, options, ct).ConfigureAwait(false);
                    options.OnNegotiated?.Invoke(negotiate);
                    if (negotiate.ChunksNeeded.Count == 0) return;

                    // On first negotiate response that advertises an HTTP chunk port, create a
                    // separate plain-HTTP client for the data path. Reused on retries.
                    if (ownedChunkHttp == null && options.UseHttpDataPath
                        && negotiate.HttpChunkPort is { } port)
                    {
                        ownedChunkHttp = CreateHttpChunkClient(port);
                    }

                    var chunkSizeBytes = (long)negotiate.ChunkSizeMB * 1024 * 1024;
                    await UploadChunksAsync(file, negotiate, chunkSizeBytes, ownedChunkHttp ?? _http, limiter, options, ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && attempt < options.MaxRetries
                    && ex is HttpRequestException or OperationCanceledException)
                {
                    attempt++;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    options.OnRetry?.Invoke(ex, attempt, delay);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ownedChunkHttp?.Dispose();
            ReleaseLimiter();
        }
    }

    /// <summary>
    /// Updates the API key used for all subsequent requests on this client instance,
    /// including any chunk uploads in progress. Call this after a key rotation so that
    /// active transfers adopt the new key immediately.
    /// </summary>
    public void UpdateApiKey(string newKey)
    {
        _currentApiKey = newKey;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newKey);
        _chunkTokenHandler?.UpdateApiKey(newKey);
        _httpChunkTokenHandler?.UpdateApiKey(newKey);
    }

    public async Task<string> RotateKeyAsync(CancellationToken ct = default)
    {
        var entropy = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(entropy);

        var request = new RotateKeyRequest { ClientEntropy = Convert.ToBase64String(entropy) };
        var response = await _http.PostAsJsonAsync("transfer/rotate-key", request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RotateKeyResponse>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty rotate-key response.");
        UpdateApiKey(result.NewKey);
        return result.NewKey;
    }

    private BandwidthLimiter? AcquireLimiter()
    {
        lock (_limiterLock)
        {
            if (Interlocked.CompareExchange(ref _activeUploads, 0, 0) == 0)
            {
                _sharedLimiter?.Dispose();
                _sharedLimiter = ThrottleMBps > 0
                    ? new BandwidthLimiter(ThrottleMBps, ParallelConnections)
                    : null;
            }
        }
        Interlocked.Increment(ref _activeUploads);
        return _sharedLimiter;
    }

    private void ReleaseLimiter() => Interlocked.Decrement(ref _activeUploads);

    private HttpClient CreateHttpChunkClient(int httpPort)
    {
        var inner = new HttpClientHandler();
        var apiKey = _currentApiKey;
        var handler = _appId is { Length: > 0 } && apiKey is { Length: > 0 }
            ? new ChunkTokenHandler(_appId, apiKey, inner)
            : null;
        _httpChunkTokenHandler = handler;

        var host = _http.BaseAddress!.Host;
        var basePath = _http.BaseAddress.AbsolutePath.TrimEnd('/') + "/";
        var client = new HttpClient(handler ?? (HttpMessageHandler)inner)
        {
            BaseAddress = new Uri($"http://{host}:{httpPort}{basePath}"),
        };
        if (_appId is { Length: > 0 })
            client.DefaultRequestHeaders.Add("X-App-Id", _appId);
        // No Authorization header — chunks authenticate via per-chunk HMAC token.
        return client;
    }

    private async Task<TransferNegotiateResponse> NegotiateAsync(
        TransferNegotiateRequest request, UploadOptions options, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("transfer/negotiate", request, ct).ConfigureAwait(false);
        ThrowForKnownStatusCodes(response);
        response.EnsureSuccessStatusCode();
        CheckKeyWarning(response, options.OnKeyWarning);
        return await response.Content.ReadFromJsonAsync<TransferNegotiateResponse>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty negotiate response.");
    }

    private async Task UploadChunksAsync(FileInfo file, TransferNegotiateResponse negotiate,
        long chunkSizeBytes, HttpClient httpChunks, BandwidthLimiter? limiter, UploadOptions options, CancellationToken ct)
    {
        var sessionBytesTotal = negotiate.ChunksNeeded
            .Sum(ci => ChunkMath.ChunkLength(ci, file.Length, chunkSizeBytes));
        var alreadyConfirmedBytes = file.Length - sessionBytesTotal;

        var started = DateTime.UtcNow;
        long bytesConfirmed = 0;
        long bytesInFlight = 0;
        int chunksConfirmed = 0;
        var parallelConnections = limiter?.ParallelConnections ?? ParallelConnections;
        var semaphore = new SemaphoreSlim(parallelConnections);

        void FireProgress()
        {
            var confirmed   = Interlocked.Read(ref bytesConfirmed);
            var inFlight    = Interlocked.Read(ref bytesInFlight);
            var sessionSent = confirmed + inFlight;
            var elapsed     = (DateTime.UtcNow - started).TotalSeconds;
            var rate        = elapsed > 0.5 ? sessionSent / 1_048_576.0 / elapsed : 0;
            var remaining   = sessionBytesTotal - sessionSent;
            var eta         = rate > 0 ? TimeSpan.FromSeconds(remaining / 1_048_576.0 / rate) : (TimeSpan?)null;
            options.OnProgress!(new UploadProgress
            {
                ChunksDone         = Volatile.Read(ref chunksConfirmed),
                ChunksTotal        = negotiate.TotalChunks,
                BytesConfirmed     = alreadyConfirmedBytes + confirmed,
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
                    try { await Task.Delay(TimeSpan.FromSeconds(options.ProgressIntervalSeconds), progressCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    FireProgress();
                }
            }, CancellationToken.None)
            : Task.CompletedTask;

        using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var chunkCt = abortCts.Token;

        var tasks = negotiate.ChunksNeeded.Select(async chunkIndex =>
        {
            try
            {
                await semaphore.WaitAsync(chunkCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return; // aborted before acquiring semaphore — nothing to release
            }
            try
            {
                var chunkBytes = ChunkMath.ChunkLength(chunkIndex, file.Length, chunkSizeBytes);
                var runIndex   = negotiate.ChunkRunIndexes != null
                    && negotiate.ChunkRunIndexes.TryGetValue(chunkIndex, out var ri) ? ri : 1;
                var apiKey = _currentApiKey;   // snapshot: pending tasks pick up a rotated key naturally
                long bytesSent = 0;
                void onBytesSent(long n) { Interlocked.Add(ref bytesInFlight, n); bytesSent += n; }

                UnauthorizedAccessException? authEx = null;
                try
                {
                    await UploadChunkAsync(file, negotiate.TransferId, chunkIndex, runIndex, chunkSizeBytes,
                        httpChunks, apiKey, onBytesSent, limiter, options.Priority, chunkCt).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException ex) { authEx = ex; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Interlocked.Add(ref bytesInFlight, -bytesSent);
                    return;
                }

                if (authEx != null)
                {
                    Interlocked.Add(ref bytesInFlight, -bytesSent);
                    bytesSent = 0;
                    var updatedKey = _currentApiKey;
                    if (updatedKey != null && updatedKey != apiKey)
                    {
                        // Key was rotated externally while this chunk was in-flight — retry once.
                        apiKey = updatedKey;
                        try
                        {
                            await UploadChunkAsync(file, negotiate.TransferId, chunkIndex, runIndex, chunkSizeBytes,
                                httpChunks, apiKey, onBytesSent, limiter, options.Priority, chunkCt).ConfigureAwait(false);
                            authEx = null;
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            Interlocked.Add(ref bytesInFlight, -bytesSent);
                            authEx = ex;
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            Interlocked.Add(ref bytesInFlight, -bytesSent);
                            return;
                        }
                    }
                    if (authEx != null) { abortCts.Cancel(); throw authEx; }
                }
                Interlocked.Add(ref bytesConfirmed, chunkBytes);
                Interlocked.Add(ref bytesInFlight, -chunkBytes);
                Interlocked.Increment(ref chunksConfirmed);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        progressCts.Cancel();
        await progressTask.ConfigureAwait(false);

        var sessionElapsed = (DateTime.UtcNow - started).TotalSeconds;
        options.OnProgress?.Invoke(new UploadProgress
        {
            ChunksDone         = negotiate.TotalChunks,
            ChunksTotal        = negotiate.TotalChunks,
            BytesConfirmed     = file.Length,
            BytesInFlight      = 0,
            BytesTotal         = file.Length,
            TransferRateMBps   = sessionElapsed > 0 ? sessionBytesTotal / 1_048_576.0 / sessionElapsed : 0,
            EstimatedRemaining = TimeSpan.Zero
        });
    }

    private async Task UploadChunkAsync(FileInfo file, Guid transferId, int chunkIndex,
        int runIndex, long chunkSizeBytes, HttpClient httpChunks,
        string? apiKey, Action<long> onBytesSent, BandwidthLimiter? limiter, byte priority, CancellationToken ct)
    {
        var offset = ChunkMath.ChunkOffset(chunkIndex, chunkSizeBytes);
        var length = ChunkMath.ChunkLength(chunkIndex, file.Length, chunkSizeBytes);

        using var content = new ChunkContent(file.FullName, offset, length, onBytesSent,
            limiter, priority, _appId, apiKey, transferId, chunkIndex, runIndex);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"transfer/{transferId}/chunk/{chunkIndex}")
        {
            Content = content
        };
        if (runIndex != 1)
            request.Headers.Add("X-Run-Index", runIndex.ToString());
        var response = await httpChunks.SendAsync(request, ct).ConfigureAwait(false);
        ThrowForKnownStatusCodes(response);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<Core.Models.ServerInfoResponse> PingAsync(
        Uri baseUri, string? appId, string? apiKey, CancellationToken ct,
        bool allowUntrustedCertificate = false, Action<string>? onKeyWarning = null)
    {
        var handler = new HttpClientHandler();
        if (allowUntrustedCertificate)
        {
            try { handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true; }
            catch (PlatformNotSupportedException) { }
        }
        using var http = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout     = TimeSpan.FromSeconds(5),
        };
        if (appId is { Length: > 0 })
            http.DefaultRequestHeaders.Add("X-App-Id", appId);
        if (apiKey is { Length: > 0 })
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.GetAsync("transfer/ping", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        CheckKeyWarning(response, onKeyWarning);
        return await response.Content.ReadFromJsonAsync<Core.Models.ServerInfoResponse>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty ping response.");
    }

    private static void ThrowForKnownStatusCodes(HttpResponseMessage response)
    {
        switch (response.StatusCode)
        {
            case System.Net.HttpStatusCode.Unauthorized:
                throw new UnauthorizedAccessException("The server rejected the API key.");
            case (System.Net.HttpStatusCode)421: // MisdirectedRequest
                throw new InvalidOperationException("The server requires HTTPS. Use an https:// URL.");
            case System.Net.HttpStatusCode.NotFound:
                throw new InvalidOperationException("The server no longer has a record of this transfer. The partial file may have been deleted. Restart the transfer manually if intended.");
        }
    }

    private static void CheckKeyWarning(HttpResponseMessage response, Action<string>? handler)
    {
        if (handler == null) return;
        if (response.Headers.TryGetValues("X-Key-Status", out var values))
        {
            var status = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(status))
                handler(status);
        }
    }

    private static async Task<byte[]> ComputeSha256Async(Stream stream, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return sha.Hash!;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _sharedLimiter?.Dispose();
    }

    // Intercepts chunk requests to strip the Bearer token and inject a scoped HMAC token.
    // DefaultRequestHeaders (including Authorization) are merged into request.Headers before
    // DelegatingHandler.SendAsync is called, so Remove("Authorization") works correctly here.
    private sealed class ChunkTokenHandler : DelegatingHandler
    {
        private readonly string _appId;
        private volatile string _apiKey;

        public ChunkTokenHandler(string appId, string apiKey, HttpMessageHandler inner)
            : base(inner)
        {
            _appId  = appId;
            _apiKey = apiKey;
        }

        public void UpdateApiKey(string key) => _apiKey = key;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (TryParseChunkRequest(request.RequestUri, out var transferId, out var chunkIndex))
            {
                var runIndex = request.Headers.TryGetValues("X-Run-Index", out var vals)
                    && int.TryParse(vals.FirstOrDefault(), out var ri) ? ri : 1;
                request.Headers.Remove("Authorization");
                request.Headers.Add("X-Chunk-Token", ChunkToken.Compute(_apiKey, _appId, transferId, chunkIndex, runIndex));
            }
            return base.SendAsync(request, ct);
        }

        private static bool TryParseChunkRequest(Uri? uri, out Guid transferId, out int chunkIndex)
        {
            transferId = default;
            chunkIndex = default;
            var path = uri?.AbsolutePath ?? "";
            var marker = path.LastIndexOf("/chunk/", StringComparison.Ordinal);
            if (marker < 0) return false;
            if (!int.TryParse(path.Substring(marker + 7), out chunkIndex)) return false;
            var before = path.Substring(0, marker);
            var slash  = before.LastIndexOf('/');
            return slash >= 0 && Guid.TryParse(before.Substring(slash + 1), out transferId);
        }
    }
}
