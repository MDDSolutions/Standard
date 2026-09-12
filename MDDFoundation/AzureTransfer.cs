using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    /// <summary>
    /// Provides utilities for transferring large files to and from Azure Blob Storage
    /// using chunked uploads and downloads coordinated through a status manifest.
    /// </summary>
    public class AzureTransfer : IDisposable
    {
        private readonly Uri containerUri;
        private readonly string sasToken;
        private readonly HttpClient httpClient;
        private readonly bool ownsHttpClient;
        private bool disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="AzureTransfer"/> class.
        /// </summary>
        /// <param name="containerUrl">The base URL for the Azure blob container (without SAS query string).</param>
        /// <param name="sasToken">The SAS token providing access to the container.</param>
        /// <param name="httpClient">Optional <see cref="HttpClient"/> instance. If null, a new instance is created.</param>
        public AzureTransfer(string containerUrl, string sasToken, HttpClient httpClient = null)
        {
            if (string.IsNullOrWhiteSpace(containerUrl)) throw new ArgumentNullException(nameof(containerUrl));

            containerUri = new Uri(containerUrl.TrimEnd('/'));
            this.sasToken = sasToken?.TrimStart('?');
            if (httpClient == null)
            {
                this.httpClient = CreateDefaultHttpClient();
                ownsHttpClient = true;
            }
            else
            {
                this.httpClient = httpClient;
                ownsHttpClient = false;
            }
        }

        /// <summary>
        /// Uploads the specified file to Azure Blob Storage in multiple chunk blobs and maintains
        /// a manifest to coordinate with concurrent download operations.
        /// </summary>
        /// <param name="file">The file to upload.</param>
        /// <param name="baseBlobName">The base name to use for the uploaded blobs.</param>
        /// <param name="chunkSizeMb">The chunk size in megabytes. Defaults to 64MB.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task UploadFileInChunksAsync(FileInfo file, string baseBlobName, int chunkSizeMb = 64, CancellationToken token = default)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (!file.Exists) throw new FileNotFoundException("Source file does not exist", file.FullName);
            if (string.IsNullOrWhiteSpace(baseBlobName)) throw new ArgumentNullException(nameof(baseBlobName));
            if (chunkSizeMb <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSizeMb));

            var chunkSizeBytes = chunkSizeMb * 1024L * 1024L;
            if (chunkSizeBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(chunkSizeMb), "Chunk size must be less than 2GB.");

            var totalChunks = (int)Math.Ceiling((double)file.Length / chunkSizeBytes);
            var statusBlobName = GetStatusBlobName(baseBlobName);

            var status = new TransferStatus
            {
                FileName = file.Name,
                FileSize = file.Length,
                ChunkSize = (int)chunkSizeBytes,
                TotalChunks = totalChunks,
                CompletedChunks = 0,
                Hash = string.Empty
            };

            await UploadStatusAsync(statusBlobName, status, token).ConfigureAwait(false);

            using (var sha = SHA256.Create())
            using (var fileStream = file.OpenRead())
            {
                var buffer = new byte[(int)chunkSizeBytes];
                for (int index = 0; index < totalChunks; index++)
                {
                    token.ThrowIfCancellationRequested();

                    var bytesRead = 0;
                    while (bytesRead < buffer.Length)
                    {
                        var read = fileStream.Read(buffer, bytesRead, buffer.Length - bytesRead);
                        if (read == 0) break;
                        bytesRead += read;
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    sha.TransformBlock(buffer, 0, bytesRead, null, 0);

                    var chunkName = GetChunkBlobName(baseBlobName, index);
                    await UploadChunkAsync(chunkName, buffer, bytesRead, token).ConfigureAwait(false);

                    status.CompletedChunks = index + 1;
                    await UploadStatusAsync(statusBlobName, status, token).ConfigureAwait(false);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                status.Hash = BitConverter.ToString(sha.Hash).Replace("-", string.Empty, StringComparison.Ordinal);
            }

            await UploadStatusAsync(statusBlobName, status, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Downloads chunks previously uploaded via <see cref="UploadFileInChunksAsync"/>, waits for
        /// the manifest to indicate chunk availability, and reassembles the content on disk. The SHA256 hash
        /// stored in the manifest is validated before the final file is made available.
        /// </summary>
        /// <param name="baseBlobName">The base blob name that identifies the chunk set.</param>
        /// <param name="destinationFilePath">The destination path for the assembled file.</param>
        /// <param name="pollInterval">How frequently to poll the manifest while waiting for chunks or hash availability.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task DownloadAndAssembleAsync(string baseBlobName, string destinationFilePath, TimeSpan? pollInterval = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(baseBlobName)) throw new ArgumentNullException(nameof(baseBlobName));
            if (string.IsNullOrWhiteSpace(destinationFilePath)) throw new ArgumentNullException(nameof(destinationFilePath));

            if (!pollInterval.HasValue)
            {
                pollInterval = TimeSpan.FromSeconds(2);
            }
            var statusBlobName = GetStatusBlobName(baseBlobName);
            TransferStatus status = null;

            do
            {
                token.ThrowIfCancellationRequested();
                status = await TryGetStatusAsync(statusBlobName, token).ConfigureAwait(false);
                if (status == null)
                {
                    await Task.Delay(pollInterval.Value, token).ConfigureAwait(false);
                }
            }
            while (status == null);

            var destinationDirectory = Path.GetDirectoryName(destinationFilePath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            var tempPath = destinationFilePath + ".partial";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using (var sha = SHA256.Create())
            using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (status.TotalChunks <= 0)
                {
                    throw new InvalidDataException($"Status manifest for {baseBlobName} reports no chunks to download.");
                }

                var buffer = new byte[Math.Max(status.ChunkSize, 1)];
                for (int index = 0; index < status.TotalChunks; index++)
                {
                    while (status.CompletedChunks <= index)
                    {
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(pollInterval.Value, token).ConfigureAwait(false);
                        status = await GetStatusWithRetryAsync(statusBlobName, pollInterval.Value, token).ConfigureAwait(false);
                    }

                    var chunkName = GetChunkBlobName(baseBlobName, index);
                    var bytesCopied = await DownloadChunkAsync(chunkName, buffer, destination, token).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, bytesCopied, null, 0);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var computedHash = BitConverter.ToString(sha.Hash).Replace("-", string.Empty, StringComparison.Ordinal);

                while (string.IsNullOrEmpty(status.Hash))
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(pollInterval.Value, token).ConfigureAwait(false);
                    status = await GetStatusWithRetryAsync(statusBlobName, pollInterval.Value, token).ConfigureAwait(false);
                }

                if (!string.Equals(status.Hash, computedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Hash mismatch while downloading {baseBlobName}. Expected {status.Hash}, computed {computedHash}.");
                }
            }

            if (File.Exists(destinationFilePath))
            {
                File.Delete(destinationFilePath);
            }

            File.Move(tempPath, destinationFilePath);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (ownsHttpClient)
            {
                httpClient?.Dispose();
            }
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.Add("x-ms-version", "2020-10-02");
            return client;
        }

        private Uri BuildBlobUri(string blobName, string additionalQuery = null)
        {
            var builder = new UriBuilder(containerUri)
            {
                Path = $"{containerUri.AbsolutePath.TrimEnd('/')}/{blobName}"
            };

            var queries = new List<string>();
            if (!string.IsNullOrEmpty(sasToken))
            {
                queries.Add(sasToken);
            }
            if (!string.IsNullOrEmpty(additionalQuery))
            {
                queries.Add(additionalQuery.TrimStart('?'));
            }

            builder.Query = string.Join("&", queries);
            return builder.Uri;
        }

        private async Task UploadChunkAsync(string blobName, byte[] buffer, int length, CancellationToken token)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, BuildBlobUri(blobName));
            request.Headers.Add("x-ms-blob-type", "BlockBlob");
            var content = new ByteArrayContent(buffer, 0, length);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content = content;

            using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
            }
        }

        private async Task UploadStatusAsync(string blobName, TransferStatus status, CancellationToken token)
        {
            var content = SerializeStatus(status);
            var request = new HttpRequestMessage(HttpMethod.Put, BuildBlobUri(blobName));
            request.Headers.Add("x-ms-blob-type", "BlockBlob");
            request.Content = new StringContent(content, Encoding.UTF8, "text/plain");

            using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
            }
        }

        private async Task<TransferStatus> TryGetStatusAsync(string blobName, CancellationToken token)
        {
            try
            {
                using (var response = await httpClient.GetAsync(BuildBlobUri(blobName), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return DeserializeStatus(payload);
                }
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        private async Task<TransferStatus> GetStatusWithRetryAsync(string blobName, TimeSpan pollInterval, CancellationToken token)
        {
            TransferStatus status = null;
            do
            {
                status = await TryGetStatusAsync(blobName, token).ConfigureAwait(false);
                if (status == null)
                {
                    await Task.Delay(pollInterval, token).ConfigureAwait(false);
                }
            }
            while (status == null);

            return status;
        }

        private async Task<int> DownloadChunkAsync(string blobName, byte[] buffer, Stream destination, CancellationToken token)
        {
            using (var response = await httpClient.GetAsync(BuildBlobUri(blobName), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var totalRead = 0;
                    int read;
                    while ((read = await responseStream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, token).ConfigureAwait(false)) > 0)
                    {
                        totalRead += read;
                        if (totalRead == buffer.Length)
                        {
                            break;
                        }
                    }

                    await destination.WriteAsync(buffer, 0, totalRead, token).ConfigureAwait(false);

                    if (totalRead == buffer.Length)
                    {
                        var extraBuffer = new byte[1];
                        var extraRead = await responseStream.ReadAsync(extraBuffer, 0, 1, token).ConfigureAwait(false);
                        if (extraRead > 0)
                        {
                            throw new InvalidDataException($"Chunk {blobName} exceeded the expected size of {buffer.Length} bytes.");
                        }
                    }

                    return totalRead;
                }
            }
        }

        private static string GetChunkBlobName(string baseName, int index)
        {
            return $"{baseName}.chunk{index.ToString("D6", CultureInfo.InvariantCulture)}";
        }

        private static string GetStatusBlobName(string baseName)
        {
            return $"{baseName}.status";
        }

        private static string SerializeStatus(TransferStatus status)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"FileName={status.FileName}");
            builder.AppendLine($"FileSize={status.FileSize}");
            builder.AppendLine($"ChunkSize={status.ChunkSize}");
            builder.AppendLine($"TotalChunks={status.TotalChunks}");
            builder.AppendLine($"CompletedChunks={status.CompletedChunks}");
            builder.AppendLine($"Hash={status.Hash}");
            return builder.ToString();
        }

        private static TransferStatus DeserializeStatus(string content)
        {
            var status = new TransferStatus();
            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var separatorIndex = line.IndexOf('=');
                if (separatorIndex < 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim();
                switch (key)
                {
                    case "FileName":
                        status.FileName = value;
                        break;
                    case "FileSize":
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                        {
                            status.FileSize = size;
                        }
                        break;
                    case "ChunkSize":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chunkSize))
                        {
                            status.ChunkSize = chunkSize;
                        }
                        break;
                    case "TotalChunks":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalChunks))
                        {
                            status.TotalChunks = totalChunks;
                        }
                        break;
                    case "CompletedChunks":
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var completedChunks))
                        {
                            status.CompletedChunks = completedChunks;
                        }
                        break;
                    case "Hash":
                        status.Hash = value;
                        break;
                }
            }

            return status;
        }

        private class TransferStatus
        {
            public string FileName { get; set; }
            public long FileSize { get; set; }
            public int ChunkSize { get; set; }
            public int TotalChunks { get; set; }
            public int CompletedChunks { get; set; }
            public string Hash { get; set; }
        }
    }
}
