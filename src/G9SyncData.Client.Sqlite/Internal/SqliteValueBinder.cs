using System.Globalization;
using G9SyncData.Client.Sqlite.Native;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;

namespace G9SyncData.Client.Sqlite.Internal;

/// <summary>
///     The ONE place a frame value becomes a stored SQLite value: the per-row apply on the device and the snapshot-file writer on the server
///     (ADR-0072) both bind through it, so a row copied out of a server-built file is byte for byte the row the device would have written
///     itself. <c>SqliteApplyValueTests</c> and the golden vectors pin the forms.
/// </summary>
internal static class SqliteValueBinder
{
    public const string DateTimeOffsetFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";

    /// <summary>Binds one frame value in its local storage form without allocating.</summary>
    public static void Bind(SqliteRowStatement statement, int index, G9CRowBatchReader reader, int column, G9ESyncColumnType type,
        G9CSqliteSyncStoreOptions options)
    {
        if (reader.IsNull(column))
        {
            statement.BindNull(index);
            return;
        }

        Span<byte> buffer = stackalloc byte[64];
        switch (type)
        {
            case G9ESyncColumnType.Boolean:
                statement.BindInt64(index, reader.GetBoolean(column) ? 1 : 0);
                break;
            case G9ESyncColumnType.Int32:
                statement.BindInt64(index, reader.GetInt32(column));
                break;
            case G9ESyncColumnType.Int64:
                statement.BindInt64(index, reader.GetInt64(column));
                break;
            case G9ESyncColumnType.TimeSpan:
                statement.BindInt64(index, reader.GetTimeSpan(column).Ticks);
                break;
            case G9ESyncColumnType.Double:
                statement.BindDouble(index, reader.GetDouble(column));
                break;
            case G9ESyncColumnType.Decimal:
                var number = reader.GetDecimal(column);
                if (options.DecimalStorage == G9ESqliteDecimalStorage.Real) statement.BindDouble(index, (double)number);
                else if (number.TryFormat(buffer, out var decimalLength, default, CultureInfo.InvariantCulture)) statement.BindText(index, buffer[..decimalLength]);
                else statement.BindText(index, number.ToString(CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.String:
                statement.BindText(index, reader.GetUtf8(column));
                break;
            case G9ESyncColumnType.Guid:
                reader.GetGuid(column).TryFormat(buffer, out var guidLength);
                statement.BindText(index, buffer[..guidLength]);
                break;
            case G9ESyncColumnType.DateTime:
                var moment = reader.GetDateTime(column);
                if (options.DateTimeStorage == G9ESqliteDateTimeStorage.Ticks) statement.BindInt64(index, moment.Ticks);
                else if (moment.TryFormat(buffer, out var dateLength, options.DateTimeFormat, CultureInfo.InvariantCulture)) statement.BindText(index, buffer[..dateLength]);
                else statement.BindText(index, moment.ToString(options.DateTimeFormat, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.DateTimeOffset:
                var offset = reader.GetDateTimeOffset(column);
                if (offset.TryFormat(buffer, out var offsetLength, DateTimeOffsetFormat, CultureInfo.InvariantCulture)) statement.BindText(index, buffer[..offsetLength]);
                else statement.BindText(index, offset.ToString(DateTimeOffsetFormat, CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.Bytes:
                statement.BindBlob(index, reader.GetBytes(column));
                break;
            default:
                throw new InvalidOperationException($"Unsupported column type {type}.");
        }
    }

    /// <summary>
    ///     Binds the row's key in its stored form (INTEGER for integer keys, canonical text otherwise) and returns the canonical key TEXT -
    ///     the form <c>g9_rows.pk</c> holds - formatted into <paramref name="buffer" /> (40 bytes is enough for every key type but String).
    /// </summary>
    public static ReadOnlySpan<byte> BindKey(SqliteRowStatement statement, int index, G9CRowBatchReader reader, Span<byte> buffer)
    {
        switch (reader.KeyType)
        {
            case G9ESyncColumnType.Guid:
            {
                reader.KeyGuid.TryFormat(buffer, out var length);
                var text = buffer[..length];
                statement.BindText(index, text);
                return text;
            }
            case G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64:
            {
                var value = reader.KeyInt64;
                value.TryFormat(buffer, out var length, default, CultureInfo.InvariantCulture);
                statement.BindInt64(index, value);
                return buffer[..length];
            }
            default:
            {
                var text = reader.KeyUtf8;
                statement.BindText(index, text);
                return text;
            }
        }
    }
}
