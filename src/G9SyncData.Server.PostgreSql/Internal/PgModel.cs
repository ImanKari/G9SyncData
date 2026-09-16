using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using G9SyncData.Server.Model;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql.Internal;

/// <summary>A column of a live table, as introspected.</summary>
internal sealed class PgColumn
{
    public required string DbName { get; init; }
    public required string Quoted { get; init; }

    /// <summary><c>format_type</c> output, e.g. <c>character varying(200)</c>.</summary>
    public required string DbType { get; init; }

    /// <summary>Base type name (domains resolved), e.g. <c>varchar</c>.</summary>
    public required string TypeName { get; init; }

    public required bool NotNull { get; init; }
    public required bool HasDefault { get; init; }
}

/// <summary>A model column bound to its live column.</summary>
internal sealed class PgBoundColumn
{
    public required G9CSyncServerColumn Model { get; init; }

    /// <summary>Null for computed columns.</summary>
    public PgColumn? Db { get; init; }

    public required string StagingType { get; init; }
    public required string StagingTypeName { get; init; }
    public required NpgsqlDbType StagingDbType { get; init; }
}

/// <summary>A model table bound to the live schema.</summary>
internal sealed class PgTable
{
    public required G9CSyncServerTable Model { get; init; }
    public int TableId { get; set; }
    public required string BaseName { get; init; }
    public required string TrackingName { get; init; }
    public required string TrackingBare { get; init; }
    public required PgColumn Key { get; init; }
    public required string KeyStagingType { get; init; }
    public required NpgsqlDbType KeyStagingDbType { get; init; }
    public required IReadOnlyDictionary<string, PgBoundColumn> Columns { get; init; }
    public required int ExtraUniqueIndexes { get; init; }
    public required double AverageRowBytes { get; init; }

    public bool Ordered => Model.OrderedApply ?? ExtraUniqueIndexes > 0;

    public PgBoundColumn Bound(G9CSyncServerColumn column) => Columns[column.Name];
}

internal static class PgTypes
{
    private static readonly HashSet<string> PlainText = new(StringComparer.Ordinal) { "text", "varchar", "bpchar", "name" };

    private static readonly HashSet<string> Comparable = new(StringComparer.Ordinal)
    {
        "bool", "int2", "int4", "int8", "float4", "float8", "numeric", "text", "varchar", "bpchar", "name", "citext", "uuid",
        "timestamp", "timestamptz", "date", "time", "timetz", "interval", "bytea", "jsonb", "oid"
    };

    public static bool IsPlainText(string typeName) => PlainText.Contains(typeName);

    public static bool NeedsTextComparison(string typeName) => !Comparable.Contains(typeName);

    public static bool IsCompatible(G9ESyncColumnType type, string typeName) => type switch
    {
        G9ESyncColumnType.Boolean => typeName == "bool",
        G9ESyncColumnType.Int32 => typeName is "int2" or "int4",
        G9ESyncColumnType.Int64 => typeName is "int2" or "int4" or "int8",
        G9ESyncColumnType.Double => typeName is "float4" or "float8" or "numeric",
        G9ESyncColumnType.Decimal => typeName is "numeric" or "int2" or "int4" or "int8",
        G9ESyncColumnType.String => PlainText.Contains(typeName) || typeName is "citext" or "json" or "jsonb" or "xml",
        G9ESyncColumnType.Guid => typeName == "uuid",
        G9ESyncColumnType.DateTime => typeName is "timestamp" or "timestamptz" or "date",
        G9ESyncColumnType.DateTimeOffset => typeName == "timestamptz",
        G9ESyncColumnType.TimeSpan => typeName == "interval",
        G9ESyncColumnType.Bytes => typeName == "bytea",
        _ => false
    };

    public static bool IsKeyCompatible(G9ESyncColumnType keyType, string typeName) => keyType switch
    {
        G9ESyncColumnType.Guid => typeName == "uuid",
        G9ESyncColumnType.String => PlainText.Contains(typeName) || typeName == "citext",
        G9ESyncColumnType.Int32 => typeName is "int2" or "int4",
        G9ESyncColumnType.Int64 => typeName is "int2" or "int4" or "int8",
        _ => false
    };

    public static (string Sql, string TypeName, NpgsqlDbType DbType) Staging(G9ESyncColumnType type, string? typeName) => type switch
    {
        G9ESyncColumnType.Boolean => ("boolean", "bool", NpgsqlDbType.Boolean),
        G9ESyncColumnType.Int32 => ("integer", "int4", NpgsqlDbType.Integer),
        G9ESyncColumnType.Int64 => ("bigint", "int8", NpgsqlDbType.Bigint),
        G9ESyncColumnType.Double => ("double precision", "float8", NpgsqlDbType.Double),
        G9ESyncColumnType.Decimal => ("numeric", "numeric", NpgsqlDbType.Numeric),
        G9ESyncColumnType.String => ("text", "text", NpgsqlDbType.Text),
        G9ESyncColumnType.Guid => ("uuid", "uuid", NpgsqlDbType.Uuid),
        G9ESyncColumnType.DateTime when typeName is "timestamp" or "date" => ("timestamp", "timestamp", NpgsqlDbType.Timestamp),
        G9ESyncColumnType.DateTime => ("timestamptz", "timestamptz", NpgsqlDbType.TimestampTz),
        G9ESyncColumnType.DateTimeOffset => ("timestamptz", "timestamptz", NpgsqlDbType.TimestampTz),
        G9ESyncColumnType.TimeSpan => ("interval", "interval", NpgsqlDbType.Interval),
        G9ESyncColumnType.Bytes => ("bytea", "bytea", NpgsqlDbType.Bytea),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static (string Sql, NpgsqlDbType DbType) KeyStaging(G9ESyncColumnType keyType) => keyType switch
    {
        G9ESyncColumnType.Guid => ("uuid", NpgsqlDbType.Uuid),
        G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 => ("bigint", NpgsqlDbType.Bigint),
        _ => ("text", NpgsqlDbType.Text)
    };

    public static NpgsqlDbType Parameter(G9ESyncColumnType type, bool isList)
    {
        var scalar = type switch
        {
            G9ESyncColumnType.Boolean => NpgsqlDbType.Boolean,
            G9ESyncColumnType.Int32 => NpgsqlDbType.Integer,
            G9ESyncColumnType.Int64 => NpgsqlDbType.Bigint,
            G9ESyncColumnType.Guid => NpgsqlDbType.Uuid,
            _ => NpgsqlDbType.Text
        };
        return isList ? NpgsqlDbType.Array | scalar : scalar;
    }

    /// <summary>Canonical key text, identical to <see cref="G9CSyncKey" />.</summary>
    public static string KeyText(G9ESyncColumnType keyType, string expression) =>
        keyType == G9ESyncColumnType.Guid ? $"lower(({expression})::text)" : $"(({expression})::text)";

    public static bool SupportsCanonicalText(G9ESyncColumnType type) =>
        type is G9ESyncColumnType.Boolean or G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 or G9ESyncColumnType.String or G9ESyncColumnType.Guid;

    /// <summary>Canonical text of a computed value, identical to <see cref="G9CSyncDigest.CanonicalText" />.</summary>
    public static string CanonicalText(G9ESyncColumnType type, string expression) => type switch
    {
        G9ESyncColumnType.Boolean => $"(CASE WHEN ({expression}) THEN '1' WHEN NOT ({expression}) THEN '0' END)",
        G9ESyncColumnType.Guid => $"lower(({expression})::text)",
        _ => $"(({expression})::text)"
    };
}

internal static class PgNames
{
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string Bits(int index) => (1L << index).ToString(CultureInfo.InvariantCulture);

    /// <summary>A bare identifier for derived objects, short enough for PostgreSQL's 63-byte limit with suffixes.</summary>
    public static string TrackingBare(string schema, string table)
    {
        var bare = schema + "__" + table;
        if (Encoding.UTF8.GetByteCount(bare) <= 48) return bare;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(bare)).AsSpan(0, 4));
        var prefix = bare;
        while (Encoding.UTF8.GetByteCount(prefix) > 38) prefix = prefix[..^1];
        return prefix + "_" + hash;
    }
}
