using System.Globalization;

namespace G9SyncData;

/// <summary>
///     Canonical primary-key text. Every place a key is compared, hashed or stored as text (client metadata,
///     digests, tokens) uses this form, so the same row can never appear under two spellings — the Dotmim
///     case-sensitive parameter hash trap.
/// </summary>
/// <remarks>
///     v1 supports single-column keys of type <see cref="G9ESyncColumnType.Guid" />,
///     <see cref="G9ESyncColumnType.String" />, <see cref="G9ESyncColumnType.Int32" /> and
///     <see cref="G9ESyncColumnType.Int64" /> (ADR-0006).
/// </remarks>
public static class G9CSyncKey
{
    /// <summary>True when <paramref name="type" /> can be a primary key.</summary>
    public static bool IsSupportedKeyType(G9ESyncColumnType type) =>
        type is G9ESyncColumnType.Guid or G9ESyncColumnType.String or G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64;

    /// <summary>Canonical text of a GUID key: lower-case "D" format (matches PostgreSQL <c>uuid::text</c>).</summary>
    public static string FromGuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Canonical text of an integer key: invariant decimal (matches PostgreSQL <c>bigint::text</c>).</summary>
    public static string FromInt64(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Normalises any textual key of the given type to canonical form.</summary>
    /// <exception cref="FormatException">The text is not a valid key of that type.</exception>
    public static string Normalize(G9ESyncColumnType keyType, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return keyType switch
        {
            G9ESyncColumnType.Guid => FromGuid(Guid.Parse(key)),
            G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 =>
                FromInt64(long.Parse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)),
            G9ESyncColumnType.String => key,
            _ => throw new ArgumentOutOfRangeException(nameof(keyType), keyType, "Unsupported key type.")
        };
    }

    /// <summary>Tries to normalise; returns false instead of throwing.</summary>
    public static bool TryNormalize(G9ESyncColumnType keyType, string? key, out string canonical)
    {
        canonical = string.Empty;
        if (key is null) return false;
        switch (keyType)
        {
            case G9ESyncColumnType.Guid when Guid.TryParse(key, out var g):
                canonical = FromGuid(g);
                return true;
            case G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64
                when long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l):
                canonical = FromInt64(l);
                return true;
            case G9ESyncColumnType.String:
                canonical = key;
                return true;
            default:
                return false;
        }
    }
}
