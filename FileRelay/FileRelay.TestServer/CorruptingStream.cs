// Wraps a request body stream and flips one bit in the first byte of the first read.
// Used by fault injection to trigger a genuine server-side hash mismatch (rather than
// short-circuiting before the server sees the request), so that TryClaimChunkAsync runs
// and the run-index increment path is exercised on the client retry.
public sealed class CorruptingStream(Stream inner) : Stream
{
    private bool _corrupted;

    public override bool CanRead  => true;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        MaybeCorrupt(buffer.AsSpan(offset, read));
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var read = await inner.ReadAsync(buffer, offset, count, ct);
        MaybeCorrupt(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var read = await inner.ReadAsync(buffer, ct);
        MaybeCorrupt(buffer.Span[..read]);
        return read;
    }

    private void MaybeCorrupt(Span<byte> span)
    {
        if (!_corrupted && span.Length > 0)
        {
            span[0] ^= 0x01;
            _corrupted = true;
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value)                => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // Do not dispose inner — ASP.NET Core owns the request body lifetime.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}
