using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace G9SyncData;

/// <summary>
///     The reconciliation hash, defined so PostgreSQL can compute the exact same numbers in plain SQL.
/// </summary>
/// <remarks>
///     <para>
///         <b>Bucket</b> = first byte of <c>md5(canonicalKey)</c>. SQL:
///         <c>get_byte(decode(md5(key), 'hex'), 0)</c>.
///     </para>
///     <para>
///         <b>Row hash</b> = first 8 bytes (big-endian, signed) of <c>md5(key || ':' || version [|| ':' || computed…])</c>.
///         SQL: <c>('x' || left(md5(...), 16))::bit(64)::bigint</c>.
///     </para>
///     <para>
///         <b>Bucket digest</b> = XOR of the row hashes of every row in the bucket. XOR makes the digest
///         order-independent and incrementally maintainable.
///     </para>
///     MD5 is used as a portable, SQL-reproducible bucketing function, not for security (ADR-0012); computed by <see cref="G9CMd5" />,
///     not the platform class, because Android routes that through JNI.
/// </remarks>
public static class G9CSyncDigest
{
    /// <summary>Number of first-level buckets.</summary>
    public const int BucketCount = 256;

    /// <summary>Bucket of a canonical key.</summary>
    public static int GetBucket(string canonicalKey)
    {
        ArgumentNullException.ThrowIfNull(canonicalKey);
        Span<byte> hash = stackalloc byte[16];
        HashUtf8(canonicalKey, hash);
        return hash[0];
    }

    /// <summary>Row hash of a canonical key, its server version and optional computed-column values.</summary>
    public static long GetRowHash(string canonicalKey, long version, ReadOnlySpan<string?> computedValues = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalKey);
        var builder = new StringBuilder(canonicalKey.Length + 24);
        builder.Append(canonicalKey).Append(':').Append(version.ToString(CultureInfo.InvariantCulture));
        foreach (var value in computedValues)
        {
            builder.Append(':').Append(value);
        }

        Span<byte> hash = stackalloc byte[16];
        HashUtf8(builder.ToString(), hash);
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    /// <summary>Bucket of a canonical key given as UTF-8 (allocation-free form of <see cref="GetBucket(string)" />).</summary>
    public static int GetBucket(ReadOnlySpan<byte> canonicalKeyUtf8)
    {
        Span<byte> hash = stackalloc byte[16];
        G9CMd5.HashData(canonicalKeyUtf8, hash);
        return hash[0];
    }

    /// <summary>
    ///     Row hash of a canonical UTF-8 key and its server version, without computed columns (allocation-free form of
    ///     <see cref="GetRowHash(string, long, ReadOnlySpan{string})" />).
    /// </summary>
    public static long GetRowHash(ReadOnlySpan<byte> canonicalKeyUtf8, long version)
    {
        var length = canonicalKeyUtf8.Length + 21;
        byte[]? rented = null;
        var buffer = length <= 256 ? stackalloc byte[length] : rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            canonicalKeyUtf8.CopyTo(buffer);
            buffer[canonicalKeyUtf8.Length] = (byte)':';
            if (!version.TryFormat(buffer[(canonicalKeyUtf8.Length + 1)..], out var digits, default, CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("The version does not fit its buffer.");
            }

            Span<byte> hash = stackalloc byte[16];
            G9CMd5.HashData(buffer[..(canonicalKeyUtf8.Length + 1 + digits)], hash);
            return BinaryPrimitives.ReadInt64BigEndian(hash);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Canonical text of a computed value, identical to what the server SQL produces.</summary>
    public static string? CanonicalText(G9ESyncColumnType type, object? value) => value switch
    {
        null or DBNull => null,
        bool b => b ? "1" : "0",
        long l when type == G9ESyncColumnType.Boolean => l != 0 ? "1" : "0",
        Guid g => G9CSyncKey.FromGuid(g),
        string s when type == G9ESyncColumnType.Guid && Guid.TryParse(s, out var g) => G9CSyncKey.FromGuid(g),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static void HashUtf8(string text, Span<byte> destination)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        byte[]? rented = null;
        var buffer = byteCount <= 512 ? stackalloc byte[byteCount] : rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(text, buffer);
            G9CMd5.HashData(buffer[..written], destination);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
