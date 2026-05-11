using System.Net;
using System.Security.Cryptography;
using FileRelay.Core;

namespace FileRelay.Client;

// Streams a byte range from a file directly into an HTTP request body,
// computing SHA-256 incrementally as bytes are sent, then appends the
// raw hash and keyed hash MAC at the end. Single disk read, no per-chunk allocation.
internal sealed class ChunkContent : HttpContent
{
    private readonly string _filePath;
    private readonly long _offset;
    private readonly long _length;
    private readonly Action<long>? _onBytesSent;
    private readonly BandwidthLimiter? _throttle;
    private readonly byte _priority;
    private readonly string? _appId;
    private readonly string? _apiKey;
    private readonly Guid _transferId;
    private readonly int _chunkIndex;
    private readonly int _runIndex;

    public ChunkContent(string filePath, long offset, long length,
        Action<long>? onBytesSent = null, BandwidthLimiter? throttle = null, byte priority = 50,
        string? appId = null, string? apiKey = null, Guid transferId = default,
        int chunkIndex = 0, int runIndex = 1)
    {
        _filePath   = filePath;
        _offset     = offset;
        _length     = length;
        _onBytesSent = onBytesSent;
        _throttle   = throttle;
        _priority   = priority;
        _appId      = appId;
        _apiKey     = apiKey;
        _transferId = transferId;
        _chunkIndex = chunkIndex;
        _runIndex   = runIndex;
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length + 32 + (HasMac ? 32 : 0); // data bytes + SHA-256 hash + optional HMAC
        return true;
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        using var sha = SHA256.Create();
        const int bufSize = 1024 * 1024; // 1MB — fewer async iterations, larger HTTP/2 frames
        using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufSize, FileOptions.Asynchronous);
        fs.Seek(_offset, SeekOrigin.Begin);

        var buf = new byte[bufSize];
        var remaining = _length;
        while (remaining > 0)
        {
            var read = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, remaining)).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Unexpected end of file during chunk upload.");
            sha.TransformBlock(buf, 0, read, null, 0);
            if (_throttle != null)
                await _throttle.AcquireAsync(read, _priority, CancellationToken.None).ConfigureAwait(false);
            await stream.WriteAsync(buf, 0, read).ConfigureAwait(false);
            _onBytesSent?.Invoke(read);
            remaining -= read;
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hash = sha.Hash!;
        await stream.WriteAsync(hash, 0, hash.Length).ConfigureAwait(false);

        if (HasMac)
        {
            var mac = ChunkToken.ComputeHashMac(_apiKey!, _appId!, _transferId, _chunkIndex, _runIndex, _length, hash);
            await stream.WriteAsync(mac, 0, mac.Length).ConfigureAwait(false);
        }
    }

    private bool HasMac
        => _appId is { Length: > 0 }
           && _apiKey is { Length: > 0 }
           && _transferId != default
           && _chunkIndex > 0
           && _runIndex > 0;
}
