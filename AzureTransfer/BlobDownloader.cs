using Azure;
using Azure.Storage.Blobs.Specialized;
using MDDFoundation;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static AzureTransfer.Helpers;
namespace AzureTransfer
{
    public static class BlobDownloader
    {
        public static async Task DownloadAsync(
            BlockBlobClient blob,
            string destinationPath,
            TransferOptions opt,
            IProgress<long>? progress = null,
            CancellationToken ct = default)
        {
            if (opt.BlockSizeMB <= 0 || opt.BufferSizeMB <= 0) throw new ArgumentOutOfRangeException();
            int blockBytes = checked(opt.BlockSizeMB * 1024 * 1024);
            int bufferBytes = checked(opt.BufferSizeMB * 1024 * 1024);

            var props = await blob.GetPropertiesAsync(cancellationToken: ct);
            long length = props.Value.ContentLength;
            if (length == 0) { using var f = new FileStream(destinationPath, FileMode.Create); return; }

            long blockCountLong = (length + blockBytes - 1) / blockBytes;
            int blockCount = checked((int)blockCountLong);

            // Load or initialize state
            string sidecar = destinationPath + ".xfer.json";
            var state = LoadOrInitState(sidecar, "download", blob.Uri.ToString(), destinationPath, length, opt, blockCount);

            string temp = destinationPath + ".partial";
            using (var fs = new FileStream(temp, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                                          bufferSize: 1024 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (fs.Length != length) fs.SetLength(length);

                long transferred = 0;
                var sem = new SemaphoreSlim(opt.DegreeOfParallelism, opt.DegreeOfParallelism);
                var tasks = new List<Task>();

                for (int bi = 0; bi < blockCount; bi++)
                {
                    if (state.Completed[bi])
                    { // resume skip, but advance progress
                        transferred += GetBlockRange(length, bi, blockBytes).Length;
                        progress?.Report(transferred);
                        continue;
                    }

                    await sem.WaitAsync(ct);
                    int blockIndex = bi;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            long thistransferred = 0;
                            int lastprogress = 0;

                            var (offset, size) = GetBlockRange(length, blockIndex, blockBytes);
                            var range = new HttpRange(offset, size);

                            // Single HTTP request per BLOCK; stream into file in BUFFER-sized writes
                            var resp = await blob.DownloadAsync(range: range, conditions: null, rangeGetContentHash: false, cancellationToken: ct);
                            using var net = resp.Value.Content;

                            var buf = ArrayPool<byte>.Shared.Rent(bufferBytes);
                            try
                            {
                                long writeOffset = offset;
                                int remaining = size;
                                while (remaining > 0)
                                {
                                    int toFill = Math.Min(bufferBytes, remaining);
                                    int filled = 0;
                                    while (filled < toFill)
                                    {
                                        int n = await net.ReadAsync(buf, filled, toFill - filled, ct);
                                        if (n == 0) throw new EndOfStreamException($"EOF @ {writeOffset}+{filled}/{toFill}");
                                        filled += n;
                                    }

                                    await RandomAccess.WriteAsync(fs.SafeFileHandle, buf.AsMemory(0, filled), writeOffset, ct);
                                    writeOffset += filled;
                                    thistransferred += filled;

                                    if (Environment.TickCount - lastprogress > 500)
                                    {
                                        long total = Interlocked.Add(ref transferred, thistransferred);
                                        progress?.Report(total);
                                        thistransferred = 0;
                                        lastprogress = Environment.TickCount;
                                    }

                                    progress?.Report(Volatile.Read(ref transferred));
                                    remaining -= filled;
                                }
                            }
                            finally { ArrayPool<byte>.Shared.Return(buf); }

                            // Verify block SHA-256 if requested
                            if (opt.VerifyBlockSha256)
                            {
                                string sha = await Sha256OfSliceAsync(fs.SafeFileHandle, offset, size, bufferBytes, ct);
                                state.Sha256Hex[blockIndex] = sha;
                            }

                            state.Completed[blockIndex] = true;
                            if (opt.PersistState) SaveState(sidecar, state);
                        }
                        finally { sem.Release(); }
                    }, ct));
                }

                await Task.WhenAll(tasks);
                await fs.FlushAsync(ct);
            }
            // Finalize atomically
            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            await Foundation.RetryAsync(() => !File.Exists(destinationPath), 10, 100);
            File.Move(temp, destinationPath);
            var fi = new FileInfo(destinationPath);
            await Foundation.RetryAsync(() => fi.Length == length, 10, 100);
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
                catch { /* fall through to new */ }
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
    }

}
