using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MDDFoundation
{
    internal sealed class ThrottledHashingWriteStream : Stream
    {
        private readonly Stream _base;
        private readonly long _start;
        private readonly long _endExclusive;
        private long _pos;

        private readonly SHA256 _sha;
        private bool _finalized;
        private byte[] _hash;

        private readonly bool _throttle;
        private readonly double _bytesPerSecond;
        private long _bytesThisWindow;
        private Stopwatch _window;

        private readonly FileCopyProgress _progress;

        public ThrottledHashingWriteStream(Stream baseStream, long offset, long length, double maxMbPerSec, FileCopyProgress progress = null)
        {
            if (baseStream == null) throw new ArgumentNullException("baseStream");
            _base = baseStream;
            _start = offset;
            _endExclusive = checked(offset + length);
            _pos = offset;

            _sha = SHA256.Create();

            _throttle = maxMbPerSec > 0;
            _bytesPerSecond = maxMbPerSec * 1024d * 1024d;
            if (_throttle)
            {
                _window = Stopwatch.StartNew();
                _bytesThisWindow = 0;
            }

            if (progress != null && !progress.HasIntegratedCallback) throw new ArgumentException("if specifying progress object, it must have Integrated Callback");
            _progress = progress;
            if (_base.CanSeek && _base.Position != _pos) _base.Seek(_pos, SeekOrigin.Begin);
        }

        private void EnsureFinalized()
        {
            if (_finalized) return;
            _sha.TransformFinalBlock(new byte[0], 0, 0);
            _hash = _sha.Hash;
            _finalized = true;
        }

        public string FinalizeHashAndGetHex()
        {
            if (!_finalized) EnsureFinalized();
            var h = _hash ?? new byte[0];
            var sb = new StringBuilder(h.Length * 2);
            for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("X2"));
            return sb.ToString();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || (offset + count) > buffer.Length) throw new ArgumentOutOfRangeException("count");
            if (count == 0) return;

            long remaining = _endExclusive - _pos;
            if (remaining <= 0) throw new IOException("Attempt to write beyond chunk bounds");
            if (count > remaining) count = (int)remaining;

            if (_base.CanSeek && _base.Position != _pos) _base.Seek(_pos, SeekOrigin.Begin);

            _sha.TransformBlock(buffer, offset, count, null, 0);
            _base.Write(buffer, offset, count);

            _pos += count;

            if (_progress != null) _progress.UpdateAndMaybeCallback(count);

            if (_throttle)
            {
                _bytesThisWindow += count;

                long elapsedMs = _window.ElapsedMilliseconds;
                if (elapsedMs <= 0) elapsedMs = 1;

                double allowedSoFar = _bytesPerSecond * (elapsedMs / 1000.0);
                if (_bytesThisWindow > allowedSoFar)
                {
                    double overBytes = _bytesThisWindow - allowedSoFar;
                    int msToWait = (int)Math.Ceiling((overBytes / _bytesPerSecond) * 1000.0);
                    if (msToWait > 0) Thread.Sleep(msToWait);
                }

                if (_window.ElapsedMilliseconds >= 1000)
                {
                    _window.Restart();
                    _bytesThisWindow = 0;
                }
            }
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || (offset + count) > buffer.Length) throw new ArgumentOutOfRangeException("count");
            if (count == 0) return;

            long remaining = _endExclusive - _pos;
            if (remaining <= 0) throw new IOException("Attempt to write beyond chunk bounds");
            if (count > remaining) count = (int)remaining;

            if (_base.CanSeek && _base.Position != _pos) _base.Seek(_pos, SeekOrigin.Begin);

            _sha.TransformBlock(buffer, offset, count, null, 0);
            await _base.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);

            _pos += count;

            if (_progress != null) _progress.UpdateAndMaybeCallback(count);

            if (_throttle)
            {
                _bytesThisWindow += count;

                long elapsedMs = _window.ElapsedMilliseconds;
                if (elapsedMs <= 0) elapsedMs = 1;

                double allowedSoFar = _bytesPerSecond * (elapsedMs / 1000.0);
                if (_bytesThisWindow > allowedSoFar)
                {
                    double overBytes = _bytesThisWindow - allowedSoFar;
                    int msToWait = (int)Math.Ceiling((overBytes / _bytesPerSecond) * 1000.0);
                    if (msToWait > 0) await Task.Delay(msToWait, cancellationToken).ConfigureAwait(false);
                }

                if (_window.ElapsedMilliseconds >= 1000)
                {
                    _window.Restart();
                    _bytesThisWindow = 0;
                }
            }
        }

        // Stream boilerplate
        public override bool CanRead { get { return false; } }
        public override bool CanSeek { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { return _endExclusive - _start; } }

        public override long Position
        {
            get { return _pos - _start; }
            set
            {
                long absolute = _start + value;
                if (absolute < _start || absolute > _endExclusive) throw new ArgumentOutOfRangeException("value");
                _pos = absolute;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target;
            if (origin == SeekOrigin.Begin) target = _start + offset;
            else if (origin == SeekOrigin.Current) target = _pos + offset;
            else target = _endExclusive + offset;

            if (target < _start || target > _endExclusive) throw new IOException("Seek outside chunk bounds");
            _pos = target;
            return _pos - _start;
        }

        public override void Flush() { _base.Flush(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { EnsureFinalized(); } catch { }
                _sha.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
