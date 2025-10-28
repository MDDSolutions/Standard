using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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
        private readonly HttpClient _http = new HttpClient();
        private readonly string _baseUrl;   // Container URL, e.g. https://acct.blob.core.windows.net/backups !!no folder
        private readonly string _azureFolderPath;  //name of folder, e.g. Everest (presumably folder/subfolder would also work)
        private readonly string _sas;       // Just the ?sp=... part
        private ManifestStoreHttp _manifestStore;
        private readonly string _localPath;
        private string _localFile;
        private string _manifestUrl; // full blob URL for manifest
        private string _manifestBlobName;
        private readonly Action<string> _log;

        public TransferCoordinator(string baseUrl, string azureFolderPath, string sasToken, string localPath, string localFile = null, Action<string> log = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _azureFolderPath = azureFolderPath.TrimStart('/').TrimEnd('/');
            _sas = sasToken.TrimStart('?');
            _log = log ?? (_ => { });
            _localPath = localPath.TrimEnd('\\');
            if (!string.IsNullOrWhiteSpace(localFile)) SetLocalFile(localFile);
        }
        public void SetLocalFile(string localFile)
        {
            _localFile = localFile;
            // derive manifest name from filename

            if (string.IsNullOrWhiteSpace(_localFile))
            {
                _manifestBlobName = null;
                _manifestUrl = null;
                _manifestStore = null;
            }
            else
            {
                _manifestBlobName = localFile + ".manifest";
                if (string.IsNullOrWhiteSpace(_azureFolderPath))
                    _manifestUrl = $"{_baseUrl}/{_manifestBlobName}?{_sas}";
                else
                    _manifestUrl = $"{_baseUrl}/{_azureFolderPath}/{_manifestBlobName}?{_sas}";
                _manifestStore = new ManifestStoreHttp(_manifestUrl);
            }
        }
        public void SetManifest(BackupManifest manifest)
        {
            if (manifest == null)
            {
                _localFile = null;
                _manifestBlobName = null;
                _manifestStore = null;
                _manifestUrl = null;
            }
            else
            {
                _localFile = manifest.FileName;
                _manifestBlobName = manifest.FileName + ".manifest";
                if (string.IsNullOrWhiteSpace(_azureFolderPath))
                    _manifestUrl = $"{_baseUrl}/{_manifestBlobName}?{_sas}";
                else
                    _manifestUrl = $"{_baseUrl}/{_azureFolderPath}/{_manifestBlobName}?{_sas}";
                _manifestStore= new ManifestStoreHttp(_manifestUrl);
            }
        }
        public async Task<List<BackupManifest>> FindManifestsAsync()
        {
            var list = new List<BackupManifest>();
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
                    if (Regex.IsMatch(name, @"\.chunk\d{5}$"))
                        continue;
                    if (!string.IsNullOrWhiteSpace(_azureFolderPath))
                        name = name.Substring(_azureFolderPath.Length + 1);
                    manifestfiles.Add(name);
                }
            }

            foreach (var file in manifestfiles)
            {
                var manifeststore = new ManifestStoreHttp($"{_baseUrl}/{_azureFolderPath}/{file}?{_sas}");
                var manifest = await manifeststore.TryLoadAsync();
                if (manifest != null)
                    list.Add(manifest);
            }

            return list;
        }
        public async Task<TransferOutcome> DownloadAsync(TimeSpan pollinterval = default, double maxMbPerSec = 0, CancellationToken token = default, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default)
        {
            if (string.IsNullOrWhiteSpace(_localFile))
                throw new InvalidOperationException("Local file name is not set. Call SetManifest(manifest) or SetLocalFile(name) first.");

            // Always load the latest remote manifest
            var manifest = await _manifestStore.TryLoadAsync().ConfigureAwait(false);
            if (manifest == null)
                throw new IOException("Remote manifest not found or unreadable.");

            // Prepare local target (temp path we’ll rename at the very end)
            var finalPath = new FileInfo(Path.Combine(_localPath, _localFile));
            var tempPath = finalPath.FullName + ".partial";

            // Load or create our local download state
            var state = LoadLocalDownloadState(manifest.FileSize, manifest.ChunkSizeBytes);

            // Ensure file exists and is pre-sized
            Directory.CreateDirectory(_localPath);
            using (var fs = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
            {
                if (fs.Length != manifest.FileSize)
                {
                    fs.SetLength(manifest.FileSize);
                    fs.Flush(true);
                }

                // Work loop: download any completed remote chunks we don't have yet
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    bool didWork = false;
                    foreach (var chunk in manifest.Chunks)
                    {
                        if (!chunk.Completed) continue;                 // only download completed (uploaded) chunks
                        if (state.Completed.Contains(chunk.Index)) continue; // already have

                        FileCopyProgress progress = null;
                        if (progresscallback != null)
                        {
                            progress = new FileCopyProgress
                            {
                                FileName = manifest.FileName + (state.NumChunks > 1 ? $" ({state.ToString()})" : ""),
                                FileSizeBytes = chunk.SizeBytes,
                                Stopwatch = Stopwatch.StartNew(),
                                OperationDuring = "Downloading",
                                OperationComplete = "Download",
                                Callback = progresscallback,
                                ProgressReportInterval = progressreportinterval == default ? TimeSpan.FromSeconds(1) : progressreportinterval
                            };
                        }

                        await DownloadOneChunkAsync(manifest, chunk, fs, maxMbPerSec, token, progress).ConfigureAwait(false);
                        state.Completed.Add(chunk.Index);
                        SaveLocalDownloadState(state);
                        _log("Downloaded chunk " + (chunk.Index + 1) + "/" + manifest.Chunks.Count);
                        didWork = true;
                    }

                    // If we just finished everything and the uploader marked the file complete, finalize.
                    if (state.Completed.Count == manifest.Chunks.Count && manifest.CompletedUtc.HasValue)
                    {
                        fs.Flush(true);
                        break;
                    }

                    // Not all chunks are available from the uploader yet
                    if (!didWork)
                    {
                        if (pollinterval > TimeSpan.Zero)
                        {
                            await Task.Delay(pollinterval, token).ConfigureAwait(false);
                            // Re-load manifest to see if new chunks appeared or CompletedUtc flipped
                            manifest = await _manifestStore.TryLoadAsync().ConfigureAwait(false);
                            if (manifest == null) throw new IOException("Remote manifest disappeared during download.");
                            continue;
                        }
                        else
                        {
                            // Exit now with partial file + local state so a scheduler can resume later
                            _log("Download pending: partial file saved; waiting for more chunks.");
                            return TransferOutcome.Pending;
                        }
                    }

                    // We did some work; loop again to see if more chunks are now available
                    // (or poll if uploader hasn’t published new ones yet)
                    if (pollinterval > TimeSpan.Zero)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                        manifest = await _manifestStore.TryLoadAsync().ConfigureAwait(false);
                        if (manifest == null) throw new IOException("Remote manifest disappeared during download.");
                    }
                }
            }
            // If we just finished everything and the uploader marked the file complete, finalize.
            if (state.Completed.Count == manifest.Chunks.Count && manifest.CompletedUtc.HasValue)
            {
                if (finalPath.Exists) finalPath.Delete();
                File.Move(tempPath, finalPath.FullName);
                // Remove our local state – we’re done
                try { File.Delete(LocalDownloadStatePath); } catch { }
                _log("Download complete: " + _localFile);
                return TransferOutcome.Complete;
            }
            return TransferOutcome.Pending;
        }
        public async Task UploadAsync(int chunkSizeMb = 1024, double maxMbPerSec = 0, CancellationToken token = default, Action<FileCopyProgress> progresscallback = null, TimeSpan progressreportinterval = default)
        {
            var file = new FileInfo(Path.Combine(_localPath,_localFile));
            if (!file.Exists) throw new FileNotFoundException("File not found", file.FullName);

            // Load or create manifest specific to this file
            var manifest = await _manifestStore.TryLoadAsync().ConfigureAwait(false);
            if (manifest == null)
            {
                manifest = new BackupManifest
                {
                    FileName = file.Name,
                    FileSize = file.Length,
                    ChunkSizeBytes = chunkSizeMb * 1024 * 1024
                };
                await _manifestStore.SaveAsync(manifest).ConfigureAwait(false);
                _log($"Created new manifest: {_manifestBlobName}");
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
                    var chunkName = $"{file.Name}.chunk{index:D5}";
                    string chunkUrl;
                    if (string.IsNullOrWhiteSpace(_azureFolderPath))
                        chunkUrl = $"{_baseUrl}/{chunkName}?{_sas}";
                    else
                        chunkUrl = $"{_baseUrl}/{_azureFolderPath}/{chunkName}?{_sas}";


                    FileCopyProgress progress = null;
                    if (progresscallback != null)
                    {
                        progress = new FileCopyProgress
                        {
                            FileName = file.Name + (totalChunks > 1 ? $" (Chunk {index}/{totalChunks})" : ""),
                            FileSizeBytes = length,
                            Stopwatch = Stopwatch.StartNew(),
                            OperationDuring = "Uploading",
                            OperationComplete = "Upload",
                            Callback = progresscallback,
                            ProgressReportInterval = progressreportinterval == default ? TimeSpan.FromSeconds(1) : progressreportinterval
                        };
                    }


                    //var hash = await UploadChunkAsync(stream, offset, length, chunkUrl, index, maxMbPerSec, token).ConfigureAwait(false);

                    // This custom stream:
                    // - restricts reads to [offset, offset+length)
                    // - updates SHA256 as bytes are read (for per-chunk hash)
                    // - throttles read pace to cap the effective upload bandwidth
                    var reader = new ThrottledHashingChunkStream(stream, offset, length, maxMbPerSec, progress);

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
                    var hash = reader.FinalizeHashAndGetHex();




                    if (existing == null)
                        manifest.AddChunk(index, chunkName, hash, length, true);
                    else
                    {
                        existing.Completed = true;
                        existing.Hash = hash;
                        existing.SizeBytes = length;
                        existing.UploadedUtc = DateTime.UtcNow;
                    }

                    await _manifestStore.SaveAsync(manifest).ConfigureAwait(false);
                    _log($"Uploaded {chunkName} ({length / 1024 / 1024:N0} MB)");
                }
            }

            manifest.CompletedUtc = DateTime.UtcNow;
            await _manifestStore.SaveAsync(manifest).ConfigureAwait(false);
            _log($"Upload complete: {file.Name}");
        }


        private string BuildChunkUrl(string chunkName)
        {
            if (string.IsNullOrWhiteSpace(_azureFolderPath))
                return _baseUrl + "/" + chunkName + "?" + _sas;
            return _baseUrl + "/" + _azureFolderPath + "/" + chunkName + "?" + _sas;
        }
        private async Task<string> UploadChunkAsync(FileStream fileStream, long offset, long length, string chunkUrl, int index, double maxMbPerSec, CancellationToken token)
        {
            // This custom stream:
            // - restricts reads to [offset, offset+length)
            // - updates SHA256 as bytes are read (for per-chunk hash)
            // - throttles read pace to cap the effective upload bandwidth
            var reader = new ThrottledHashingChunkStream(fileStream, offset, length, maxMbPerSec);

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
        private string LocalDownloadStatePath
        {
            get
            {
                var name = _localFile ?? "unknown";
                return Path.Combine(_localPath, "." + name + ".dl.json");
            }
        }
        private LocalDownloadState LoadLocalDownloadState(long fileSize, long chunkSizeBytes)
        {
            var path = LocalDownloadStatePath;
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
        private void SaveLocalDownloadState(LocalDownloadState state)
        {
            var path = LocalDownloadStatePath;
            var tmp = path + ".tmp";
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
        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }
        private async Task DownloadOneChunkAsync(BackupManifest manifest, ManifestChunk chunk, FileStream target, double maxMbPerSec, CancellationToken token, FileCopyProgress progress)
        {
            if (progress != null && !progress.HasIntegratedCallback) throw new ArgumentException("if progress is specified, it must have Integrated Callback");

            // Build chunk URL
            string chunkUrl;
            if (string.IsNullOrWhiteSpace(_azureFolderPath))
                chunkUrl = _baseUrl + "/" + chunk.BlobName + "?" + _sas;
            else
                chunkUrl = _baseUrl + "/" + _azureFolderPath + "/" + chunk.BlobName + "?" + _sas;

            using (var resp = await _http.GetAsync(chunkUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var sha = System.Security.Cryptography.SHA256.Create())
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
}
