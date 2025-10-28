using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    //public sealed class DownloadCoordinator
    //{
    //    private readonly string _baseUrl; // e.g., https://acct.blob.core.windows.net/container/folder
    //    private readonly string _sas;     // starts with ?sv=...
    //    private readonly string _fileName; // target logical name (also used to find the manifest)
    //    private readonly string _destPath; // local full destination path
    //    private readonly Action<string> _log;
    //    private readonly double _maxMbPerSec;

    //    private readonly HttpClient _http;

    //    public DownloadCoordinator(
    //        string baseUrl,
    //        string sas,
    //        string fileName,
    //        string destinationFullPath,
    //        Action<string> log,
    //        double maxMbPerSec = 0.0)
    //    {
    //        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentNullException(nameof(baseUrl));
    //        if (string.IsNullOrWhiteSpace(sas)) throw new ArgumentNullException(nameof(sas));
    //        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentNullException(nameof(fileName));
    //        if (string.IsNullOrWhiteSpace(destinationFullPath)) throw new ArgumentNullException(nameof(destinationFullPath));

    //        // Normalize base URL to have no trailing slash; SAS must start with '?'
    //        _baseUrl = baseUrl.TrimEnd('/');
    //        _sas = sas.StartsWith("?") ? sas : "?" + sas;
    //        _fileName = fileName;
    //        _destPath = destinationFullPath;
    //        _log = log ?? (_ => { });
    //        _maxMbPerSec = maxMbPerSec;

    //        // Single HttpClient instance; no decompression; long timeout
    //        var handler = new HttpClientHandler
    //        {
    //            AllowAutoRedirect = false,
    //            AutomaticDecompression = System.Net.DecompressionMethods.None
    //        };
    //        _http = new HttpClient(handler, disposeHandler: true);
    //        _http.Timeout = TimeSpan.FromMinutes(30);
    //    }

    //    private string CombineBlobUrl(string blobName)
    //    {
    //        // blobName must be URL-encoded by the creator; we assume manifest stores it exactly as uploaded
    //        return _baseUrl + "/" + blobName + _sas;
    //    }

    //    private string ManifestUrl()
    //    {
    //        // Deterministic manifest name: <fileName>.manifest.json
    //        // DO NOT log the SAS; when logging, only show the blob name—not the URL.
    //        var manifestBlobName = _fileName + ".manifest.json";
    //        return CombineBlobUrl(manifestBlobName);
    //    }

    //    public async Task DownloadAsync(CancellationToken token = default(CancellationToken))
    //    {
    //        // 1) Load manifest
    //        var store = new ManifestStoreHttp(ManifestUrl(), _log);
    //        var manifest = await store.LoadAsync(token).ConfigureAwait(false);
    //        ValidateManifestForDownload(manifest);

    //        _log("Manifest loaded: " + manifest.FileName + $" ({manifest.FileSizeBytes} bytes, {manifest.Chunks.Count} chunks)");

    //        // 2) Prep local temp file and per-chunk markers
    //        var tempPath = _destPath + ".downloading";
    //        var markerDir = _destPath + ".chunks";
    //        Directory.CreateDirectory(Path.GetDirectoryName(_destPath) ?? ".");
    //        Directory.CreateDirectory(markerDir);

    //        using (var fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
    //                                       bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
    //        {
    //            if (fs.Length != manifest.FileSizeBytes)
    //            {
    //                fs.SetLength(manifest.FileSizeBytes);
    //                await fs.FlushAsync(token).ConfigureAwait(false);
    //            }

    //            // 3) Sequentially download/verify each chunk; write at correct offset
    //            for (int i = 0; i < manifest.Chunks.Count; i++)
    //            {
    //                token.ThrowIfCancellationRequested();

    //                var c = manifest.Chunks[i];
    //                var marker = Path.Combine(markerDir, "chunk_" + c.Index.ToString("D6") + ".ok");

    //                // Skip if already verified
    //                if (File.Exists(marker))
    //                {
    //                    _log($"Chunk {c.Index} already verified; skipping.");
    //                    continue;
    //                }

    //                var chunkUrl = CombineBlobUrl(c.BlobName);

    //                _log($"Downloading chunk {c.Index} (len={c.Length}) from blob '{c.BlobName}'...");

    //                using (var req = new HttpRequestMessage(HttpMethod.Get, chunkUrl))
    //                using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
    //                {
    //                    resp.EnsureSuccessStatusCode();

    //                    using (var remote = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
    //                    using (var hashing = new SHA256Managed())
    //                    using (var throttler = new ThrottledStreamReader(remote, _maxMbPerSec))
    //                    {
    //                        // Position to chunk offset and stream-copy with hash
    //                        long offset = (long)c.Index * (long)manifest.ChunkSizeBytes;
    //                        fs.Position = offset;

    //                        const int BUFSZ = 8 * 1024 * 1024; // 8MB streaming buffer
    //                        var buffer = new byte[BUFSZ];
    //                        long remaining = c.Length;

    //                        while (remaining > 0)
    //                        {
    //                            token.ThrowIfCancellationRequested();

    //                            int want = remaining > BUFSZ ? BUFSZ : (int)remaining;
    //                            int read = await throttler.ReadAsync(buffer, 0, want, token).ConfigureAwait(false);
    //                            if (read <= 0) throw new IOException($"Unexpected EOF while reading chunk {c.Index} from remote.");

    //                            hashing.TransformBlock(buffer, 0, read, null, 0);
    //                            await fs.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);

    //                            remaining -= read;
    //                        }

    //                        hashing.TransformFinalBlock(new byte[0], 0, 0);
    //                        var hex = ToHex(hashing.Hash);

    //                        if (!StringEqualsConstTime(hex, c.HashHex))
    //                        {
    //                            // Hash mismatch: invalidate any partial write by zeroing the chunk region (optional)
    //                            _log($"HASH MISMATCH for chunk {c.Index}. Expected={c.HashHex}, Got={hex}");
    //                            // Optionally zero written region:
    //                            // await ZeroRegionAsync(fs, offset, c.Length, token).ConfigureAwait(false);
    //                            throw new IOException($"Hash mismatch on chunk {c.Index}.");
    //                        }
    //                    }
    //                }

    //                // 4) Mark verified
    //                File.WriteAllText(marker, "ok");
    //                _log($"Chunk {c.Index} verified.");
    //            }

    //            await fs.FlushAsync(token).ConfigureAwait(false);
    //        }

    //        // 5) All chunks verified: finalize
    //        // Clean up markers, move into place atomically
    //        try
    //        {
    //            if (File.Exists(_destPath)) File.Delete(_destPath);
    //            File.Move(_destPath + ".downloading", _destPath);
    //        }
    //        finally
    //        {
    //            // Best-effort cleanup of markers
    //            try
    //            {
    //                Directory.Delete(_destPath + ".chunks", recursive: true);
    //            }
    //            catch { /* ignore */ }
    //        }

    //        _log("Download complete: " + _destPath);
    //    }

    //    private static string ToHex(byte[] bytes)
    //    {
    //        if (bytes == null || bytes.Length == 0) return string.Empty;
    //        var sb = new StringBuilder(bytes.Length * 2);
    //        for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("X2"));
    //        return sb.ToString();
    //    }

    //    // Constant-time-ish compare for hex strings (same length expected)
    //    private static bool StringEqualsConstTime(string a, string b)
    //    {
    //        if (a == null || b == null) return false;
    //        if (a.Length != b.Length) return false;
    //        int diff = 0;
    //        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
    //        return diff == 0;
    //    }

    //    private static void ValidateManifestForDownload(BackupManifest m)
    //    {
    //        if (m == null) throw new InvalidOperationException("Manifest is null.");
    //        if (m.FileSizeBytes <= 0) throw new InvalidOperationException("Manifest has invalid FileSizeBytes.");
    //        if (m.ChunkSizeBytes <= 0) throw new InvalidOperationException("Manifest has invalid ChunkSizeBytes.");
    //        if (m.Chunks == null || m.Chunks.Count == 0) throw new InvalidOperationException("Manifest has no chunks.");
    //        for (int i = 0; i < m.Chunks.Count; i++)
    //        {
    //            var c = m.Chunks[i];
    //            if (c.Index != i) throw new InvalidOperationException("Manifest chunks are not sequentially indexed starting at 0.");
    //            if (c.Length <= 0) throw new InvalidOperationException($"Chunk {i} has invalid Length.");
    //            if (string.IsNullOrWhiteSpace(c.BlobName)) throw new InvalidOperationException($"Chunk {i} missing BlobName.");
    //            if (string.IsNullOrWhiteSpace(c.HashHex)) throw new InvalidOperationException($"Chunk {i} missing HashHex.");
    //        }
    //    }

    //    // Optional helper if you ever want to explicitly zero a failed region
    //    private static async Task ZeroRegionAsync(FileStream fs, long offset, long length, CancellationToken token)
    //    {
    //        fs.Position = offset;
    //        const int Z = 1024 * 1024;
    //        var zero = new byte[Z];
    //        while (length > 0)
    //        {
    //            int n = length > Z ? Z : (int)length;
    //            await fs.WriteAsync(zero, 0, n, token).ConfigureAwait(false);
    //            length -= n;
    //        }
    //    }
    //}

    ///// <summary>
    ///// Simple throttled reader that enforces an approximate bytes-per-second ceiling.
    ///// Matches uploader style (MB/s, C# 7.3).
    ///// </summary>
    //internal sealed class ThrottledStreamReader : IDisposable
    //{
    //    private readonly Stream _inner;
    //    private readonly bool _throttle;
    //    private readonly double _bytesPerSecond;
    //    private long _bytesThisWindow;
    //    private System.Diagnostics.Stopwatch _window;

    //    public ThrottledStreamReader(Stream inner, double maxMbPerSec)
    //    {
    //        _inner = inner ?? throw new ArgumentNullException("inner");
    //        _throttle = maxMbPerSec > 0;
    //        _bytesPerSecond = maxMbPerSec * 1024d * 1024d;
    //        if (_throttle)
    //        {
    //            _window = System.Diagnostics.Stopwatch.StartNew();
    //            _bytesThisWindow = 0;
    //        }
    //    }

    //    public async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    //    {
    //        int read = await _inner.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
    //        if (!_throttle || read <= 0) return read;

    //        _bytesThisWindow += read;
    //        long elapsedMs = _window.ElapsedMilliseconds;
    //        if (elapsedMs <= 0) elapsedMs = 1;

    //        double allowedSoFar = _bytesPerSecond * (elapsedMs / 1000.0);
    //        if (_bytesThisWindow > allowedSoFar)
    //        {
    //            double overBytes = _bytesThisWindow - allowedSoFar;
    //            int msToWait = (int)Math.Ceiling((overBytes / _bytesPerSecond) * 1000.0);
    //            if (msToWait > 0)
    //            {
    //                // Use a synchronous sleep here; it keeps the rate simple and avoids extra async overhead.
    //                Thread.Sleep(msToWait);
    //            }
    //        }

    //        if (_window.ElapsedMilliseconds >= 1000)
    //        {
    //            _window.Restart();
    //            _bytesThisWindow = 0;
    //        }

    //        return read;
    //    }

    //    public void Dispose()
    //    {
    //        // Do not dispose the inner stream (owned by HttpClient)
    //    }
    //}
}
