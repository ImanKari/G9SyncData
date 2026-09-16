using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace G9SyncData.SourceGenerators;

/// <summary>Reads one <c>[G9AttrSyncTable]</c> class into a <see cref="TableModel" />. Pure: symbols in, records out.</summary>
internal static class G9SyncTableExtractor
{
    private const string KeyAttribute = "G9SyncData.G9AttrSyncKeyAttribute";
    private const string IgnoreAttribute = "G9SyncData.G9AttrSyncIgnoreAttribute";
    private const string ColumnAttribute = "G9SyncData.G9AttrSyncColumnAttribute";
    private const string IndexAttribute = "G9SyncData.G9AttrSyncIndexAttribute";

    // sqlite-net-pcl, so existing entities need no duplicate attributes
    private const string SqliteTable = "SQLite.TableAttribute";
    private const string SqliteColumn = "SQLite.ColumnAttribute";
    private const string SqlitePrimaryKey = "SQLite.PrimaryKeyAttribute";
    private const string SqliteIgnore = "SQLite.IgnoreAttribute";
    private const string SqliteNotNull = "SQLite.NotNullAttribute";
    private const string SqliteIndexed = "SQLite.IndexedAttribute";
    private const string SqliteUnique = "SQLite.UniqueAttribute";

    private const int MaxColumns = 62;

    /// <summary>Member names of <c>G9ESyncColumnType</c>, indexed by value.</summary>
    private static readonly string[] ColumnTypes =
        ["Unspecified", "Boolean", "Int32", "Int64", "Double", "Decimal", "String", "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "Bytes"];

    public static TableModel Extract(INamedTypeSymbol type, AttributeData table, CancellationToken cancellationToken)
    {
        var diagnostics = new List<DiagnosticInfo>();
        var location = LocationInfo.From(type);
        var typeName = type.ToDisplayString();

        var serverTable = table.ConstructorArguments.Length > 0 ? table.ConstructorArguments[0].Value as string : null;
        if (string.IsNullOrWhiteSpace(serverTable)) diagnostics.Add(Info(G9SyncDiagnostics.NoServerTable, location, type.Name));
        var localName = Named(table, "LocalName") as string ?? FirstArgument(Find(type, SqliteTable)) as string ?? type.Name;

        var properties = CollectProperties(type);
        var key = properties.FirstOrDefault(p => Has(p, KeyAttribute))
                  ?? properties.FirstOrDefault(p => Has(p, SqlitePrimaryKey))
                  ?? properties.FirstOrDefault(p => p.Name == "Id");

        var keyColumn = string.Empty;
        var keyType = "Guid";
        if (key is null)
        {
            diagnostics.Add(Info(G9SyncDiagnostics.NoKey, location, type.Name));
        }
        else
        {
            var keyAttribute = Find(key, ColumnAttribute);
            keyColumn = ColumnName(key, keyAttribute);
            var inferred = ExplicitType(keyAttribute) ?? Infer(key.Type);
            if (inferred is "Guid" or "String" or "Int32" or "Int64") keyType = inferred;
            else diagnostics.Add(Info(G9SyncDiagnostics.UnsupportedKey, LocationInfo.From(key) ?? location, type.Name, key.Name, key.Type.ToDisplayString()));
        }

        var columns = new List<ColumnModel>();
        var included = new List<(IPropertySymbol Property, string Column)>();
        foreach (var property in properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SymbolEqualityComparer.Default.Equals(property, key) || Has(property, IgnoreAttribute) || Has(property, SqliteIgnore)) continue;

            var columnAttribute = Find(property, ColumnAttribute);
            var columnType = ExplicitType(columnAttribute) ?? Infer(property.Type);
            if (columnType is null)
            {
                diagnostics.Add(Info(G9SyncDiagnostics.UnsupportedType, LocationInfo.From(property) ?? location, type.Name, property.Name,
                    property.Type.ToDisplayString()));
                continue;
            }

            var name = ColumnName(property, columnAttribute);
            columns.Add(new ColumnModel(name, columnType, IsNullable(property, columnAttribute)));
            included.Add((property, name));
        }

        if (columns.Count > MaxColumns)
        {
            diagnostics.Add(Info(G9SyncDiagnostics.TooManyColumns, location, type.Name, columns.Count.ToString(CultureInfo.InvariantCulture)));
        }

        var indexes = type.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == IndexAttribute)
            .Select(a => new IndexModel(
                Named(a, "Name") as string,
                Named(a, "Unique") is true,
                new EquatableArray<string>(a.ConstructorArguments.Length > 0 && a.ConstructorArguments[0].Kind == TypedConstantKind.Array
                    ? a.ConstructorArguments[0].Values.Select(v => v.Value as string ?? string.Empty)
                    : [])))
            .Concat(SqliteNetIndexes(included));

        return new TableModel(typeName, serverTable ?? string.Empty, localName, keyColumn, keyType, new EquatableArray<ColumnModel>(columns),
            new EquatableArray<IndexModel>(indexes), location, new EquatableArray<DiagnosticInfo>(diagnostics));
    }

    /// <summary>
    ///     sqlite-net <c>[Indexed]</c> / <c>[Unique]</c> on synced properties: unnamed ones become single-column indexes; named ones
    ///     with the same name form one composite index ordered by <c>Order</c>.
    /// </summary>
    private static IEnumerable<IndexModel> SqliteNetIndexes(List<(IPropertySymbol Property, string Column)> columns)
    {
        var named = new Dictionary<string, List<(int Order, string Column, bool Unique)>>(StringComparer.Ordinal);
        foreach (var (property, column) in columns)
        {
            foreach (var attribute in property.GetAttributes())
            {
                if (!IsSqliteIndexed(attribute.AttributeClass)) continue;
                var unique = attribute.AttributeClass!.ToDisplayString() == SqliteUnique || Named(attribute, "Unique") is true;
                var name = Named(attribute, "Name") as string ?? FirstArgument(attribute) as string;
                var order = Named(attribute, "Order") is int namedOrder ? namedOrder
                    : attribute.ConstructorArguments.Length > 1 && attribute.ConstructorArguments[1].Value is int argumentOrder ? argumentOrder
                    : 0;

                if (string.IsNullOrEmpty(name))
                {
                    yield return new IndexModel(null, unique, new EquatableArray<string>([column]));
                    continue;
                }

                if (!named.TryGetValue(name!, out var parts)) named[name!] = parts = [];
                parts.Add((order, column, unique));
            }
        }

        foreach (var pair in named.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            yield return new IndexModel(pair.Key, pair.Value.Any(p => p.Unique), new EquatableArray<string>(pair.Value.OrderBy(p => p.Order).Select(p => p.Column)));
        }
    }

    private static bool IsSqliteIndexed(INamedTypeSymbol? attributeClass)
    {
        for (var current = attributeClass; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == SqliteIndexed) return true;
        }

        return false;
    }

    /// <summary>Public read/write instance properties, base class first; an override keeps its base position.</summary>
    private static List<IPropertySymbol> CollectProperties(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType) chain.Add(current);
        chain.Reverse();

        var ordered = new List<IPropertySymbol>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var declaring in chain)
        {
            foreach (var member in declaring.GetMembers())
            {
                if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public } property
                    || property.GetMethod is null
                    || property.SetMethod is not { DeclaredAccessibility: Accessibility.Public })
                {
                    continue;
                }

                if (positions.TryGetValue(property.Name, out var position)) ordered[position] = property;
                else
                {
                    positions[property.Name] = ordered.Count;
                    ordered.Add(property);
                }
            }
        }

        return ordered;
    }

    private static string? Infer(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) type = nullable.TypeArguments[0];
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
        {
            return underlying.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_UInt32 ? "Int64" : "Int32";
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                return "Boolean";
            case SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32:
                return "Int32";
            case SpecialType.System_UInt32 or SpecialType.System_Int64:
                return "Int64";
            case SpecialType.System_Single or SpecialType.System_Double:
                return "Double";
            case SpecialType.System_Decimal:
                return "Decimal";
            case SpecialType.System_String:
                return "String";
            case SpecialType.System_DateTime:
                return "DateTime";
        }

        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }) return "Bytes";
        return type.ToDisplayString() switch
        {
            "System.Guid" => "Guid",
            "System.DateTimeOffset" => "DateTimeOffset",
            "System.TimeSpan" => "TimeSpan",
            _ => null
        };
    }

    /// <summary>
    ///     Value types follow <c>Nullable&lt;T&gt;</c>. Reference types are nullable locally unless declared NotNull: a NULL the server
    ///     sends must never fail an apply, and nullable annotations are not a database contract.
    /// </summary>
    private static bool IsNullable(IPropertySymbol property, AttributeData? column)
    {
        switch (Named(column, "Nullability") is { } value ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0)
        {
            case 1:
                return true;
            case 2:
                return false;
        }

        if (Has(property, SqliteNotNull)) return false;
        return !property.Type.IsValueType || property.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };
    }

    private static string ColumnName(IPropertySymbol property, AttributeData? column) =>
        Named(column, "Name") as string ?? FirstArgument(Find(property, SqliteColumn)) as string ?? property.Name;

    private static string? ExplicitType(AttributeData? column)
    {
        if (Named(column, "Type") is not { } value) return null;
        var index = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        return index > 0 && index < ColumnTypes.Length ? ColumnTypes[index] : null;
    }

    private static AttributeData? Find(ISymbol symbol, string attribute) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attribute);

    private static bool Has(ISymbol symbol, string attribute) => Find(symbol, attribute) is not null;

    private static object? Named(AttributeData? attribute, string name) =>
        attribute?.NamedArguments.FirstOrDefault(a => a.Key == name) is { Key: not null } argument ? argument.Value.Value : null;

    private static object? FirstArgument(AttributeData? attribute) =>
        attribute is { ConstructorArguments.Length: > 0 } ? attribute.ConstructorArguments[0].Value : null;

    private static DiagnosticInfo Info(DiagnosticDescriptor descriptor, LocationInfo? location, params string[] arguments) =>
        new(descriptor.Id, new EquatableArray<string>(arguments), location);
}
