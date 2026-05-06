using System.Net;
using System.Security.Cryptography;

namespace FileRelay.Client;

// Streams a byte range from a file directly into an HTTP request body,
// computing SHA-256 incrementally as bytes are sent, then appends the
// 32 raw hash bytes at the end. Single disk read, no per-chunk allocation.
internal sealed class ChunkContent : HttpContent
{
    private readonly string _filePath;
    private readonly long _offset;
    private readonly long _length;
    private readonly Action<long>? _onBytesSent;

    public ChunkContent(string filePath, long offset, long length, Action<long>? onBytesSent = null)
    {
        _filePath = filePath;
        _offset = offset;
        _length = length;
        _onBytesSent = onBytesSent;
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length + 32; // data bytes + SHA-256 hash bytes
        return true;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        fs.Seek(_offset, SeekOrigin.Begin);

        var buf = new byte[81920];
        var remaining = _length;
        while (remaining > 0)
        {
            var read = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, remaining), ct);
            if (read == 0) throw new EndOfStreamException("Unexpected end of file during chunk upload.");
            sha.TransformBlock(buf, 0, read, null, 0);
            await stream.WriteAsync(buf.AsMemory(0, read), ct);
            _onBytesSent?.Invoke(read);
            remaining -= read;
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        await stream.WriteAsync(sha.Hash!.AsMemory(), ct);
    }
}
