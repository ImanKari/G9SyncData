using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace G9SyncData;

/// <summary>
///     A one-shot, allocation-free MD5 (RFC 1321) for the reconciliation hash's short inputs (ADR-0012). On Android the platform
///     <see cref="System.Security.Cryptography.MD5" /> goes through JNI into Java's MessageDigest: every call left Java objects behind and
///     cost microseconds, which a million-row digest multiplied into seconds and a doubled RSS. MD5 here is a bucketing function, not a
///     security boundary.
/// </summary>
internal static class G9CMd5
{
    private static readonly uint[] K =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391
    ];

    private static readonly int[] S =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21
    ];

    /// <summary>Writes the 16-byte MD5 of <paramref name="source" /> to <paramref name="destination" />.</summary>
    public static void HashData(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < 16) throw new ArgumentException("MD5 needs 16 bytes.", nameof(destination));
        uint a0 = 0x67452301, b0 = 0xefcdab89, c0 = 0x98badcfe, d0 = 0x10325476;

        var full = source.Length / 64 * 64;
        for (var offset = 0; offset < full; offset += 64) Block(source.Slice(offset, 64), ref a0, ref b0, ref c0, ref d0);

        // The tail, the 0x80 marker and the bit length: one or two final blocks.
        Span<byte> tail = stackalloc byte[128];
        tail.Clear();
        var remaining = source.Length - full;
        source[full..].CopyTo(tail);
        tail[remaining] = 0x80;
        var tailBlocks = remaining < 56 ? 64 : 128;
        BinaryPrimitives.WriteUInt64LittleEndian(tail.Slice(tailBlocks - 8, 8), (ulong)source.Length * 8);
        for (var offset = 0; offset < tailBlocks; offset += 64) Block(tail.Slice(offset, 64), ref a0, ref b0, ref c0, ref d0);

        BinaryPrimitives.WriteUInt32LittleEndian(destination, a0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], b0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], c0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], d0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Block(ReadOnlySpan<byte> block, ref uint a0, ref uint b0, ref uint c0, ref uint d0)
    {
        Span<uint> m = stackalloc uint[16];
        for (var i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));

        uint a = a0, b = b0, c = c0, d = d0;
        for (var i = 0; i < 64; i++)
        {
            uint f;
            int g;
            switch (i >> 4)
            {
                case 0:
                    f = (b & c) | (~b & d);
                    g = i;
                    break;
                case 1:
                    f = (d & b) | (~d & c);
                    g = ((5 * i) + 1) & 15;
                    break;
                case 2:
                    f = b ^ c ^ d;
                    g = ((3 * i) + 5) & 15;
                    break;
                default:
                    f = c ^ (b | ~d);
                    g = (7 * i) & 15;
                    break;
            }

            var next = d;
            d = c;
            c = b;
            b += BitOperations.RotateLeft(a + f + K[i] + m[g], S[i]);
            a = next;
        }

        a0 += a;
        b0 += b;
        c0 += c;
        d0 += d;
    }
}
