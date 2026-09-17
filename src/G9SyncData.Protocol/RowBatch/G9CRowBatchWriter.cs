using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace G9SyncData.Protocol.RowBatch;

/// <summary>
///     Writes a G9RowBatch frame: a 10-byte header followed by a (usually Brotli-compressed) body of sections.
///     Each section holds rows of one table and one <see cref="G9ERowBatchOp" />.
/// </summary>
/// <remarks>
///     <para>Frame header: <c>'G' '9' 'R' 'B' | version u8 | compression u8 | body length i32 LE</c>.</para>
///     <para>Body: <c>section count i32 LE</c>, then per section
///     <c>op u8 | table id varint | key type u8 | column count varint | column types u8[] | row count i32 LE | rows</c>.</para>
///     <para>Row layouts and value encodings are specified in <c>AiGuides/04-Protocol.md</c>.</para>
///     The writer is single-threaded and pooled; dispose it (or reuse it with <see cref="Reset" />).
/// </remarks>
public sealed class G9CRowBatchWriter : IDisposable
{
    private byte[] _buffer;
    private int _length;
    private int _sectionCount;
    private int _totalRows;

    private bool _inSection;
    private G9ERowBatchOp _op;
    private G9ESyncColumnType _keyType;
    private G9ESyncColumnType[] _types = [];
    private int _rowCountPosition;
    private int _sectionRows;

    private bool _inRow;
    private bool _rowHasValues;
    private int _column;
    private int _bitmapPosition;
    private bool _disposed;

    public G9CRowBatchWriter(int initialCapacity = 64 * 1024)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 256));
        _length = 4;
    }

    /// <summary>Uncompressed body length written so far (a good estimate of the frame's size before compression).</summary>
    public int Length => _length;

    public int TotalRows => _totalRows;
    public int SectionCount => _sectionCount;
    public int CurrentSectionRows => _sectionRows;
    public bool InSection => _inSection;

    /// <summary>Starts a section. Key-only ops (<see cref="G9ERowBatchOp.Tombstone" />, <see cref="G9ERowBatchOp.MoveOut" />,
    /// <see cref="G9ERowBatchOp.KeyVersions" />) take no column types.</summary>
    public void BeginSection(G9ERowBatchOp op, int tableId, G9ESyncColumnType keyType, ReadOnlySpan<G9ESyncColumnType> columnTypes)
    {
        ThrowIfDisposed();
        if (_inSection) throw new InvalidOperationException("EndSection must be called before BeginSection.");
        if (!Enum.IsDefined(op)) throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown row batch op.");
        if (!G9CSyncKey.IsSupportedKeyType(keyType)) throw new ArgumentOutOfRangeException(nameof(keyType), keyType, "Unsupported key type.");
        ArgumentOutOfRangeException.ThrowIfNegative(tableId);
        if (op is G9ERowBatchOp.Tombstone or G9ERowBatchOp.MoveOut or G9ERowBatchOp.KeyVersions && columnTypes.Length > 0)
        {
            throw new ArgumentException("Key-only sections carry no columns.", nameof(columnTypes));
        }

        if (columnTypes.Length > 1024) throw new ArgumentException("Too many columns.", nameof(columnTypes));
        foreach (var type in columnTypes)
        {
            if (type == G9ESyncColumnType.Unspecified || !Enum.IsDefined(type))
            {
                throw new ArgumentException($"Invalid column type '{type}'.", nameof(columnTypes));
            }
        }

        EnsureCapacity(2 + (G9Varint.MaxBytes * 2) + columnTypes.Length + 4);
        _buffer[_length++] = (byte)op;
        _length += G9Varint.Write(_buffer.AsSpan(_length), (ulong)tableId);
        _buffer[_length++] = (byte)keyType;
        _length += G9Varint.Write(_buffer.AsSpan(_length), (ulong)columnTypes.Length);
        foreach (var type in columnTypes) _buffer[_length++] = (byte)type;
        _rowCountPosition = _length;
        _length += 4;

        _op = op;
        _keyType = keyType;
        _types = columnTypes.ToArray();
        _sectionRows = 0;
        _inSection = true;
    }

    public void EndSection()
    {
        ThrowIfDisposed();
        if (!_inSection) throw new InvalidOperationException("No section is open.");
        if (_inRow) throw new InvalidOperationException("EndRow must be called before EndSection.");
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_rowCountPosition, 4), _sectionRows);
        _sectionCount++;
        _inSection = false;
    }

    // ---------------------------------------------------------------- rows

    /// <summary>Starts an <see cref="G9ERowBatchOp.Upsert" /> or <see cref="G9ERowBatchOp.Backfill" /> row; write every column, then <see cref="EndRow" />.</summary>
    public void BeginRow(string key, long version)
    {
        StartValueRow();
        WriteKey(key);
        WriteUnsigned(version);
        OpenValues();
    }

    /// <inheritdoc cref="BeginRow(string, long)" />
    public void BeginRow(Guid key, long version)
    {
        StartValueRow();
        WriteKey(key);
        WriteUnsigned(version);
        OpenValues();
    }

    /// <inheritdoc cref="BeginRow(string, long)" />
    public void BeginRow(long key, long version)
    {
        StartValueRow();
        WriteKey(key);
        WriteUnsigned(version);
        OpenValues();
    }

    /// <summary>Adds a complete <see cref="G9ERowBatchOp.Tombstone" /> or <see cref="G9ERowBatchOp.MoveOut" /> row.</summary>
    public void AddKey(string key, long version)
    {
        StartRow(G9ERowBatchOp.Tombstone, G9ERowBatchOp.MoveOut);
        WriteKey(key);
        WriteUnsigned(version);
        CompleteRow();
    }

    /// <inheritdoc cref="AddKey(string, long)" />
    public void AddKey(Guid key, long version)
    {
        StartRow(G9ERowBatchOp.Tombstone, G9ERowBatchOp.MoveOut);
        WriteKey(key);
        WriteUnsigned(version);
        CompleteRow();
    }

    /// <inheritdoc cref="AddKey(string, long)" />
    public void AddKey(long key, long version)
    {
        StartRow(G9ERowBatchOp.Tombstone, G9ERowBatchOp.MoveOut);
        WriteKey(key);
        WriteUnsigned(version);
        CompleteRow();
    }

    /// <summary>Adds a complete <see cref="G9ERowBatchOp.KeyVersions" /> row.</summary>
    public void AddKeyVersion(string key, long version, long rowHash)
    {
        StartRow(G9ERowBatchOp.KeyVersions, G9ERowBatchOp.KeyVersions);
        WriteKey(key);
        WriteUnsigned(version);
        EnsureCapacity(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length, 8), rowHash);
        _length += 8;
        CompleteRow();
    }

    /// <summary>Starts a <see cref="G9ERowBatchOp.Push" /> row. Upserts must then write every column; deletes write none. Finish with <see cref="EndRow" />.</summary>
    public void BeginPushRow(string key, G9EPushRowOp op, long sequence, long baseVersion, long dirtyMask, long hlcMs)
    {
        StartRow(G9ERowBatchOp.Push, G9ERowBatchOp.Push);
        if (!Enum.IsDefined(op)) throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown push op.");
        WriteKey(key);
        EnsureCapacity(1);
        _buffer[_length++] = (byte)op;
        WriteUnsigned(sequence);
        WriteUnsigned(baseVersion);
        EnsureCapacity(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length, 8), dirtyMask);
        _length += 8;
        WriteVarintRaw(G9Varint.ZigZag(hlcMs));
        if (op == G9EPushRowOp.Upsert)
        {
            OpenValues();
        }
    }

    /// <summary>Completes a row started with <c>BeginRow</c> or <c>BeginPushRow</c>.</summary>
    public void EndRow()
    {
        ThrowIfDisposed();
        if (!_inRow) throw new InvalidOperationException("No row is open.");
        if (_rowHasValues && _column != _types.Length)
        {
            throw new InvalidOperationException($"Row ended after {_column} of {_types.Length} column values.");
        }

        CompleteRow();
    }

    // ---------------------------------------------------------------- values

    public void WriteNull()
    {
        var column = NextColumn(null);
        _buffer[_bitmapPosition + (column >> 3)] |= (byte)(1 << (column & 7));
    }

    public void WriteBoolean(bool value)
    {
        NextColumn(G9ESyncColumnType.Boolean);
        EnsureCapacity(1);
        _buffer[_length++] = value ? (byte)1 : (byte)0;
    }

    public void WriteInt32(int value)
    {
        NextColumn(G9ESyncColumnType.Int32);
        WriteVarintRaw(G9Varint.ZigZag(value));
    }

    public void WriteInt64(long value)
    {
        NextColumn(G9ESyncColumnType.Int64);
        WriteVarintRaw(G9Varint.ZigZag(value));
    }

    public void WriteDouble(double value)
    {
        NextColumn(G9ESyncColumnType.Double);
        EnsureCapacity(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_length, 8), value);
        _length += 8;
    }

    public void WriteDecimal(decimal value)
    {
        NextColumn(G9ESyncColumnType.Decimal);
        EnsureCapacity(16);
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length, 4), bits[i]);
            _length += 4;
        }
    }

    /// <summary>Writes a string, or NULL when <paramref name="value" /> is null.</summary>
    public void WriteString(string? value)
    {
        if (value is null)
        {
            WriteNull();
            return;
        }

        NextColumn(G9ESyncColumnType.String);
        WriteStringRaw(value);
    }

    /// <summary>Writes a string that is already UTF-8 encoded (no transcoding).</summary>
    public void WriteStringUtf8(ReadOnlySpan<byte> utf8)
    {
        NextColumn(G9ESyncColumnType.String);
        WriteBytesRaw(utf8);
    }

    public void WriteGuid(Guid value)
    {
        NextColumn(G9ESyncColumnType.Guid);
        WriteGuidRaw(value);
    }

    public void WriteDateTime(DateTime value)
    {
        NextColumn(G9ESyncColumnType.DateTime);
        EnsureCapacity(9);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length, 8), value.Ticks);
        _length += 8;
        _buffer[_length++] = (byte)value.Kind;
    }

    public void WriteDateTimeOffset(DateTimeOffset value)
    {
        NextColumn(G9ESyncColumnType.DateTimeOffset);
        EnsureCapacity(10);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length, 8), value.UtcTicks);
        _length += 8;
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_length, 2), checked((short)value.Offset.TotalMinutes));
        _length += 2;
    }

    public void WriteTimeSpan(TimeSpan value)
    {
        NextColumn(G9ESyncColumnType.TimeSpan);
        WriteVarintRaw(G9Varint.ZigZag(value.Ticks));
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        NextColumn(G9ESyncColumnType.Bytes);
        WriteBytesRaw(value);
    }

    /// <summary>Writes a boxed value converted to the next column's declared type (convenience path, allocates when boxing).</summary>
    public void WriteValue(object? value)
    {
        if (value is null or DBNull)
        {
            WriteNull();
            return;
        }

        if (!_inRow || !_rowHasValues || _column >= _types.Length)
        {
            throw new InvalidOperationException("No column value is expected.");
        }

        switch (_types[_column])
        {
            case G9ESyncColumnType.Boolean:
                WriteBoolean(value is bool b ? b : Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0);
                break;
            case G9ESyncColumnType.Int32:
                WriteInt32(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.Int64:
                WriteInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.Double:
                WriteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.Decimal:
                WriteDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.String:
                WriteString(value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.Guid:
                WriteGuid(value is Guid g ? g : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!));
                break;
            case G9ESyncColumnType.DateTime:
                WriteDateTime(value is DateTime dt ? dt : Convert.ToDateTime(value, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.DateTimeOffset:
                WriteDateTimeOffset(value is DateTimeOffset dto ? dto : new DateTimeOffset(Convert.ToDateTime(value, CultureInfo.InvariantCulture)));
                break;
            case G9ESyncColumnType.TimeSpan:
                WriteTimeSpan(value is TimeSpan ts ? ts : TimeSpan.FromTicks(Convert.ToInt64(value, CultureInfo.InvariantCulture)));
                break;
            case G9ESyncColumnType.Bytes:
                WriteBytes(value as byte[] ?? throw new InvalidCastException("Bytes column requires byte[]."));
                break;
            default:
                throw new InvalidOperationException($"Unsupported column type {_types[_column]}.");
        }
    }

    // ---------------------------------------------------------------- frame

    /// <summary>Produces the frame. Falls back to an uncompressed body when compression does not help.</summary>
    /// <param name="compression">Body compression.</param>
    /// <param name="quality">Brotli quality.</param>
    /// <param name="version">
    ///     Body layout: 1 row-wise (every reader), 2 column-grouped (smaller once compressed, ADR-0040). Write 2 only for a peer that
    ///     asked for it.
    /// </param>
    public byte[] ToFrame(G9ECompression compression = G9ECompression.Brotli, int quality = 4, int version = G9RowBatchFormat.Version)
    {
        ThrowIfDisposed();
        if (_inSection) throw new InvalidOperationException("EndSection must be called before ToFrame.");
        if (version is not (G9RowBatchFormat.Version or G9RowBatchFormat.ColumnVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Unknown G9RowBatch version.");
        }

        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(0, 4), _sectionCount);
        byte[]? grouped = null;
        try
        {
            ReadOnlySpan<byte> body;
            if (version == G9RowBatchFormat.ColumnVersion)
            {
                grouped = G9RowBatchColumnLayout.FromRows(_buffer.AsSpan(0, _length), out var groupedLength);
                body = grouped.AsSpan(0, groupedLength);
            }
            else
            {
                body = _buffer.AsSpan(0, _length);
            }

            if (compression == G9ECompression.Brotli && body.Length > 128)
            {
                var rented = ArrayPool<byte>.Shared.Rent(BrotliEncoder.GetMaxCompressedLength(body.Length));
                try
                {
                    if (BrotliEncoder.TryCompress(body, rented, out var written, quality, 22) && written < body.Length)
                    {
                        var compressed = new byte[G9RowBatchFormat.HeaderSize + written];
                        WriteHeader(compressed, (byte)version, G9ECompression.Brotli, body.Length);
                        rented.AsSpan(0, written).CopyTo(compressed.AsSpan(G9RowBatchFormat.HeaderSize));
                        return compressed;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }

            var raw = new byte[G9RowBatchFormat.HeaderSize + body.Length];
            WriteHeader(raw, (byte)version, G9ECompression.None, body.Length);
            body.CopyTo(raw.AsSpan(G9RowBatchFormat.HeaderSize));
            return raw;
        }
        finally
        {
            if (grouped is not null) ArrayPool<byte>.Shared.Return(grouped);
        }
    }

    /// <summary>Clears all content so the pooled buffer can be reused for the next frame.</summary>
    public void Reset()
    {
        ThrowIfDisposed();
        _length = 4;
        _sectionCount = 0;
        _totalRows = 0;
        _inSection = false;
        _inRow = false;
        _rowHasValues = false;
        _sectionRows = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    // ---------------------------------------------------------------- internals

    private void StartValueRow() => StartRow(G9ERowBatchOp.Upsert, G9ERowBatchOp.Backfill);

    private void StartRow(G9ERowBatchOp allowed1, G9ERowBatchOp allowed2)
    {
        ThrowIfDisposed();
        if (!_inSection) throw new InvalidOperationException("BeginSection must be called before adding rows.");
        if (_inRow) throw new InvalidOperationException("EndRow must be called before starting another row.");
        if (_op != allowed1 && _op != allowed2)
        {
            throw new InvalidOperationException($"This row kind is not valid in a {_op} section.");
        }

        _inRow = true;
        _rowHasValues = false;
        _column = 0;
    }

    private void CompleteRow()
    {
        _inRow = false;
        _rowHasValues = false;
        _sectionRows++;
        _totalRows++;
    }

    private void OpenValues()
    {
        var bitmapBytes = (_types.Length + 7) >> 3;
        EnsureCapacity(bitmapBytes);
        _bitmapPosition = _length;
        _buffer.AsSpan(_length, bitmapBytes).Clear();
        _length += bitmapBytes;
        _column = 0;
        _rowHasValues = true;
    }

    private int NextColumn(G9ESyncColumnType? expected)
    {
        ThrowIfDisposed();
        if (!_inRow || !_rowHasValues) throw new InvalidOperationException("No row with column values is open.");
        if (_column >= _types.Length) throw new InvalidOperationException("Every column of this row has already been written.");
        if (expected is not null && _types[_column] != expected)
        {
            throw new InvalidOperationException($"Column {_column} is declared {_types[_column]}, but a {expected} value was written.");
        }

        return _column++;
    }

    private void WriteKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        switch (_keyType)
        {
            case G9ESyncColumnType.Guid:
                WriteGuidRaw(Guid.Parse(key));
                break;
            case G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64:
                WriteVarintRaw(G9Varint.ZigZag(long.Parse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)));
                break;
            default:
                WriteStringRaw(key);
                break;
        }
    }

    private void WriteKey(Guid key)
    {
        if (_keyType != G9ESyncColumnType.Guid) throw new InvalidOperationException($"Section key type is {_keyType}.");
        WriteGuidRaw(key);
    }

    private void WriteKey(long key)
    {
        if (_keyType is not (G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64))
        {
            throw new InvalidOperationException($"Section key type is {_keyType}.");
        }

        WriteVarintRaw(G9Varint.ZigZag(key));
    }

    private void WriteUnsigned(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        WriteVarintRaw((ulong)value);
    }

    private void WriteVarintRaw(ulong value)
    {
        EnsureCapacity(G9Varint.MaxBytes);
        _length += G9Varint.Write(_buffer.AsSpan(_length), value);
    }

    private void WriteGuidRaw(Guid value)
    {
        EnsureCapacity(16);
        if (!value.TryWriteBytes(_buffer.AsSpan(_length, 16), bigEndian: true, out _))
        {
            throw new InvalidOperationException("Failed to write GUID bytes.");
        }

        _length += 16;
    }

    private void WriteStringRaw(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        EnsureCapacity(G9Varint.MaxBytes + byteCount);
        _length += G9Varint.Write(_buffer.AsSpan(_length), (ulong)byteCount);
        _length += Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length, byteCount));
    }

    private void WriteBytesRaw(ReadOnlySpan<byte> value)
    {
        EnsureCapacity(G9Varint.MaxBytes + value.Length);
        _length += G9Varint.Write(_buffer.AsSpan(_length), (ulong)value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    private static void WriteHeader(Span<byte> frame, byte version, G9ECompression compression, int bodyLength)
    {
        frame[0] = (byte)'G';
        frame[1] = (byte)'9';
        frame[2] = (byte)'R';
        frame[3] = (byte)'B';
        frame[4] = version;
        frame[5] = (byte)compression;
        BinaryPrimitives.WriteInt32LittleEndian(frame[6..], bodyLength);
    }

    private void EnsureCapacity(int additional)
    {
        var required = (long)_length + additional;
        if (required <= _buffer.Length) return;
        if (required > G9RowBatchFormat.MaxBodyBytes)
        {
            throw new InvalidOperationException("Row batch exceeds the maximum body size.");
        }

        var next = ArrayPool<byte>.Shared.Rent((int)Math.Max(required, (long)_buffer.Length * 2));
        _buffer.AsSpan(0, _length).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
