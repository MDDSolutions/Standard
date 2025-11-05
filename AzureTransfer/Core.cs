using Microsoft.Win32.SafeHandles;
using System.Buffers;

namespace AzureTransfer
{
    public sealed class TransferOptions
    {
        // Work partitioning
        public int DegreeOfParallelism { get; set; } = Environment.ProcessorCount;
        public int BlockSizeMB { get; set; } = 512;       // “unit of work” (hundreds of MB)
        public int BufferSizeMB { get; set; } = 4;        // per read/write syscall

        // Upload-only (Azure Block Blob staging)
        public int AzureBlockSizeMB { get; set; } = 16;   // size of each StageBlock
        public bool StageBlockMd5 { get; set; } = false;  // service verifies per-StageBlock MD5

        // Integrity
        public bool VerifyBlockSha256 { get; set; } = true; // verify after download / before commit
        public bool PersistState { get; set; } = true;      // write .xfer.json to support resume
    }
    public sealed class TransferState
    {
        public string Version { get; init; } = "1";
        public string Direction { get; init; } = "download"; // or "upload"
        public string Source { get; init; } = "";            // blob URL or path
        public string Destination { get; init; } = "";
        public long FileLength { get; init; }
        public int BlockSizeMB { get; init; }
        public int BufferSizeMB { get; init; }
        public int AzureBlockSizeMB { get; init; }           // upload
        public int BlockCount { get; init; }
        public bool[] Completed { get; init; } = Array.Empty<bool>();
        public string[] Sha256Hex { get; init; } = Array.Empty<string>();
    }
    public static class Helpers
    {
        public static (long Offset, int Length) GetBlockRange(long fileLen, int blockIndex, int blockSizeBytes)
        {
            long offset = (long)blockIndex * blockSizeBytes;
            int length = (int)Math.Min(blockSizeBytes, fileLen - offset);
            return (offset, length);
        }

        public static string Sha256Hex(ReadOnlySpan<byte> data)
        {
            Span<byte> hash = stackalloc byte[32];
            System.Security.Cryptography.SHA256.HashData(data, hash);
            return Convert.ToHexString(hash);
        }

        // Incremental SHA256 over a file slice using a reusable buffer
        public static async Task<string> Sha256OfSliceAsync(SafeFileHandle handle, long offset, int length, int bufferBytes, CancellationToken ct)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var buf = ArrayPool<byte>.Shared.Rent(bufferBytes);
            try
            {
                int remaining = length;
                long pos = offset;
                while (remaining > 0)
                {
                    int toRead = Math.Min(bufferBytes, remaining);
                    int read = await RandomAccess.ReadAsync(handle, buf.AsMemory(0, toRead), pos, ct);
                    if (read == 0) throw new EndOfStreamException();
                    sha.TransformBlock(buf, 0, read, null, 0);
                    pos += read;
                    remaining -= read;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Convert.ToHexString(sha.Hash!);
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
    }
}
