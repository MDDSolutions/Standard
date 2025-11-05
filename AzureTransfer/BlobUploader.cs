using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AzureTransfer.Helpers;

namespace AzureTransfer
{
    public static class BlobUploader
    {
        private const int MaxAzureBlocks = 50_000;

        public static async Task UploadAsync(
            BlockBlobClient blob,
            string sourcePath,
            TransferOptions opt,
            IProgress<long>? progress = null,
            CancellationToken ct = default)
        {
            int blockBytes = checked(opt.BlockSizeMB * 1024 * 1024);
            int azureBytes = checked(opt.AzureBlockSizeMB * 1024 * 1024);
            int bufferBytes = checked(opt.BufferSizeMB * 1024 * 1024);
            if (azureBytes <= 0 || bufferBytes <= 0 || blockBytes <= 0) throw new ArgumentOutOfRangeException();
            if (azureBytes > blockBytes) azureBytes = blockBytes; // cap

            long fileLen = new FileInfo(sourcePath).Length;
            if (fileLen == 0)
            {
                await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct);
                using var empty = new MemoryStream(Array.Empty<byte>());
                await blob.UploadAsync(empty, cancellationToken: ct);
                return;
            }

            // Plan Azure blocks for the whole file
            long azureCountLong = (fileLen + azureBytes - 1) / azureBytes;
            if (azureCountLong > MaxAzureBlocks)
                throw new InvalidOperationException($"Azure block count {azureCountLong} exceeds limit {MaxAzureBlocks}; increase AzureBlockSizeMB.");

            int azureCount = checked((int)azureCountLong);
            // Deterministic file-wide block IDs
            string[] azureIds = Enumerable.Range(0, azureCount).Select(i => Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(i.ToString("D12")))).ToArray();

            var pub = new PublishState(azureCount);
            // --- Publisher task (runs alongside staging) ---
            const long PUBLISH_BYTES = 1L * 1024 * 1024 * 1024; // e.g., publish every 1 GiB
            var publishCts = new CancellationTokenSource();

            var publisher = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        publishCts.Token.ThrowIfCancellationRequested();
                        await Task.Delay(TimeSpan.FromSeconds(2), publishCts.Token);

                        // Find the largest contiguous prefix [0..k] with all Staged==true
                        int k = pub.LastPublished;
                        while (k + 1 < pub.Staged.Length && Volatile.Read(ref pub.Staged[k + 1]))
                            k++;

                        if (k > pub.LastPublished)
                        {
                            long advanced = ((long)(k - pub.LastPublished)) * azureBytes;
                            if (advanced >= PUBLISH_BYTES || pub.UploadDone)
                            {
                                // Commit prefix 0..k
                                await blob.CommitBlockListAsync(azureIds.AsSpan(0, k + 1).ToArray(),
                                                                conditions: null,
                                                                cancellationToken: publishCts.Token);

                                // Optional: create snapshot here if you want readers to pin stable views
                                // var snap = await blob.CreateSnapshotAsync(cancellationToken: publishCts.Token);

                                pub.LastPublished = k;
                                pub.LastPublishedBytes += advanced;
                            }
                        }

                        if (pub.UploadDone)
                            break;
                    }
                }
                catch (OperationCanceledException) { /* normal on shutdown */ }
            }, publishCts.Token);





            // State for resume at the **app block** level
            string sidecar = sourcePath + ".xfer.json";
            long appBlockCountLong = (fileLen + blockBytes - 1) / blockBytes;
            int appBlockCount = checked((int)appBlockCountLong);
            var state = LoadOrInitState(sidecar, "upload", sourcePath, blob.Uri.ToString(), fileLen, opt, appBlockCount);

            using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                          bufferSize: 1024 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            long transferred = 0;
            var sem = new SemaphoreSlim(opt.DegreeOfParallelism, opt.DegreeOfParallelism);
            var tasks = new List<Task>();

            for (int abi = 0; abi < appBlockCount; abi++)
            {
                if (state.Completed[abi])
                {
                    transferred += GetBlockRange(fileLen, abi, blockBytes).Length;
                    progress?.Report(transferred);
                    continue;
                }

                await sem.WaitAsync(ct);
                int appIndex = abi;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var (appOffset, appLen) = GetBlockRange(fileLen, appIndex, blockBytes);

                        // Compute SHA256 over the app block (optional but recommended)
                        if (opt.VerifyBlockSha256)
                            state.Sha256Hex[appIndex] = await Sha256OfSliceAsync(fs.SafeFileHandle, appOffset, appLen, bufferBytes, ct);

                        // Stage all Azure blocks that lie within this app block
                        int firstAzure = (int)(appOffset / azureBytes);
                        int lastAzure = (int)((appOffset + appLen - 1) / azureBytes);

                        for (int az = firstAzure; az <= lastAzure; az++)
                        {
                            long azOffset = (long)az * azureBytes;
                            int azLen = (int)Math.Min(azureBytes, fileLen - azOffset);

                            // Read the Azure block in BUFFER-sized pieces into a single memory stream (or stream directly)
                            var mem = new MemoryStream(capacity: azLen);
                            var buf = ArrayPool<byte>.Shared.Rent(bufferBytes);
                            try
                            {
                                int remaining = azLen;
                                long pos = azOffset;
                                while (remaining > 0)
                                {
                                    int toRead = Math.Min(bufferBytes, remaining);
                                    int read = await RandomAccess.ReadAsync(fs.SafeFileHandle, buf.AsMemory(0, toRead), pos, ct);
                                    if (read == 0) throw new EndOfStreamException();
                                    await mem.WriteAsync(buf.AsMemory(0, read), ct);
                                    pos += read;
                                    remaining -= read;

                                    Interlocked.Add(ref transferred, read);
                                    progress?.Report(Volatile.Read(ref transferred));
                                }
                                mem.Position = 0;

                                // Optional per-StageBlock MD5 (service-side verification)
                                byte[]? md5 = null;
                                if (opt.StageBlockMd5)
                                {
                                    md5 = System.Security.Cryptography.MD5.HashData(mem.GetBuffer().AsSpan(0, azLen));
                                    mem.Position = 0;
                                }

                                await blob.StageBlockAsync(
                                    base64BlockId: azureIds[az],
                                    content: mem,
                                    transactionalContentHash: md5, // pass null if your SDK version requires named args
                                    conditions: null,
                                    progressHandler: null,
                                    cancellationToken: ct);

                                Volatile.Write(ref pub.Staged[az], true);
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(buf);
                                await mem.DisposeAsync();
                            }
                        }

                        state.Completed[appIndex] = true;
                        if (opt.PersistState) SaveState(sidecar, state);
                    }
                    finally { sem.Release(); }
                }, ct));
            }
            await Task.WhenAll(tasks);

            //// Commit the full ordered list of Azure block IDs
            //await blob.CommitBlockListAsync(azureIds, conditions: null, cancellationToken: ct);

            pub.UploadDone = true;              // tell publisher to do a final sweep/commit
            await publisher;                     // wait for it to finish
                                                 // Final publish (ensure full list is visible)
            await blob.CommitBlockListAsync(azureIds, conditions: null, cancellationToken: ct);


            if (opt.PersistState) SaveState(sidecar, state);
        }

        static TransferState LoadOrInitState(string path, string dir, string src, string dst, long len, TransferOptions opt, int blocks)
        {
            if (File.Exists(path))
            {
                try
                {
                    var s = System.Text.Json.JsonSerializer.Deserialize<TransferState>(File.ReadAllText(path))!;
                    if (s.FileLength == len && s.BlockSizeMB == opt.BlockSizeMB && s.BlockCount == blocks) return s;
                }
                catch { }
            }
            return new TransferState
            {
                Direction = dir,
                Source = src,
                Destination = dst,
                FileLength = len,
                BlockSizeMB = opt.BlockSizeMB,
                BufferSizeMB = opt.BufferSizeMB,
                AzureBlockSizeMB = opt.AzureBlockSizeMB,
                BlockCount = blocks,
                Completed = new bool[blocks],
                Sha256Hex = new string[blocks]
            };
        }

        static void SaveState(string path, TransferState s)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        private sealed class PublishState
        {
            // Shared across tasks for THIS transfer only
            public readonly bool[] Staged;         // per Azure block index -> has been staged
            public int LastPublished = -1;         // last committed prefix end (azure index)
            public long LastPublishedBytes = 0;    // bytes exposed so far
            public volatile bool UploadDone;       // signal publisher to finish

            public PublishState(int azureBlockCount)
            {
                Staged = new bool[azureBlockCount];
            }
        }

    }

}
