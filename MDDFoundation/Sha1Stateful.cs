using System;
using System.Collections.Generic;
using System.Text;

namespace MDDFoundation
{
    public sealed class Sha1Stateful
    {
        private readonly byte[] _block = new byte[64];
        private int _blockLen;
        private ulong _totalBytes;

        private uint _h0, _h1, _h2, _h3, _h4;

        // Reuse schedule to avoid per-block allocations
        private readonly uint[] _w = new uint[80];

        public Sha1Stateful() => Reset();

        public void Reset()
        {
            _h0 = 0x67452301u;
            _h1 = 0xEFCDAB89u;
            _h2 = 0x98BADCFEu;
            _h3 = 0x10325476u;
            _h4 = 0xC3D2E1F0u;

            _blockLen = 0;
            _totalBytes = 0;
        }

        public void Update(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();

            _totalBytes += (ulong)count;

            int idx = offset;
            int remaining = count;

            if (_blockLen > 0)
            {
                int toCopy = Math.Min(64 - _blockLen, remaining);
                Buffer.BlockCopy(buffer, idx, _block, _blockLen, toCopy);
                _blockLen += toCopy;
                idx += toCopy;
                remaining -= toCopy;

                if (_blockLen == 64)
                {
                    ProcessBlock(_block, 0);
                    _blockLen = 0;
                }
            }

            while (remaining >= 64)
            {
                ProcessBlock(buffer, idx);
                idx += 64;
                remaining -= 64;
            }

            if (remaining > 0)
            {
                Buffer.BlockCopy(buffer, idx, _block, 0, remaining);
                _blockLen = remaining;
            }
        }

        public byte[] FinalizeHash()
        {
            ulong totalBits = _totalBytes * 8UL;

            _block[_blockLen++] = 0x80;

            if (_blockLen > 56)
            {
                for (int i = _blockLen; i < 64; i++) _block[i] = 0;
                ProcessBlock(_block, 0);
                _blockLen = 0;
            }

            for (int i = _blockLen; i < 56; i++) _block[i] = 0;

            WriteUInt64BE(_block, 56, totalBits);
            ProcessBlock(_block, 0);
            _blockLen = 0;

            byte[] digest = new byte[20];
            WriteUInt32BE(digest, 0, _h0);
            WriteUInt32BE(digest, 4, _h1);
            WriteUInt32BE(digest, 8, _h2);
            WriteUInt32BE(digest, 12, _h3);
            WriteUInt32BE(digest, 16, _h4);
            return digest;
        }

        public byte[] ExportState()
        {
            const int magic = unchecked((int)0x53484131); // "SHA1"
            const int ver = 1;

            byte[] blob = new byte[
                4 + 4 +
                4 * 5 +
                8 +
                4 +
                64];

            int p = 0;
            WriteInt32LE(blob, ref p, magic);
            WriteInt32LE(blob, ref p, ver);

            WriteUInt32LE(blob, ref p, _h0);
            WriteUInt32LE(blob, ref p, _h1);
            WriteUInt32LE(blob, ref p, _h2);
            WriteUInt32LE(blob, ref p, _h3);
            WriteUInt32LE(blob, ref p, _h4);

            WriteUInt64LE(blob, ref p, _totalBytes);
            WriteInt32LE(blob, ref p, _blockLen);

            Buffer.BlockCopy(_block, 0, blob, p, 64);

            return blob;
        }

        public void ImportState(byte[] blob)
        {
            if (blob == null) throw new ArgumentNullException(nameof(blob));
            if (blob.Length != (4 + 4 + 4 * 5 + 8 + 4 + 64))
                throw new IOException($"Invalid SHA1 state blob length: {blob.Length}");

            int p = 0;
            int magic = ReadInt32LE(blob, ref p);
            int ver = ReadInt32LE(blob, ref p);

            if (magic != unchecked((int)0x53484131) || ver != 1)
                throw new IOException("Invalid SHA1 state blob header/version.");

            _h0 = ReadUInt32LE(blob, ref p);
            _h1 = ReadUInt32LE(blob, ref p);
            _h2 = ReadUInt32LE(blob, ref p);
            _h3 = ReadUInt32LE(blob, ref p);
            _h4 = ReadUInt32LE(blob, ref p);

            _totalBytes = ReadUInt64LE(blob, ref p);
            _blockLen = ReadInt32LE(blob, ref p);

            if (_blockLen < 0 || _blockLen > 64)
                throw new IOException("Invalid SHA1 state blob (blockLen out of range).");

            Buffer.BlockCopy(blob, p, _block, 0, 64);
        }

        private void ProcessBlock(byte[] buffer, int offset)
        {
            for (int i = 0; i < 16; i++)
                _w[i] = ReadUInt32BE(buffer, offset + i * 4);

            for (int i = 16; i < 80; i++)
                _w[i] = Rol1(_w[i - 3] ^ _w[i - 8] ^ _w[i - 14] ^ _w[i - 16]);

            uint a = _h0, b = _h1, c = _h2, d = _h3, e = _h4;

            for (int i = 0; i < 80; i++)
            {
                uint f, k;

                if (i < 20)
                {
                    f = (b & c) | (~b & d);
                    k = 0x5A827999u;
                }
                else if (i < 40)
                {
                    f = b ^ c ^ d;
                    k = 0x6ED9EBA1u;
                }
                else if (i < 60)
                {
                    f = (b & c) | (b & d) | (c & d);
                    k = 0x8F1BBCDCu;
                }
                else
                {
                    f = b ^ c ^ d;
                    k = 0xCA62C1D6u;
                }

                uint temp = unchecked(Rol5(a) + f + e + k + _w[i]);
                e = d;
                d = c;
                c = Rol30(b);
                b = a;
                a = temp;
            }

            _h0 = unchecked(_h0 + a);
            _h1 = unchecked(_h1 + b);
            _h2 = unchecked(_h2 + c);
            _h3 = unchecked(_h3 + d);
            _h4 = unchecked(_h4 + e);
        }

        private static uint Rol1(uint x) => (x << 1) | (x >> 31);
        private static uint Rol5(uint x) => (x << 5) | (x >> 27);
        private static uint Rol30(uint x) => (x << 30) | (x >> 2);

        private static uint ReadUInt32BE(byte[] b, int o)
        {
            return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);
        }

        private static void WriteUInt32BE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        private static void WriteUInt64BE(byte[] b, int o, ulong v)
        {
            b[o] = (byte)(v >> 56);
            b[o + 1] = (byte)(v >> 48);
            b[o + 2] = (byte)(v >> 40);
            b[o + 3] = (byte)(v >> 32);
            b[o + 4] = (byte)(v >> 24);
            b[o + 5] = (byte)(v >> 16);
            b[o + 6] = (byte)(v >> 8);
            b[o + 7] = (byte)v;
        }

        private static void WriteInt32LE(byte[] b, ref int p, int v)
        {
            b[p++] = (byte)v;
            b[p++] = (byte)(v >> 8);
            b[p++] = (byte)(v >> 16);
            b[p++] = (byte)(v >> 24);
        }

        private static void WriteUInt32LE(byte[] b, ref int p, uint v)
        {
            b[p++] = (byte)v;
            b[p++] = (byte)(v >> 8);
            b[p++] = (byte)(v >> 16);
            b[p++] = (byte)(v >> 24);
        }

        private static void WriteUInt64LE(byte[] b, ref int p, ulong v)
        {
            b[p++] = (byte)v;
            b[p++] = (byte)(v >> 8);
            b[p++] = (byte)(v >> 16);
            b[p++] = (byte)(v >> 24);
            b[p++] = (byte)(v >> 32);
            b[p++] = (byte)(v >> 40);
            b[p++] = (byte)(v >> 48);
            b[p++] = (byte)(v >> 56);
        }

        private static int ReadInt32LE(byte[] b, ref int p)
        {
            int v = b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24);
            p += 4;
            return v;
        }

        private static uint ReadUInt32LE(byte[] b, ref int p)
        {
            uint v = (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24));
            p += 4;
            return v;
        }

        private static ulong ReadUInt64LE(byte[] b, ref int p)
        {
            ulong v =
                (ulong)b[p] |
                ((ulong)b[p + 1] << 8) |
                ((ulong)b[p + 2] << 16) |
                ((ulong)b[p + 3] << 24) |
                ((ulong)b[p + 4] << 32) |
                ((ulong)b[p + 5] << 40) |
                ((ulong)b[p + 6] << 48) |
                ((ulong)b[p + 7] << 56);
            p += 8;
            return v;
        }
    }
}
