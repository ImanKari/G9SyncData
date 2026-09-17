using System.Buffers;
using System.Buffers.Binary;

namespace G9SyncData.Protocol.RowBatch;

/// <summary>
///     The column-grouped body layout of G9RowBatch version 2 (ADR-0040), converted to and from the row-wise layout of version 1.
/// </summary>
/// <remarks>
///     <para>
///         A version 2 section has the version 1 section header, then the byte length of each of its streams (varints), then the
///         streams. Every stream holds the same field of every row, in row order, encoded exactly as in version 1:
///     </para>
///     <list type="table">
///         <item><term>Upsert, Backfill</term><description>keys · versions · null bitmaps · one stream per column (non-null values only)</description></item>
///         <item><term>Tombstone, MoveOut</term><description>keys · versions</description></item>
///         <item><term>KeyVersions</term><description>keys · versions · row hashes</description></item>
///         <item><term>Push</term><description>keys · push ops · local sequences · base versions · dirty masks · HLC ms · null bitmaps (upserts only) · one stream per column</description></item>
///     </list>
///     <para>
///         Only the order of the bytes changes, so the conversion is exact both ways. It pays off under compression: similar values
///         (GUID references, timestamps, varint versions) stand next to each other instead of being interleaved with every other column,
///         and Brotli finds far more of them in its window (17% fewer bytes on AgriPad pots; 19-Performance).
///     </para>
/// </remarks>
internal static class G9RowBatchColumnLayout
{
    private const int KeyStream = 0;

    /// <summary>Converts a version 1 body to the version 2 layout. The result is rented from <see cref="ArrayPool{T}.Shared" />.</summary>
    public static byte[] FromRows(ReadOnlySpan<byte> rows, out int length)
    {
        var sections = BinaryPrimitives.ReadInt32LittleEndian(rows);
        // The same payload bytes plus one varint length per stream per section (grown on demand).
        var output = ArrayPool<byte>.Shared.Rent(rows.Length + 256);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(output, sections);
            var read = 4;
            var written = 4;
            Span<int> lengths = stackalloc int[StreamCountLimit];
            Span<int> cursors = stackalloc int[StreamCountLimit];
            for (var s = 0; s < sections; s++)
            {
                var header = SectionHeader.Read(rows, ref read);
                var streams = StreamCount(header);
                EnsureOutput(ref output, written, header.Length + (streams * G9Varint.MaxBytes));
                rows.Slice(header.Start, header.Length).CopyTo(output.AsSpan(written));
                written += header.Length;

                // Pass 1: every stream's length.
                lengths[..streams].Clear();
                var rowsStart = read;
                var count = new StreamSink(lengths, default, default);
                for (var row = 0; row < header.RowCount; row++) ScanRow(rows, ref read, header, in count);

                foreach (var streamLength in lengths[..streams]) written += G9Varint.Write(output.AsSpan(written), (ulong)streamLength);

                // Pass 2: copy each field to its stream.
                var total = 0;
                for (var i = 0; i < streams; i++)
                {
                    cursors[i] = written + total;
                    total += lengths[i];
                }

                EnsureOutput(ref output, written, total);
                read = rowsStart;
                var copy = new StreamSink(default, cursors, output);
                for (var row = 0; row < header.RowCount; row++) ScanRow(rows, ref read, header, in copy);
                written += total;
            }

            if (read != rows.Length) throw new FormatException("Row batch body has trailing bytes.");
            length = written;
            return output;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(output);
            throw;
        }
    }

    /// <summary>
    ///     Converts a version 2 body back to the version 1 layout, which is never longer. The result is rented from
    ///     <see cref="ArrayPool{T}.Shared" />.
    /// </summary>
    /// <exception cref="FormatException">The body is malformed: a stream is too short or has bytes no row used.</exception>
    public static byte[] ToRows(ReadOnlySpan<byte> columns, out int length)
    {
        if (columns.Length < 4) throw new FormatException("Row batch body is too short.");
        var sections = BinaryPrimitives.ReadInt32LittleEndian(columns);
        if (sections < 0) throw new FormatException("Negative section count.");
        var output = ArrayPool<byte>.Shared.Rent(columns.Length);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(output, sections);
            var read = 4;
            var written = 4;
            Span<int> starts = stackalloc int[StreamCountLimit];
            Span<int> ends = stackalloc int[StreamCountLimit];
            for (var s = 0; s < sections; s++)
            {
                var header = SectionHeader.Read(columns, ref read);
                columns.Slice(header.Start, header.Length).CopyTo(output.AsSpan(written));
                written += header.Length;

                var streams = StreamCount(header);
                var offset = 0L;
                for (var i = 0; i < streams; i++)
                {
                    var streamLength = G9Varint.Read(columns, ref read);
                    starts[i] = checked((int)offset);
                    offset += (long)streamLength;
                    if (offset > columns.Length) throw new FormatException("Row batch stream is truncated.");
                    ends[i] = (int)offset;
                }

                if (read + offset > columns.Length) throw new FormatException("Row batch stream is truncated.");
                for (var i = 0; i < streams; i++)
                {
                    starts[i] += read;
                    ends[i] += read;
                }

                for (var row = 0; row < header.RowCount; row++) CopyRow(columns, starts, ends, header, output, ref written);
                for (var i = 0; i < streams; i++)
                {
                    if (starts[i] != ends[i]) throw new FormatException("Row batch stream has bytes no row used.");
                }

                read += (int)offset;
            }

            if (read != columns.Length) throw new FormatException("Row batch body has trailing bytes.");
            length = written;
            return output;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(output);
            throw;
        }
    }

    // ---------------------------------------------------------------- layout

    /// <summary>The largest stream count of any section: push rows (7) plus the column limit.</summary>
    private const int StreamCountLimit = 7 + 1024;

    private static int StreamCount(in SectionHeader header) => header.Op switch
    {
        G9ERowBatchOp.Upsert or G9ERowBatchOp.Backfill => 3 + header.Types.Length,
        G9ERowBatchOp.Tombstone or G9ERowBatchOp.MoveOut => 2,
        G9ERowBatchOp.KeyVersions => 3,
        G9ERowBatchOp.Push => 7 + header.Types.Length,
        _ => throw new FormatException($"Unknown op {(byte)header.Op}.")
    };

    /// <summary>Walks one row of a version 1 body and hands each field to its stream.</summary>
    private static void ScanRow(ReadOnlySpan<byte> rows, ref int position, in SectionHeader header, in StreamSink sink)
    {
        sink.Put(KeyStream, rows, ref position, KeyLength(rows, position, header.KeyType));
        switch (header.Op)
        {
            case G9ERowBatchOp.Upsert or G9ERowBatchOp.Backfill:
                sink.Put(1, rows, ref position, VarintLength(rows, position));
                ScanValues(rows, ref position, header, 2, in sink);
                break;
            case G9ERowBatchOp.Tombstone or G9ERowBatchOp.MoveOut:
                sink.Put(1, rows, ref position, VarintLength(rows, position));
                break;
            case G9ERowBatchOp.KeyVersions:
                sink.Put(1, rows, ref position, VarintLength(rows, position));
                sink.Put(2, rows, ref position, 8);
                break;
            case G9ERowBatchOp.Push:
                var op = Available(rows, position, 1)[0];
                sink.Put(1, rows, ref position, 1);
                sink.Put(2, rows, ref position, VarintLength(rows, position));
                sink.Put(3, rows, ref position, VarintLength(rows, position));
                sink.Put(4, rows, ref position, 8);
                sink.Put(5, rows, ref position, VarintLength(rows, position));
                if (op == (byte)G9EPushRowOp.Upsert) ScanValues(rows, ref position, header, 6, in sink);
                else if (op != (byte)G9EPushRowOp.Delete) throw new FormatException($"Unknown push op {op}.");
                break;
        }
    }

    private static void ScanValues(ReadOnlySpan<byte> rows, ref int position, in SectionHeader header, int bitmapStream, in StreamSink sink)
    {
        var bitmapBytes = (header.Types.Length + 7) >> 3;
        var bitmap = position;
        sink.Put(bitmapStream, rows, ref position, bitmapBytes);
        for (var column = 0; column < header.Types.Length; column++)
        {
            if ((rows[bitmap + (column >> 3)] & (1 << (column & 7))) != 0) continue;
            sink.Put(bitmapStream + 1 + column, rows, ref position, ValueLength(rows, position, header.Types[column]));
        }
    }

    /// <summary>Rebuilds one version 1 row from the streams' cursors.</summary>
    private static void CopyRow(ReadOnlySpan<byte> columns, Span<int> cursors, ReadOnlySpan<int> ends, in SectionHeader header, byte[] output, ref int written)
    {
        Take(columns, cursors, ends, KeyStream, KeyLength(columns, cursors[KeyStream], header.KeyType), output, ref written);
        switch (header.Op)
        {
            case G9ERowBatchOp.Upsert or G9ERowBatchOp.Backfill:
                Take(columns, cursors, ends, 1, VarintLength(columns, cursors[1]), output, ref written);
                CopyValues(columns, cursors, ends, header, 2, output, ref written);
                break;
            case G9ERowBatchOp.Tombstone or G9ERowBatchOp.MoveOut:
                Take(columns, cursors, ends, 1, VarintLength(columns, cursors[1]), output, ref written);
                break;
            case G9ERowBatchOp.KeyVersions:
                Take(columns, cursors, ends, 1, VarintLength(columns, cursors[1]), output, ref written);
                Take(columns, cursors, ends, 2, 8, output, ref written);
                break;
            case G9ERowBatchOp.Push:
                var op = cursors[1] < ends[1] ? columns[cursors[1]] : throw new FormatException("Row batch stream is truncated.");
                Take(columns, cursors, ends, 1, 1, output, ref written);
                Take(columns, cursors, ends, 2, VarintLength(columns, cursors[2]), output, ref written);
                Take(columns, cursors, ends, 3, VarintLength(columns, cursors[3]), output, ref written);
                Take(columns, cursors, ends, 4, 8, output, ref written);
                Take(columns, cursors, ends, 5, VarintLength(columns, cursors[5]), output, ref written);
                if (op == (byte)G9EPushRowOp.Upsert) CopyValues(columns, cursors, ends, header, 6, output, ref written);
                else if (op != (byte)G9EPushRowOp.Delete) throw new FormatException($"Unknown push op {op}.");
                break;
        }
    }

    private static void CopyValues(ReadOnlySpan<byte> columns, Span<int> cursors, ReadOnlySpan<int> ends, in SectionHeader header, int bitmapStream,
        byte[] output, ref int written)
    {
        var bitmapBytes = (header.Types.Length + 7) >> 3;
        var bitmap = written;
        Take(columns, cursors, ends, bitmapStream, bitmapBytes, output, ref written);
        for (var column = 0; column < header.Types.Length; column++)
        {
            if ((output[bitmap + (column >> 3)] & (1 << (column & 7))) != 0) continue;
            var stream = bitmapStream + 1 + column;
            Take(columns, cursors, ends, stream, ValueLength(columns, cursors[stream], header.Types[column]), output, ref written);
        }
    }

    private static void Take(ReadOnlySpan<byte> columns, Span<int> cursors, ReadOnlySpan<int> ends, int stream, int length, byte[] output, ref int written)
    {
        if (length < 0 || cursors[stream] + length > ends[stream]) throw new FormatException("Row batch stream is truncated.");
        if (written + length > output.Length) throw new FormatException("Row batch body is inconsistent.");
        columns.Slice(cursors[stream], length).CopyTo(output.AsSpan(written));
        cursors[stream] += length;
        written += length;
    }

    // ---------------------------------------------------------------- field lengths (encodings of 04-Protocol)

    private static int KeyLength(ReadOnlySpan<byte> body, int position, G9ESyncColumnType keyType) => keyType switch
    {
        G9ESyncColumnType.Guid => 16,
        G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 => VarintLength(body, position),
        _ => PrefixedLength(body, position)
    };

    private static int ValueLength(ReadOnlySpan<byte> body, int position, G9ESyncColumnType type) => type switch
    {
        G9ESyncColumnType.Boolean => 1,
        G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 or G9ESyncColumnType.TimeSpan => VarintLength(body, position),
        G9ESyncColumnType.Double => 8,
        G9ESyncColumnType.Decimal or G9ESyncColumnType.Guid => 16,
        G9ESyncColumnType.DateTime => 9,
        G9ESyncColumnType.DateTimeOffset => 10,
        G9ESyncColumnType.String or G9ESyncColumnType.Bytes => PrefixedLength(body, position),
        _ => throw new FormatException($"Unknown column type {(byte)type}.")
    };

    private static int VarintLength(ReadOnlySpan<byte> body, int position)
    {
        var start = position;
        G9Varint.Read(body, ref position);
        return position - start;
    }

    private static int PrefixedLength(ReadOnlySpan<byte> body, int position)
    {
        var start = position;
        var length = G9Varint.Read(body, ref position);
        if (length > int.MaxValue) throw new FormatException("Row batch value is too long.");
        return checked((position - start) + (int)length);
    }

    private static ReadOnlySpan<byte> Available(ReadOnlySpan<byte> body, int position, int length) =>
        length >= 0 && position + length <= body.Length ? body.Slice(position, length) : throw new FormatException("Row batch is truncated.");

    private static void EnsureOutput(ref byte[] output, int written, int additional)
    {
        var required = (long)written + additional;
        if (required <= output.Length) return;
        if (required > G9RowBatchFormat.MaxBodyBytes + (64L * 1024 * 1024)) throw new InvalidOperationException("Row batch exceeds the maximum body size.");
        var next = ArrayPool<byte>.Shared.Rent((int)Math.Max(required, (long)output.Length * 2));
        output.AsSpan(0, written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(output);
        output = next;
    }

    /// <summary>Counts stream lengths (pass 1) or copies fields to their stream cursors (pass 2).</summary>
    private readonly ref struct StreamSink(Span<int> lengths, Span<int> cursors, Span<byte> output)
    {
        private readonly Span<int> _lengths = lengths;
        private readonly Span<int> _cursors = cursors;
        private readonly Span<byte> _output = output;

        public void Put(int stream, ReadOnlySpan<byte> body, ref int position, int length)
        {
            var field = Available(body, position, length);
            if (_lengths.Length > 0)
            {
                _lengths[stream] += length;
            }
            else
            {
                field.CopyTo(_output[_cursors[stream]..]);
                _cursors[stream] += length;
            }

            position += length;
        }
    }

    /// <summary>A section header, identical in both layouts: <c>op u8 | table id varint | key type u8 | column count varint | types | row count i32</c>.</summary>
    private readonly ref struct SectionHeader
    {
        public int Start { get; private init; }
        public int Length { get; private init; }
        public G9ERowBatchOp Op { get; private init; }
        public G9ESyncColumnType KeyType { get; private init; }
        public ReadOnlySpan<byte> RawTypes { get; private init; }
        public ReadOnlySpan<G9ESyncColumnType> Types => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, G9ESyncColumnType>(RawTypes);
        public int RowCount { get; private init; }

        public static SectionHeader Read(ReadOnlySpan<byte> body, ref int position)
        {
            var start = position;
            var op = (G9ERowBatchOp)Available(body, position, 1)[0];
            position++;
            if (!Enum.IsDefined(op)) throw new FormatException($"Unknown op {(byte)op}.");
            G9Varint.Read(body, ref position);
            var keyType = (G9ESyncColumnType)Available(body, position, 1)[0];
            position++;
            if (!G9CSyncKey.IsSupportedKeyType(keyType)) throw new FormatException($"Unsupported key type {keyType}.");
            var columnCount = G9Varint.Read(body, ref position);
            if (columnCount > 1024) throw new FormatException("Too many columns.");
            var types = Available(body, position, (int)columnCount);
            position += (int)columnCount;
            var rowCount = BinaryPrimitives.ReadInt32LittleEndian(Available(body, position, 4));
            position += 4;
            if (rowCount < 0) throw new FormatException("Negative row count.");
            return new SectionHeader { Start = start, Length = position - start, Op = op, KeyType = keyType, RawTypes = types, RowCount = rowCount };
        }
    }
}
