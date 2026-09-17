using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace G9SyncData.Server.EfCore;

/// <summary>
///     Maps an EF Core property onto a <see cref="G9ESyncColumnType" />.
///     <para>
///         The type that matters is the PROVIDER type, not the CLR type: a property with a value converter
///         (<c>Version</c> stored as text, <c>List&lt;int&gt;</c> stored as a string) reaches PostgreSQL as whatever the
///         converter produces, and that is what the wire must carry. Reading <c>GetProviderClrType()</c>
///         first is therefore not an optimisation — it is the difference between syncing the column and failing on it.
///     </para>
/// </summary>
public static class G9CEfCoreTypeMap
{
    /// <summary>Store types that PostGIS owns. They travel as text (WKB hex) and are cast back on write.</summary>
    private static readonly string[] SpatialStoreTypes = ["geometry", "geography"];

    /// <summary>The effective type of <paramref name="property" /> once value conversion is accounted for.</summary>
    /// <remarks>
    ///     <c>GetProviderClrType()</c> alone is not enough: it is set by <c>HasConversion&lt;string&gt;()</c> but NOT by
    ///     <c>HasConversion(new ValueConverter&lt;TModel, TProvider&gt;(…))</c>, where the provider type lives on the converter
    ///     (Agriwise's <c>AttributeDefinition.DataTypeConfig</c>, LES-0034). Converters chosen by the type mapping count too.
    /// </remarks>
    public static Type EffectiveClrType(IProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        var type = property.GetValueConverter()?.ProviderClrType
                   ?? property.GetProviderClrType()
                   ?? property.FindTypeMapping()?.Converter?.ProviderClrType
                   ?? property.ClrType;
        return Nullable.GetUnderlyingType(type) ?? type;
    }

    /// <summary>True when the column is a PostGIS geometry, which needs the text read/write casts.</summary>
    public static bool IsSpatial(IProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        var storeType = property.GetColumnType();
        if (!string.IsNullOrEmpty(storeType))
        {
            foreach (var spatial in SpatialStoreTypes)
            {
                if (storeType.StartsWith(spatial, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        var clrType = property.ClrType;
        return clrType.Namespace is not null
               && clrType.Namespace.StartsWith("NetTopologySuite.Geometries", StringComparison.Ordinal);
    }

    /// <summary>Maps a CLR type onto a sync column type. Enums map to their underlying integral type.</summary>
    public static bool TryMap(Type clrType, out G9ESyncColumnType columnType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (type.IsEnum) type = Enum.GetUnderlyingType(type);

        if (type == typeof(bool)) { columnType = G9ESyncColumnType.Boolean; return true; }
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint)) { columnType = G9ESyncColumnType.Int32; return true; }
        if (type == typeof(long) || type == typeof(ulong)) { columnType = G9ESyncColumnType.Int64; return true; }
        if (type == typeof(float) || type == typeof(double)) { columnType = G9ESyncColumnType.Double; return true; }
        if (type == typeof(decimal)) { columnType = G9ESyncColumnType.Decimal; return true; }
        if (type == typeof(string) || type == typeof(char)) { columnType = G9ESyncColumnType.String; return true; }
        if (type == typeof(Guid)) { columnType = G9ESyncColumnType.Guid; return true; }
        if (type == typeof(DateTime) || type == typeof(DateOnly)) { columnType = G9ESyncColumnType.DateTime; return true; }
        if (type == typeof(DateTimeOffset)) { columnType = G9ESyncColumnType.DateTimeOffset; return true; }
        if (type == typeof(TimeSpan) || type == typeof(TimeOnly)) { columnType = G9ESyncColumnType.TimeSpan; return true; }
        if (type == typeof(byte[])) { columnType = G9ESyncColumnType.Bytes; return true; }

        columnType = G9ESyncColumnType.Unspecified;
        return false;
    }
}
