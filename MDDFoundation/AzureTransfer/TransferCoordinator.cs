using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MDDFoundation
{
    public class TransferCoordinator
    {
        private static HttpClient CreateHttp()
        {
            var h = new HttpClientHandler
            {
                // SocketsHttpHandler is not available in older .NET Frameworks; HttpClientHandler is the compatible alternative
                // MaxConnectionsPerServer is not available on HttpClientHandler, but MaxConnectionsPerServer can be set via ServicePointManager
                AutomaticDecompression = System.Net.DecompressionMethods.None
            };
            return new HttpClient(h, disposeHandler: true);
        }
        //private static HttpClient CreateHttp()
        //{
        //    // Requires Microsoft.Net.Http.WinHttpHandler (works on .NET Core 2.0)
        //    var h = new WinHttpHandler
        //    {
        //        // Let us open many parallel range requests to the same host
        //        MaxConnectionsPerServer = 64,           // tune 16–128
        //        AutomaticDecompression = DecompressionMethods.None,

        //        // Prefer HTTP/2 (Azure Blob supports it; WinHTTP negotiates ALPN)
        //        //HttpVersion = new Version(2, 0),
        //        AutomaticRedirection = false,
        //        EnableMultipleHttp2Connections = true,  // allow more than one h2 connection if needed

        //        // Don’t inherit system proxy accidentally
        //        WindowsProxyUsePolicy = WindowsProxyUsePolicy.DoNotUseProxy
        //    };

        //    var client = new HttpClient(h, disposeHandler: true);

        //    // Encourage h2 on each request (no error if negotiated down to h1.1)
        //    //client.DefaultRequestVersion = new Version(2, 0);
        //    #if NET5_0_OR_GREATER
        //        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        //    #endif
        //    return client;
        //}

        private readonly HttpClient _http = CreateHttp();
        private readonly string _baseUrl;   // Container URL, e.g. https://acct.blob.core.windows.net/backups !!no folder
        private readonly string _sas;       // Just the ?sp=... part
        private readonly Action<string> _log;
        private readonly int _maxdop;
        private readonly SemaphoreSlim _slots; // tune: 4–8 typical
        private readonly double _maxMBs;
        private readonly ConcurrentPriorityQueue<TransferWork> _queue = new ConcurrentPriorityQueue<TransferWork>();
        private readonly int _chunkSizeMb;

        public TransferCoordinator(string baseUrl, string sasToken, Action<string> log = null, int maxdop = 6, double maxMBs = 0, int chunkSizeMB = 1024)
        {
            System.Net.ServicePointManager.DefaultConnectionLimit = Math.Max(256, System.Net.ServicePointManager.DefaultConnectionLimit);
            System.Net.ServicePointManager.Expect100Continue = false;
            _baseUrl = baseUrl.TrimEnd('/');
            _sas = sasToken.TrimStart('?');
            _log = log ?? (_ => { });
            _maxdop = maxdop;
            _slots = new SemaphoreSlim(maxdop);
            _maxMBs = maxMBs / _maxdop;
            _chunkSizeMb = chunkSizeMB;
        }
        public async Task<List<TransferManifest>> FindManifestsAsync(string _azureFolderPath)
        {
            var list = new List<TransferManifest>();
            var manifestfiles = new List<string>();
            using (var http = new HttpClient())
            {
                string url = _baseUrl + "?restype=container&comp=list" + (string.IsNullOrWhiteSpace(_azureFolderPath) ? "" : $"&prefix={_azureFolderPath}/&") + _sas;

                var resp = await http.GetAsync(url);
                resp.EnsureSuccessStatusCode();

                string xml = await resp.Content.ReadAsStringAsync();
                var doc = XDocument.Parse(xml);

                foreach (var blob in doc.Descendants("Blob"))
                {
                    var name = (string)blob.Element("Name");

                    if (!string.IsNullOrWhiteSpace(_azureFolderPath))
                        name = name.Substring(_azureFolderPath.Length + 1);

                    if (TransferManifest.LooksLikeChunkName(name))
                        continue;

                    if (TransferManifest.LooksLikeManifestName(name))
                    {
                        manifestfiles.Add(name); // your existing path that loads the real manifest
                        continue;
                    }

                    // synthesize a simple single-chunk manifest for plain files
                    long size = 0;
                    var props = blob.Element("Properties");
                    if (props != null)
                    {
                        long.TryParse((string)props.Element("Content-Length"), out size);
                    }

                    var simple = TransferManifest.CreateSimpleManifest(
                        fileName: name,
                        relativepath: _azureFolderPath,
                        size: size,
                        chunkSizeBytes: (int)size);

                    list.Add(simple);
                }
            }

            foreach (var file in manifestfiles)
            {
                var manifeststore = new ManifestStoreHttp($"{_baseUrl}/{(string.IsNullOrWhiteSpace(_azureFolderPath) ? "" : $"{_azureFolderPath}/")}{file}?{_sas}");
                var manifest = await manifeststore.TryLoadAsync();
                if (manifest != null)
                    list.Add(manifest);
            }

            return list;
        }
        public async Task<TransferOutcome> DownloadAsync(TransferManifest manifest, string localpath, TimeSpan pollinterval = default, double maxMbPerSec = 0, CancellationToken token = default, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default)
        {
            var sw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(localpath))
                throw new ArgumentNullException("localpath");
            if (manifest == null)
                throw new ArgumentNullException("manifest");

            FileInfo finalPath;
            string tempPath;
            PreparePartialFile(manifest, localpath, out finalPath, out tempPath);

            if (manifest == null)
                throw new IOException("Remote manifest not found or unreadable.");
            var localdownloadstatefile = finalPath.FullName + ".dl.json";
            var state = LoadLocalDownloadState(localdownloadstatefile, manifest.FileSize, manifest.ChunkSizeBytes);

            ManifestStoreHttp manifestStore = null;
            if (!manifest.IsSimple) manifestStore = new ManifestStoreHttp(manifest.ManifestURL(_baseUrl, _sas));

            // --- NEW: parallel fan-out
            const int maxParallel = 6; // tune: 4–8 is a good starting point
            var sem = new SemaphoreSlim(maxParallel);

            long bytesdownloaded = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                // Pick work that is available & not yet done locally
                var toFetch = new List<ManifestChunk>();
                foreach (var c in manifest.Chunks)
                    if (c.Completed && !state.Completed.Contains(c.Index))
                        toFetch.Add(c);

                if (toFetch.Count == 0)
                {
                    // nothing new at the moment
                    if (state.Completed.Count == manifest.Chunks.Count && manifest.CompletedUtc.HasValue)
                        break; // done

                    if (manifestStore != null && pollinterval > TimeSpan.Zero)
                    {
                        await Task.Delay(pollinterval, token).ConfigureAwait(false);
                        manifest = await manifestStore.TryLoadAsync().ConfigureAwait(false)
                                   ?? throw new IOException("Remote manifest disappeared during download.");
                        continue;
                    }
                    else
                    {
                        _log("Download pending: partial file saved; waiting for more chunks.");
                        return TransferOutcome.Pending;
                    }
                }

                var tasks = new List<Task>(toFetch.Count);
                foreach (var chunk in toFetch)
                {
                    await sem.WaitAsync(token).ConfigureAwait(false);
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            FileCopyProgress progress = null;
                            if (progresscallback != null)
                            {
                                progress = new FileCopyProgress
                                {
                                    FileName = manifest.FileName + $" (Chunk {chunk.Index + 1}/{manifest.Chunks.Count})",
                                    FileSizeBytes = chunk.SizeBytes,
                                    Stopwatch = Stopwatch.StartNew(),
                                    OperationDuring = "Downloading",
                                    OperationComplete = "Download",
                                    Callback = progresscallback,
                                    ProgressReportInterval = (progressreportinterval == default ? TimeSpan.FromSeconds(1) : progressreportinterval)
                                };
                            }

                            // IMPORTANT: open a separate handle per task; allow others to write too.
                            //using (var fsChunk = new FileStream(
                            //    tempPath, FileMode.Open, FileAccess.Write,
                            //    FileShare.ReadWrite, 1024 * 1024,
                            //    FileOptions.SequentialScan | FileOptions.Asynchronous))
                            //{
                                // Position inside DownloadOneChunkAsync (it computes offset from chunk.Index)
                                await DownloadChunkAsync(manifest, chunk, tempPath, maxMbPerSec, token, progress).ConfigureAwait(false);
                                Interlocked.Add(ref bytesdownloaded, chunk.SizeBytes);
                            //}

                            lock (state) state.Completed.Add(chunk.Index);
                            await SaveLocalDownloadState(localdownloadstatefile, state);
                            _log($"Downloaded chunk {chunk.Index + 1}/{manifest.Chunks.Count}");
                        }
                        finally
                        {
                            sem.Release();
                        }
                    }, token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // small poll so uploader can publish more completed chunks
                if (pollinterval > TimeSpan.Zero)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                    if (manifestStore != null)
                    {
                        manifest = await manifestStore.TryLoadAsync().ConfigureAwait(false)
                                   ?? throw new IOException("Remote manifest disappeared during download.");
                    }
                }
            }

            // finalize
            if (finalPath.Exists)
            {
                finalPath.Delete();
                await Foundation.RetryAsync(() => !finalPath.Exists, 10, 100);
            }

            File.Move(tempPath, finalPath.FullName);
            try { File.Delete(localdownloadstatefile); } catch { }
            sw.Stop();
            var rate = bytesdownloaded / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;

            long finalsize = 0;
            await Foundation.RetryAsync(() =>
            {
                if (finalPath.Exists) finalsize = finalPath.Length;
                return finalsize != 0;
            }, 10, 100);

            _log($"Download complete: {finalPath.Name} ({Foundation.SizeDisplay(finalsize)}) Overall downloaded this run: {Foundation.SizeDisplay(bytesdownloaded)}, {sw.Elapsed.TotalSeconds:N1} sec, {rate:N2}MB/s");
            return TransferOutcome.Complete;
        }

        private static void PreparePartialFile(TransferManifest manifest, string localpath, out FileInfo finalPath, out string tempPath)
        {
            if (localpath.EndsWith("*"))
                finalPath = new FileInfo(Path.Combine(localpath.TrimEnd('*'), manifest.FileName));
            else
                finalPath = new FileInfo(Path.Combine(localpath, manifest.RelativePath.Replace('/','\\'), manifest.FileName));

            tempPath = finalPath.FullName + ".partial";
            Directory.CreateDirectory(finalPath.DirectoryName);

            if (!File.Exists(tempPath))
            {
                // Pre-size once (single handle), then close so parallel writers can open:
                using (var presize = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite, 1024 * 1024, FileOptions.SequentialScan))
                {
                    if (presize.Length != manifest.FileSize)
                    {
                        presize.SetLength(manifest.FileSize);
                        presize.Flush(true);
                    }
                }
            }
        }

        public async Task UploadAsync(FileInfo file, string relativepath, int chunkSizeMb = 1024, double maxMbPerSec = 0, CancellationToken token = default, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default)
        {
            if (!file.Exists) throw new FileNotFoundException("File not found", file.FullName);


            var tmanifest = new TransferManifest
            {
                FileName = file.Name,
                FileSize = file.Length,
                RelativePath = relativepath,
                ChunkSizeBytes = chunkSizeMb * 1024 * 1024
            };

            var manifestStore = new ManifestStoreHttp(tmanifest.ManifestURL(_baseUrl, _sas));

            var manifest = await manifestStore.TryLoadAsync().ConfigureAwait(false);
            if (manifest == null)
            {
                if (tmanifest.NumChunks <= 1)
                {
                    manifest = TransferManifest.CreateSimpleManifest(file.Name, relativepath, file.Length, (int)file.Length);
                    manifestStore = null;
                }
                else
                {
                    manifest = tmanifest;
                    await manifestStore.SaveAsync(manifest).ConfigureAwait(false);
                    _log($"Created new manifest: {file.Name}.manifest");
                }

            }

            var chunkSize = manifest.ChunkSizeBytes;
            var totalChunks = manifest.NumChunks; // (int)Math.Ceiling((double)file.Length / chunkSize);

            using (var stream = file.OpenRead())
            {
                for (int index = 0; index < totalChunks; index++)
                {
                    token.ThrowIfCancellationRequested();

                    var existing = manifest.Chunks.Find(c => c.Index == index);
                    if (existing != null && existing.Completed)
                    {
                        _log($"Chunk {index + 1}/{totalChunks} already uploaded; skipping.");
                        continue;
                    }

                    var offset = (long)index * chunkSize;
                    var length = Math.Min(chunkSize, file.Length - offset);

                    string chunkUrl;
                    if (string.IsNullOrWhiteSpace(manifest.RelativePath))
                        chunkUrl = $"{_baseUrl}";
                    else
                        chunkUrl = $"{_baseUrl}/{manifest.RelativePath}";

                    string chunkName;
                    if (manifest.IsSimple)
                        chunkName = file.Name;
                    else
                        chunkName = $"{file.Name}.chunk{index:D5}";

                    chunkUrl = $"{chunkUrl}/{chunkName}?{_sas}";

                    FileCopyProgress progress = null;
                    if (progresscallback != null)
                    {
                        progress = new FileCopyProgress
                        {
                            FileName = file.Name + (totalChunks > 1 ? $" (Chunk {index+1}/{totalChunks})" : ""),
                            FileSizeBytes = length,
                            Stopwatch = Stopwatch.StartNew(),
                            OperationDuring = "Uploading",
                            OperationComplete = "Upload",
                            Callback = progresscallback,
                            ProgressReportInterval = progressreportinterval == default ? TimeSpan.FromSeconds(1) : progressreportinterval
                        };
                    }

                    var hash = await UploadChunkAsync(stream, offset, length, chunkUrl, index, maxMbPerSec, token, progress).ConfigureAwait(false);

                    if (existing == null)
                        manifest.AddChunk(index, chunkName, hash, length, true);
                    else
                    {
                        existing.Completed = true;
                        existing.Hash = hash;
                        existing.SizeBytes = length;
                        existing.UploadedUtc = DateTime.UtcNow;
                    }

                    if (manifestStore != null)
                        await manifestStore.SaveAsync(manifest).ConfigureAwait(false);
                    _log($"Uploaded {chunkName} ({length / 1024 / 1024:N0} MB)");
                }
            }

            manifest.CompletedUtc = DateTime.UtcNow;
            if (manifestStore != null)
                await manifestStore.SaveAsync(manifest).ConfigureAwait(false);
            _log($"Upload complete: {file.Name}");
        }
        public void EnqueueDownload(TransferManifest manifest, int priority = 1)
        {
            if (manifest.NumChunks <= 1)
            {
                _queue.Enqueue(new TransferWork
                {
                    Kind = WorkKind.DownloadFile,
                    Manifest = manifest,
                    Priority = priority
                }, 0);
            }
            else
            {
                // Optional: front-load chunk 0
                for (int i = 0; i < manifest.Chunks.Count; i++)
                {
                    var c = manifest.Chunks[i];
                    if (!c.Completed) continue; // only queue chunks that exist remotely

                    var pr = (i == 0) ? 2 : 5;
                    _queue.Enqueue(new TransferWork
                    {
                        Kind = WorkKind.DownloadChunk,
                        Manifest = manifest,
                        Chunk = c,
                        Priority = pr
                    }, pr);
                }
            }
        }

        public void EnqueueUpload(TransferManifest manifest, int priority = 1)
        {
            if (manifest.NumChunks <= 1)
            {
                _queue.Enqueue(new TransferWork
                {
                    Kind = WorkKind.UploadFile,
                    Manifest = manifest,
                    Priority = priority
                }, 0);
            }
            else
            {
                for (int i = 0; i < manifest.Chunks.Count; i++)
                {
                    var c = manifest.Chunks[i];
                    if (c.Completed) continue;
                    var pr = (i == 0) ? 2 : 5;
                    _queue.Enqueue(new TransferWork
                    {
                        Kind = WorkKind.UploadChunk,
                        Manifest = manifest,
                        Chunk = c,
                        Priority = pr
                    }, pr);
                }
            }
        }
        public Task RunQueueAsync(CancellationToken token = default, Action<FileCopyProgress> progress = null, TimeSpan progressInterval = default)
        {
            // Launch N workers (N can be > _slots, the semaphore is the hard limit)
            var workerCount = Math.Max(_slots.CurrentCount * 2, 8);
            var workers = new List<Task>(workerCount);

            for (int w = 0; w < workerCount; w++)
            {
                workers.Add(Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!_queue.TryDequeue(out var work))
                        {
                            await Task.Delay(50, token).ConfigureAwait(false);
                            continue;
                        }

                        await _slots.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            switch (work.Kind)
                            {
                                case WorkKind.DownloadFile:
                                    // One-shot “simple” file: stream blob directly to final path
                                    await DownloadAsync(work.Manifest, work.LocalPath, default, _maxMBs, token, work.ProgressCallback, work.ProgressCallbackInterval).ConfigureAwait(false);
                                    break;

                                case WorkKind.UploadFile:
                                    await UploadAsync(new FileInfo(work.LocalPath), work.Manifest.RelativePath, _chunkSizeMb, _maxMBs, token, work.ProgressCallback, work.ProgressCallbackInterval).ConfigureAwait(false);
                                    break;

                                case WorkKind.DownloadChunk:
                                    {
                                        FileInfo finalPath;
                                        string tempPath;
                                        //Prepare only if file does not exist - otherwise this just sets the names
                                        PreparePartialFile(work.Manifest, work.LocalPath, out finalPath, out tempPath);

                                        // Open temp file with sharing, position in DownloadOneChunkAsync
                                        //using (var fs = new FileStream(
                                        //    tempPath, FileMode.Open, FileAccess.Write,
                                        //    FileShare.ReadWrite, 1024 * 1024,
                                        //    FileOptions.SequentialScan | FileOptions.Asynchronous))
                                        //{
                                            //await DownloadChunkAsync(
                                            //    work.Manifest, work.Chunk, tempPath,
                                            //    /* per-chunk throttle */ 0, token,
                                            //    progress == null ? null :
                                            //        new FileCopyProgress
                                            //        {
                                            //            FileName = work.Manifest.FileName + $" (Chunk {work.Chunk.Index + 1}/{work.Manifest.Chunks.Count})",
                                            //            FileSizeBytes = work.Chunk.SizeBytes,
                                            //            Stopwatch = Stopwatch.StartNew(),
                                            //            OperationDuring = "Downloading",
                                            //            OperationComplete = "Download",
                                            //            Callback = progress,
                                            //            ProgressReportInterval = (progressInterval == default ? TimeSpan.FromSeconds(1) : progressInterval)
                                            //        }).ConfigureAwait(false);
                                        //}
                                        // You can mark local state here if you want
                                        break;
                                    }

                                case WorkKind.UploadChunk:
                                    {
                                        // Reuse your existing per-chunk upload (open once, seek, stream)
                                        using (var fs = new FileStream(
                                            work.LocalPath, FileMode.Open, FileAccess.Read,
                                            FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                                        {
                                            throw new NotImplementedException();
                                            // Build chunk URL and stream with ThrottledHashingChunkStream as you already do
                                            // (You can factor existing code into a reusable method and call it here.)
                                        }
                                        //break;
                                    }
                            }
                        }
                        finally
                        {
                            _slots.Release();
                        }
                    }
                }, token));
            }

            return Task.WhenAll(workers);
        }
        private LocalDownloadState LoadLocalDownloadState(string path, long fileSize, long chunkSizeBytes)
        {
            try
            {
                if (File.Exists(path))
                {
                    using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        var ser = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(LocalDownloadState));
                        var s = (LocalDownloadState)ser.ReadObject(fs);
                        // If manifest changed (size/chunk), reset to avoid corruption
                        if (s.FileSize != fileSize || s.ChunkSizeBytes != chunkSizeBytes)
                            return new LocalDownloadState { FileSize = fileSize, ChunkSizeBytes = chunkSizeBytes };
                        return s;
                    }
                }
            }
            catch { /* fall through to fresh state */ }

            return new LocalDownloadState { FileSize = fileSize, ChunkSizeBytes = chunkSizeBytes };
        }
        // in TransferCoordinator
        private readonly SemaphoreSlim _stateIoLock = new SemaphoreSlim(1, 1);

        private async Task SaveLocalDownloadState(string path, LocalDownloadState state)
        {
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            await _stateIoLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = File.Open(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var ser = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(LocalDownloadState));
                    ser.WriteObject(fs, state);
                }
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            finally
            {
                _stateIoLock.Release();
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
        private async Task<string> UploadChunkAsync(FileStream fileStream, long offset, long length, string chunkUrl, int index, double maxMbPerSec, CancellationToken token, FileCopyProgress progress)
        {
            // This custom stream:
            // - restricts reads to [offset, offset+length)
            // - updates SHA256 as bytes are read (for per-chunk hash)
            // - throttles read pace to cap the effective upload bandwidth
            var reader = new ThrottledHashingReadStream(fileStream, offset, length, maxMbPerSec, progress);

            var request = new HttpRequestMessage(HttpMethod.Put, chunkUrl);
            // Required by Azure Blob REST for block blobs
            request.Headers.Add("x-ms-blob-type", "BlockBlob");

            var content = new StreamContent(reader); // streaming; no 1GB buffering
            content.Headers.ContentLength = length;  // avoid buffering because length is known
            request.Content = content;

            using (var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
            }

            // finalize hash AFTER HTTP has consumed the stream
            return reader.FinalizeHashAndGetHex();
        }
        private async Task DownloadChunkAsync2(TransferManifest manifest, ManifestChunk chunk, FileStream target, double maxMbPerSec, CancellationToken token, FileCopyProgress progress)
        {
            if (progress != null && !progress.HasIntegratedCallback) throw new ArgumentException("if progress is specified, it must have Integrated Callback");

            // Build chunk URL
            string chunkUrl;
            if (string.IsNullOrWhiteSpace(manifest.RelativePath))
                chunkUrl = _baseUrl + "/" + chunk.BlobName + "?" + _sas;
            else
                chunkUrl = _baseUrl + "/" + manifest.RelativePath + "/" + chunk.BlobName + "?" + _sas;

            using (var resp = await _http.GetAsync(chunkUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var sha = SHA256.Create())
                {
                    var offset = (long)chunk.Index * manifest.ChunkSizeBytes;
                    target.Position = offset;

                    var buffer = new byte[8 * 1024 * 1024]; // 8MB
                    long remaining = chunk.SizeBytes;
                    var sw = (maxMbPerSec > 0) ? Stopwatch.StartNew() : null;
                    long sentThisWindow = 0;
                    const int windowMs = 200; // throttle window

                    while (remaining > 0)
                    {
                        token.ThrowIfCancellationRequested();

                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = await src.ReadAsync(buffer, 0, toRead, token).ConfigureAwait(false);
                        if (read <= 0) throw new IOException("Unexpected EOF while downloading chunk.");

                        sha.TransformBlock(buffer, 0, read, null, 0);
                        await target.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);

                        remaining -= read;
                        progress?.UpdateAndMaybeCallback(read);
                        // Throttle by write-rate if requested
                        if (maxMbPerSec > 0)
                        {
                            sentThisWindow += read;
                            var elapsed = sw.ElapsedMilliseconds;
                            if (elapsed >= windowMs)
                            {
                                double targetBytesPerMs = (maxMbPerSec * 1024.0 * 1024.0) / 1000.0;
                                double expectedMs = sentThisWindow / targetBytesPerMs;
                                if (expectedMs > elapsed)
                                {
                                    int delay = (int)(expectedMs - elapsed);
                                    if (delay > 0) await Task.Delay(delay, token).ConfigureAwait(false);
                                }
                                sw.Restart();
                                sentThisWindow = 0;
                            }
                        }
                    }

                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var hex = ToHex(sha.Hash);

                    // Compare case-insensitive
                    if (!string.Equals(hex, chunk.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Hash mismatch for chunk {chunk.Index}. Expected {chunk.Hash}, got {hex}.");
                }
            }
        }
        private async Task DownloadChunkAsync3(TransferManifest manifest, ManifestChunk chunk, string tempPath, double maxMbPerSec, CancellationToken token, FileCopyProgress progress)
        {
            if (progress != null && !progress.HasIntegratedCallback) throw new ArgumentException("if progress is specified, it must have Integrated Callback");

            string chunkUrl;
            if (string.IsNullOrWhiteSpace(manifest.RelativePath))
                chunkUrl = _baseUrl + "/" + chunk.BlobName + "?" + _sas;
            else
                chunkUrl = _baseUrl + "/" + manifest.RelativePath + "/" + chunk.BlobName + "?" + _sas;



            using (var target = new FileStream(
                    tempPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.ReadWrite,                  // allow readers if you have any
                    8 * 1024 * 1024,                 // larger buffer
                    FileOptions.RandomAccess))       // concurrent random writes
            using (var resp = await _http.GetAsync(chunkUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();

                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var writer = new ThrottledHashingWriteStream(target, offset: (long)chunk.Index * manifest.ChunkSizeBytes, length: chunk.SizeBytes, maxMbPerSec: maxMbPerSec, progress: progress))
                {
                    // Stream copy; no buffering the whole chunk in RAM.
                    // Buffer size 8 MB matches your previous loop.
                    await src.CopyToAsync(writer, 8 * 1024 * 1024, token).ConfigureAwait(false);

                    var hex = writer.FinalizeHashAndGetHex();
                    if (!string.IsNullOrEmpty(chunk.Hash) &&
                        !string.Equals(hex, chunk.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Hash mismatch for chunk {chunk.Index}. Expected {chunk.Hash}, got {hex}.");
                    }
                }
            }
        }
        private async Task DownloadChunkAsync4(
    TransferManifest manifest, ManifestChunk chunk, string tempPath,
    double maxMbPerSec, CancellationToken token, FileCopyProgress progress)
        {
            if (progress != null && !progress.HasIntegratedCallback)
                throw new ArgumentException("if progress is specified, it must have Integrated Callback");

            // Build URL
            string chunkUrl = string.IsNullOrWhiteSpace(manifest.RelativePath)
                ? _baseUrl + "/" + chunk.BlobName + "?" + _sas
                : _baseUrl + "/" + manifest.RelativePath + "/" + chunk.BlobName + "?" + _sas;

            // Open a dedicated stream for this chunk (avoid shared FileStream contention)
            using (var target = new FileStream(
                tempPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1024 * 1024,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var resp = await _http.GetAsync(chunkUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var sha = SHA256.Create())
                {
                    var offset = (long)chunk.Index * manifest.ChunkSizeBytes;

                    // Manual positioning per write; don’t rely on shared Position
                    var buffer = new byte[1024 * 1024]; // 1 MB buffers often perform better than 8 MB with WinHTTP
                    long remaining = chunk.SizeBytes;

                    Stopwatch sw = null;
                    long sentWindow = 0;
                    const int windowMs = 200;
                    if (maxMbPerSec > 0) sw = Stopwatch.StartNew();

                    while (remaining > 0)
                    {
                        token.ThrowIfCancellationRequested();

                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = await src.ReadAsync(buffer, 0, toRead, token).ConfigureAwait(false);
                        if (read <= 0) throw new IOException("Unexpected EOF while downloading chunk.");

                        sha.TransformBlock(buffer, 0, read, null, 0);

                        // Write at explicit offset to avoid interfering with other writers
                        target.Seek(offset + (chunk.SizeBytes - remaining), SeekOrigin.Begin);
                        await target.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);

                        remaining -= read;
                        progress?.UpdateAndMaybeCallback(read);

                        if (sw != null)
                        {
                            sentWindow += read;
                            var elapsed = sw.ElapsedMilliseconds;
                            if (elapsed >= windowMs)
                            {
                                double bps = maxMbPerSec * 1024.0 * 1024.0;
                                double needMs = (sentWindow / bps) * 1000.0;
                                if (needMs > elapsed)
                                {
                                    int delay = (int)(needMs - elapsed);
                                    if (delay > 0) await Task.Delay(delay, token).ConfigureAwait(false);
                                }
                                sw.Restart();
                                sentWindow = 0;
                            }
                        }
                    }

                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var hex = ToHex(sha.Hash);
                    if (!hex.Equals(chunk.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Hash mismatch for chunk {chunk.Index}. Expected {chunk.Hash}, got {hex}.");
                }
            }
        }


        private async Task DownloadChunkAsync5(
    TransferManifest manifest,
    ManifestChunk chunk,
    string tempPath,
    double maxMbPerSec,
    CancellationToken token,
    FileCopyProgress progress)
        {
            if (progress != null && !progress.HasIntegratedCallback)
                throw new ArgumentException("if progress is specified, it must have Integrated Callback");

            // Build chunk URL
            string chunkUrl = string.IsNullOrWhiteSpace(manifest.RelativePath)
                ? _baseUrl + "/" + chunk.BlobName + "?" + _sas
                : _baseUrl + "/" + manifest.RelativePath + "/" + chunk.BlobName + "?" + _sas;

            // Where this chunk lives in the destination file
            long chunkFileOffset = (long)chunk.Index * manifest.ChunkSizeBytes;
            long chunkSize = chunk.SizeBytes;

            // --- NEW: fan-out inside the chunk ---
            const int defaultSegSizeMB = 32;   // good starting point; try 16–32 MB
            int segSize = defaultSegSizeMB * 1024 * 1024;
            int segCount = (int)Math.Max(1, (chunkSize + segSize - 1) / segSize);
            // clamp segment count so we don’t explode connections (tune as needed)
            segCount = Math.Min(segCount, 16);

            // Shared hasher fed in segment order; we’ll buffer each segment’s bytes.
            // (You still get end-to-end per-chunk SHA-256 check.)
            using (var sha = SHA256.Create())
            {
                //var segmentBuffers = new byte[segCount][];
                var tasks = new List<Task>(segCount);

                for (int segIndex = 0; segIndex < segCount; segIndex++)
                {
                    int localSegIndex = segIndex;
                    tasks.Add(Task.Run(async () =>
                    {
                        token.ThrowIfCancellationRequested();

                        long segStartInChunk = (long)localSegIndex * segSize;
                        long segLen = Math.Min(segSize, chunkSize - segStartInChunk);
                        if (segLen <= 0) return;

                        long absoluteStart = segStartInChunk;
                        long absoluteEndInclusive = segStartInChunk + segLen - 1;

                        // GET with Range
                        var req = new HttpRequestMessage(HttpMethod.Get, chunkUrl);
                        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(absoluteStart, absoluteEndInclusive);

                        var buffersize = 8 * 1024 * 1024;

                        using (var target = new FileStream(
                                    tempPath,
                                    FileMode.Open,
                                    FileAccess.Write,
                                    FileShare.ReadWrite,                  // allow readers if you have any
                                    buffersize,                 // larger buffer
                                    FileOptions.RandomAccess))       // concurrent random writes
                        using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                        {
                            resp.EnsureSuccessStatusCode();
                            target.Position = chunkFileOffset + segStartInChunk;
                            // read into a single buffer (keeps write/seek simple and lets us hash in order)
                            var buf = new byte[8 * 1024 * 1024];
                            int filled = 0;
                            using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                while (filled < segLen)
                                {
                                    int toRead = (int)Math.Min(buffersize, segLen - filled);
                                    int r = await src.ReadAsync(buf, 0, toRead, token).ConfigureAwait(false);
                                    if (r <= 0) throw new IOException("Unexpected EOF in ranged response.");
                                    filled += r;
                                    await target.WriteAsync(buf, 0, r, token).ConfigureAwait(false);
                                    progress?.UpdateAndMaybeCallback(r);
                                }
                            }
                        }
                    }, token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Write segments to disk in order (one FS handle; writes are fast)
                //for (int segIndex = 0; segIndex < segCount; segIndex++)
                //{
                //    var buf = segmentBuffers[segIndex];
                //    if (buf == null || buf.Length == 0) continue;

                //    long segStartInChunk = (long)segIndex * segSize;
                //    target.Position = chunkFileOffset + segStartInChunk;

                //    // hash & write
                //    sha.TransformBlock(buf, 0, buf.Length, null, 0);
                //    await target.WriteAsync(buf, 0, buf.Length, token).ConfigureAwait(false);
                //}

                //sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                //var hex = ToHex(sha.Hash);
                //if (!string.Equals(hex, chunk.Hash, StringComparison.OrdinalIgnoreCase))
                //    throw new InvalidOperationException($"Hash mismatch for chunk {chunk.Index}. Expected {chunk.Hash}, got {hex}.");
            }
        }

        private async Task DownloadChunkAsync(
    TransferManifest manifest,
    ManifestChunk chunk,
    string tempPath,
    double maxMbPerSec,
    CancellationToken token,
    FileCopyProgress progress)
        {
            if (progress != null && !progress.HasIntegratedCallback)
                throw new ArgumentException("if progress is specified, it must have Integrated Callback");

            // URL for this chunk
            string chunkUrl = string.IsNullOrWhiteSpace(manifest.RelativePath)
                ? _baseUrl + "/" + chunk.BlobName + "?" + _sas
                : _baseUrl + "/" + manifest.RelativePath + "/" + chunk.BlobName + "?" + _sas;

            // Offset & sizes
            long chunkFileOffset = (long)chunk.Index * manifest.ChunkSizeBytes;
            long chunkSize = chunk.SizeBytes;

            // Segment fan-out
            const int defaultSegSizeMB = 16;                      // tune 16–32MB
            int segSize = defaultSegSizeMB * 1024 * 1024;
            int segCount = (int)Math.Max(1, (chunkSize + segSize - 1) / segSize);
            segCount = Math.Min(segCount, 16);                    // cap concurrency per chunk

            // One task per segment
            var tasks = new List<Task>(segCount);

            for (int segIndex = 0; segIndex < segCount; segIndex++)
            {
                int sidx = segIndex;
                tasks.Add(Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();

                    long segStartInChunk = (long)sidx * segSize;
                    long segLen = Math.Min(segSize, chunkSize - segStartInChunk);
                    if (segLen <= 0) return;

                    long rangeStart = segStartInChunk;                  // range within the chunk-blob
                    long rangeEnd = segStartInChunk + segLen - 1;     // inclusive

                    // Prepare HTTP range GET
                    var req = new HttpRequestMessage(HttpMethod.Get, chunkUrl);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(rangeStart, rangeEnd);

                    // Independent writer for this segment
                    const int writeBuf = 1024 * 1024; // 1MB read/write buffers are often sweet-spot
                    using (var target = new FileStream(
                        tempPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        writeBuf,
                        FileOptions.RandomAccess | FileOptions.Asynchronous))
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();

                        target.Seek(chunkFileOffset + segStartInChunk, SeekOrigin.Begin);

                        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            var buf = new byte[writeBuf];
                            long remaining = segLen;

                            // Optional: simple throttle window
                            Stopwatch sw = null;
                            long sentWindow = 0;
                            const int windowMs = 200;
                            if (maxMbPerSec > 0) sw = Stopwatch.StartNew();

                            while (remaining > 0)
                            {
                                token.ThrowIfCancellationRequested();

                                int toRead = (int)Math.Min((long)buf.Length, remaining);
                                int r = await src.ReadAsync(buf, 0, toRead, token).ConfigureAwait(false);
                                if (r <= 0) throw new IOException("Unexpected EOF in ranged response.");

                                await target.WriteAsync(buf, 0, r, token).ConfigureAwait(false);

                                remaining -= r;
                                progress?.UpdateAndMaybeCallback(r);

                                if (sw != null)
                                {
                                    sentWindow += r;
                                    var elapsed = sw.ElapsedMilliseconds;
                                    if (elapsed >= windowMs)
                                    {
                                        double bps = maxMbPerSec * 1024.0 * 1024.0;
                                        double needMs = (sentWindow / bps) * 1000.0;
                                        if (needMs > elapsed)
                                        {
                                            int delay = (int)(needMs - elapsed);
                                            if (delay > 0) await Task.Delay(delay, token).ConfigureAwait(false);
                                        }
                                        sw.Restart();
                                        sentWindow = 0;
                                    }
                                }
                            }
                        }
                    }
                }, token));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // If you want per-chunk hash verification, do a post-pass here:
            // using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            // { fs.Seek(chunkFileOffset, SeekOrigin.Begin); hash the next chunk.SizeBytes, compare to chunk.Hash }
        }


    }
    public enum TransferOutcome
    {
        Complete = 0,
        Pending = 1
    }
    [DataContract]
    internal sealed class LocalDownloadState
    {
        [DataMember(Order = 1)]
        public long FileSize;

        [DataMember(Order = 2)]
        public long ChunkSizeBytes;

        [DataMember(Order = 3)]
        public HashSet<int> Completed = new HashSet<int>();

        public int NumChunks => (int)Math.Ceiling((double)FileSize / ChunkSizeBytes);
        public override string ToString() => $"{Completed.Count}/{NumChunks} Chunks Completed";
    }
    internal enum WorkKind { UploadFile, DownloadFile, UploadChunk, DownloadChunk }

    internal sealed class TransferWork
    {
        public WorkKind Kind;
        public TransferManifest Manifest;  // always set
        public string LocalPath;
        public ManifestChunk Chunk;      // set for chunk work
        public int Priority;             // lower == higher priority
        public Action<FileCopyProgress> ProgressCallback;
        public TimeSpan ProgressCallbackInterval;
    }

}
