namespace G9SyncData.Protocol.RowBatch;

/// <summary>LEB128 unsigned varints and zig-zag encoding.</summary>
internal static class G9Varint
{
    public const int MaxBytes = 10;

    public static int Write(Span<byte> destination, ulong value)
    {
        var index = 0;
        while (value >= 0x80)
        {
            destination[index++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[index++] = (byte)value;
        return index;
    }

    public static ulong Read(ReadOnlySpan<byte> source, ref int position)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (position >= source.Length) throw new FormatException("Truncated varint.");
            var b = source[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 63) throw new FormatException("Varint is too long.");
        }
    }

    public static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

    public static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);
}
