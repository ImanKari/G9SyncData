using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace G9SyncData.Protocol.RowBatch;

/// <summary>
///     Reads a G9RowBatch frame produced by <see cref="G9CRowBatchWriter" />, in either body layout (1 row-wise, 2 column-grouped).
///     Forward-only and allocation-light: values are decoded from the frame buffer on demand; only <see cref="GetString" /> and
///     <see cref="Key" /> allocate.
/// </summary>
public sealed class G9CRowBatchReader : IDisposable
{
    private readonly byte[] _body;
    private readonly int _bodyStart;
    private readonly int _bodyLength;
    private readonly bool _rented;
    private int _position;
    private int _sectionsRemaining;
    private bool _sectionOpen;
    private int _rowsRemaining;

    private G9ESyncColumnType[] _types = [];
    private int[] _offsets = [];
    private int _bitmapPosition;
    private int _keyStart;
    private int _keyLength;
    private string? _keyText;
    private bool _disposed;

    private G9CRowBatchReader(byte[] body, int start, int length, bool rented)
    {
        _body = body;
        _bodyStart = start;
        _bodyLength = length;
        _rented = rented;
        if (length < 4) throw new FormatException("Row batch body is too short.");
        SectionCount = BinaryPrimitives.ReadInt32LittleEndian(Body[..4]);
        if (SectionCount < 0) throw new FormatException("Negative section count.");
        _sectionsRemaining = SectionCount;
        _position = 4;
    }

    public int SectionCount { get; }

    // section
    public G9ERowBatchOp Op { get; private set; }
    public int TableId { get; private set; }
    public G9ESyncColumnType KeyType { get; private set; }
    public IReadOnlyList<G9ESyncColumnType> ColumnTypes => _types;
    public int ColumnCount => _types.Length;
    public int RowCount { get; private set; }

    // row
    public long Version { get; private set; }
    public G9EPushRowOp PushOp { get; private set; }
    public long Sequence { get; private set; }
    public long BaseVersion { get; private set; }
    public long DirtyMask { get; private set; }
    public long HlcMs { get; private set; }
    public long RowHash { get; private set; }
    public bool HasValues { get; private set; }

    private ReadOnlySpan<byte> Body => _body.AsSpan(_bodyStart, _bodyLength);

    /// <summary>Validates, decompresses when needed, and opens a frame.</summary>
    /// <exception cref="FormatException">The frame is malformed.</exception>
    public static G9CRowBatchReader Open(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length < G9RowBatchFormat.HeaderSize ||
            frame[0] != (byte)'G' || frame[1] != (byte)'9' || frame[2] != (byte)'R' || frame[3] != (byte)'B')
        {
            throw new FormatException("Not a G9RowBatch frame.");
        }

        var version = frame[4];
        if (version is not (G9RowBatchFormat.Version or G9RowBatchFormat.ColumnVersion))
        {
            throw new FormatException($"Unsupported G9RowBatch version {version}.");
        }

        var compression = (G9ECompression)frame[5];
        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(6, 4));
        if (bodyLength < 4 || bodyLength > G9RowBatchFormat.MaxBodyBytes + (64 * 1024 * 1024)) throw new FormatException("Invalid body length.");

        switch (compression)
        {
            case G9ECompression.None:
                if (frame.Length - G9RowBatchFormat.HeaderSize != bodyLength) throw new FormatException("Body length mismatch.");
                return version == G9RowBatchFormat.Version
                    ? new G9CRowBatchReader(frame, G9RowBatchFormat.HeaderSize, bodyLength, rented: false)
                    : FromColumns(frame.AsSpan(G9RowBatchFormat.HeaderSize, bodyLength));
            case G9ECompression.Brotli:
                var body = ArrayPool<byte>.Shared.Rent(bodyLength);
                var keep = false;
                try
                {
                    if (!BrotliDecoder.TryDecompress(frame.AsSpan(G9RowBatchFormat.HeaderSize), body.AsSpan(0, bodyLength), out var written)
                        || written != bodyLength)
                    {
                        throw new FormatException("Brotli body could not be decompressed.");
                    }

                    if (version == G9RowBatchFormat.ColumnVersion) return FromColumns(body.AsSpan(0, bodyLength));
                    keep = true;
                    return new G9CRowBatchReader(body, 0, bodyLength, rented: true);
                }
                finally
                {
                    if (!keep) ArrayPool<byte>.Shared.Return(body);
                }
            default:
                throw new FormatException($"Unknown compression {(byte)compression}.");
        }
    }

    /// <summary>A column-grouped body is regrouped into rows once, then read like any row-wise body.</summary>
    private static G9CRowBatchReader FromColumns(ReadOnlySpan<byte> columns)
    {
        var rows = G9RowBatchColumnLayout.ToRows(columns, out var length);
        try
        {
            return new G9CRowBatchReader(rows, 0, length, rented: true);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rows);
            throw;
        }
    }

    /// <summary>Moves to the next section, skipping unread rows of the current one.</summary>
    public bool NextSection()
    {
        ThrowIfDisposed();
        if (_sectionOpen)
        {
            while (ReadRow())
            {
            }
        }

        if (_sectionsRemaining == 0)
        {
            _sectionOpen = false;
            return false;
        }

        var body = Body;
        Op = (G9ERowBatchOp)ReadByte(body);
        if (!Enum.IsDefined(Op)) throw new FormatException($"Unknown op {(byte)Op}.");
        TableId = G9Varint.ReadInt32(body, ref _position);
        KeyType = (G9ESyncColumnType)ReadByte(body);
        if (!G9CSyncKey.IsSupportedKeyType(KeyType)) throw new FormatException($"Unsupported key type {KeyType}.");
        var columnCount = G9Varint.ReadInt32(body, ref _position);
        if (columnCount > 1024) throw new FormatException("Too many columns.");
        EnsureAvailable(body, columnCount + 4);
        _types = new G9ESyncColumnType[columnCount];
        for (var i = 0; i < columnCount; i++) _types[i] = (G9ESyncColumnType)body[_position++];
        RowCount = BinaryPrimitives.ReadInt32LittleEndian(body.Slice(_position, 4));
        _position += 4;
        if (RowCount < 0) throw new FormatException("Negative row count.");
        if (_offsets.Length < columnCount) _offsets = new int[columnCount];
        _rowsRemaining = RowCount;
        _sectionsRemaining--;
        _sectionOpen = true;
        return true;
    }

    /// <summary>Moves to the next row of the current section.</summary>
    public bool ReadRow()
    {
        ThrowIfDisposed();
        if (!_sectionOpen || _rowsRemaining == 0) return false;
        var body = Body;
        _keyText = null;
        HasValues = false;
        Version = 0;
        RowHash = 0;

        switch (KeyType)
        {
            case G9ESyncColumnType.Guid:
                EnsureAvailable(body, 16);
                _keyStart = _position;
                _keyLength = 16;
                _position += 16;
                break;
            case G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64:
                _keyStart = _position;
                G9Varint.Read(body, ref _position);
                _keyLength = _position - _keyStart;
                break;
            default:
                var length = G9Varint.ReadInt32(body, ref _position);
                EnsureAvailable(body, length);
                _keyStart = _position;
                _keyLength = length;
                _position += length;
                break;
        }

        switch (Op)
        {
            case G9ERowBatchOp.Upsert or G9ERowBatchOp.Backfill:
                Version = (long)G9Varint.Read(body, ref _position);
                ReadValues(body);
                break;
            case G9ERowBatchOp.Tombstone or G9ERowBatchOp.MoveOut:
                Version = (long)G9Varint.Read(body, ref _position);
                break;
            case G9ERowBatchOp.KeyVersions:
                Version = (long)G9Varint.Read(body, ref _position);
                EnsureAvailable(body, 8);
                RowHash = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(_position, 8));
                _position += 8;
                break;
            case G9ERowBatchOp.Push:
                PushOp = (G9EPushRowOp)ReadByte(body);
                if (!Enum.IsDefined(PushOp)) throw new FormatException($"Unknown push op {(byte)PushOp}.");
                Sequence = (long)G9Varint.Read(body, ref _position);
                BaseVersion = (long)G9Varint.Read(body, ref _position);
                EnsureAvailable(body, 8);
                DirtyMask = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(_position, 8));
                _position += 8;
                HlcMs = G9Varint.UnZigZag(G9Varint.Read(body, ref _position));
                if (PushOp == G9EPushRowOp.Upsert) ReadValues(body);
                break;
        }

        _rowsRemaining--;
        return true;
    }

    // ---------------------------------------------------------------- key

    /// <summary>Canonical key text (see <see cref="G9CSyncKey" />).</summary>
    public string Key => _keyText ??= KeyType switch
    {
        G9ESyncColumnType.Guid => G9CSyncKey.FromGuid(KeyGuid),
        G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 => G9CSyncKey.FromInt64(KeyInt64),
        _ => Encoding.UTF8.GetString(Body.Slice(_keyStart, _keyLength))
    };

    public Guid KeyGuid => KeyType == G9ESyncColumnType.Guid
        ? new Guid(Body.Slice(_keyStart, 16), bigEndian: true)
        : throw new InvalidOperationException($"Key type is {KeyType}.");

    public long KeyInt64
    {
        get
        {
            if (KeyType is not (G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64)) throw new InvalidOperationException($"Key type is {KeyType}.");
            var position = _keyStart;
            return G9Varint.UnZigZag(G9Varint.Read(Body, ref position));
        }
    }

    /// <summary>UTF-8 bytes of a string key.</summary>
    public ReadOnlySpan<byte> KeyUtf8 => KeyType == G9ESyncColumnType.String
        ? Body.Slice(_keyStart, _keyLength)
        : throw new InvalidOperationException($"Key type is {KeyType}.");

    // ---------------------------------------------------------------- values

    public bool IsNull(int column)
    {
        EnsureValues(column);
        return _offsets[column] < 0;
    }

    public bool GetBoolean(int column) => Body[Offset(column, G9ESyncColumnType.Boolean)] != 0;

    public int GetInt32(int column)
    {
        var position = Offset(column, G9ESyncColumnType.Int32);
        var value = G9Varint.UnZigZag(G9Varint.Read(Body, ref position));
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : throw new FormatException("Int32 value is out of range.");
    }

    public long GetInt64(int column)
    {
        var position = Offset(column, G9ESyncColumnType.Int64);
        return G9Varint.UnZigZag(G9Varint.Read(Body, ref position));
    }

    public double GetDouble(int column) =>
        BinaryPrimitives.ReadDoubleLittleEndian(Body.Slice(Offset(column, G9ESyncColumnType.Double), 8));

    public decimal GetDecimal(int column)
    {
        var position = Offset(column, G9ESyncColumnType.Decimal);
        Span<int> bits = stackalloc int[4];
        for (var i = 0; i < 4; i++) bits[i] = BinaryPrimitives.ReadInt32LittleEndian(Body.Slice(position + (i * 4), 4));
        // decimal.GetBits: bits 0-15 and 24-30 of the flags are zero and the scale is at most 28.
        if ((bits[3] & 0x7F00FFFF) != 0 || ((bits[3] >> 16) & 0xFF) > 28) throw new FormatException("Invalid decimal value.");
        return new decimal(bits);
    }

    public string GetString(int column) => Encoding.UTF8.GetString(GetUtf8(column));

    public ReadOnlySpan<byte> GetUtf8(int column) => LengthPrefixed(Offset(column, G9ESyncColumnType.String));

    public Guid GetGuid(int column) => new(Body.Slice(Offset(column, G9ESyncColumnType.Guid), 16), bigEndian: true);

    public DateTime GetDateTime(int column)
    {
        var position = Offset(column, G9ESyncColumnType.DateTime);
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(Body.Slice(position, 8));
        var kind = Body[position + 8];
        if ((ulong)ticks > (ulong)DateTime.MaxValue.Ticks || kind > (byte)DateTimeKind.Local) throw new FormatException("Invalid DateTime value.");
        return new DateTime(ticks, (DateTimeKind)kind);
    }

    public DateTimeOffset GetDateTimeOffset(int column)
    {
        var position = Offset(column, G9ESyncColumnType.DateTimeOffset);
        var utcTicks = BinaryPrimitives.ReadInt64LittleEndian(Body.Slice(position, 8));
        var minutes = BinaryPrimitives.ReadInt16LittleEndian(Body.Slice(position + 8, 2));
        var offset = TimeSpan.FromMinutes(minutes);
        var local = utcTicks + offset.Ticks;
        if (minutes is < -840 or > 840 || (ulong)utcTicks > (ulong)DateTime.MaxValue.Ticks || (ulong)local > (ulong)DateTime.MaxValue.Ticks)
        {
            throw new FormatException("Invalid DateTimeOffset value.");
        }

        return new DateTimeOffset(local, offset);
    }

    public TimeSpan GetTimeSpan(int column)
    {
        var position = Offset(column, G9ESyncColumnType.TimeSpan);
        return TimeSpan.FromTicks(G9Varint.UnZigZag(G9Varint.Read(Body, ref position)));
    }

    public ReadOnlySpan<byte> GetBytes(int column) => LengthPrefixed(Offset(column, G9ESyncColumnType.Bytes));

    /// <summary>Boxed value of a column in its natural CLR type, or null (convenience path, allocates).</summary>
    public object? GetValue(int column)
    {
        if (IsNull(column)) return null;
        return _types[column] switch
        {
            G9ESyncColumnType.Boolean => GetBoolean(column),
            G9ESyncColumnType.Int32 => GetInt32(column),
            G9ESyncColumnType.Int64 => GetInt64(column),
            G9ESyncColumnType.Double => GetDouble(column),
            G9ESyncColumnType.Decimal => GetDecimal(column),
            G9ESyncColumnType.String => GetString(column),
            G9ESyncColumnType.Guid => GetGuid(column),
            G9ESyncColumnType.DateTime => GetDateTime(column),
            G9ESyncColumnType.DateTimeOffset => GetDateTimeOffset(column),
            G9ESyncColumnType.TimeSpan => GetTimeSpan(column),
            G9ESyncColumnType.Bytes => GetBytes(column).ToArray(),
            _ => throw new InvalidOperationException($"Unsupported column type {_types[column]}.")
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_rented) ArrayPool<byte>.Shared.Return(_body);
    }

    // ---------------------------------------------------------------- internals

    private void ReadValues(ReadOnlySpan<byte> body)
    {
        var count = _types.Length;
        var bitmapBytes = (count + 7) >> 3;
        EnsureAvailable(body, bitmapBytes);
        _bitmapPosition = _position;
        _position += bitmapBytes;
        HasValues = true;

        for (var column = 0; column < count; column++)
        {
            if ((body[_bitmapPosition + (column >> 3)] & (1 << (column & 7))) != 0)
            {
                _offsets[column] = -1;
                continue;
            }

            _offsets[column] = _position;
            switch (_types[column])
            {
                case G9ESyncColumnType.Boolean:
                    EnsureAvailable(body, 1);
                    _position += 1;
                    break;
                case G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 or G9ESyncColumnType.TimeSpan:
                    G9Varint.Read(body, ref _position);
                    break;
                case G9ESyncColumnType.Double:
                    EnsureAvailable(body, 8);
                    _position += 8;
                    break;
                case G9ESyncColumnType.Decimal:
                case G9ESyncColumnType.Guid:
                    EnsureAvailable(body, 16);
                    _position += 16;
                    break;
                case G9ESyncColumnType.DateTime:
                    EnsureAvailable(body, 9);
                    _position += 9;
                    break;
                case G9ESyncColumnType.DateTimeOffset:
                    EnsureAvailable(body, 10);
                    _position += 10;
                    break;
                case G9ESyncColumnType.String or G9ESyncColumnType.Bytes:
                    var length = G9Varint.ReadInt32(body, ref _position);
                    EnsureAvailable(body, length);
                    _position += length;
                    break;
                default:
                    throw new FormatException($"Unknown column type {(byte)_types[column]}.");
            }
        }
    }

    private int Offset(int column, G9ESyncColumnType expected)
    {
        EnsureValues(column);
        if (_types[column] != expected) throw new InvalidOperationException($"Column {column} is {_types[column]}, not {expected}.");
        var offset = _offsets[column];
        if (offset < 0) throw new InvalidOperationException($"Column {column} is NULL.");
        return offset;
    }

    private void EnsureValues(int column)
    {
        ThrowIfDisposed();
        if (!HasValues) throw new InvalidOperationException("The current row carries no column values.");
        if ((uint)column >= (uint)_types.Length) throw new ArgumentOutOfRangeException(nameof(column));
    }

    private ReadOnlySpan<byte> LengthPrefixed(int position)
    {
        var body = Body;
        var length = G9Varint.ReadInt32(body, ref position);
        return body.Slice(position, length);
    }

    private byte ReadByte(ReadOnlySpan<byte> body)
    {
        EnsureAvailable(body, 1);
        return body[_position++];
    }

    private void EnsureAvailable(ReadOnlySpan<byte> body, int count)
    {
        if (count < 0 || _position + count > body.Length) throw new FormatException("Row batch is truncated.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
