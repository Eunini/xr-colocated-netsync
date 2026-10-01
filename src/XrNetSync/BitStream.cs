using System;
using System.Collections.Generic;

namespace XrNetSync
{
    public sealed class BitWriter
    {
        readonly List<byte> bytes = new List<byte>(64);
        ulong scratch;
        int scratchBits;

        public int BitCount => bytes.Count * 8 + scratchBits;

        public void Write(uint value, int bits)
        {
            if (bits < 1 || bits > 32) throw new ArgumentOutOfRangeException(nameof(bits));
            ulong mask = bits == 32 ? 0xFFFFFFFFul : (1ul << bits) - 1;
            scratch |= (value & mask) << scratchBits;
            scratchBits += bits;
            while (scratchBits >= 8)
            {
                bytes.Add((byte)scratch);
                scratch >>= 8;
                scratchBits -= 8;
            }
        }

        public void WriteBool(bool v) => Write(v ? 1u : 0u, 1);

        public byte[] ToArray()
        {
            var result = new byte[(BitCount + 7) / 8];
            bytes.CopyTo(result);
            if (scratchBits > 0) result[bytes.Count] = (byte)scratch;
            return result;
        }
    }

    public sealed class BitReader
    {
        readonly byte[] data;
        int bitPos;

        public BitReader(byte[] data) { this.data = data; }

        public int BitsRemaining => data.Length * 8 - bitPos;

        public uint Read(int bits)
        {
            if (bits < 1 || bits > 32) throw new ArgumentOutOfRangeException(nameof(bits));
            if (bits > BitsRemaining) throw new InvalidOperationException("Read past end of packet");
            ulong v = 0;
            for (int i = 0; i < bits; )
            {
                int byteIndex = bitPos >> 3, bitInByte = bitPos & 7;
                int take = Math.Min(8 - bitInByte, bits - i);
                ulong chunk = (ulong)(data[byteIndex] >> bitInByte) & ((1ul << take) - 1);
                v |= chunk << i;
                i += take;
                bitPos += take;
            }
            return (uint)v;
        }

        public bool ReadBool() => Read(1) != 0;
    }
}
