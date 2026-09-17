using G9SyncData.Server.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace G9SyncData.Server.EfCore;

/// <summary>
///     Translates an EF Core <see cref="IModel" /> into the G9SyncData server model.
///     <para>
///         Every problem is collected and reported together, and it is reported at SERVER START, not on a device:
///         a model that cannot be translated must break the deployment, never a field sync.
///     </para>
/// </summary>
public static class G9CEfCoreModelExtractor
{
    /// <summary>
    ///     Adds tables to <paramref name="builder" /> from the EF entity types selected in <paramref name="configure" />.
    ///     Subscriptions are declared separately, on the same builder.
    /// </summary>
    /// <exception cref="G9CSyncException">
    ///     Code <see cref="G9SyncErrorCodes.ModelInvalid" />, listing every entity and property that could not be mapped.
    /// </exception>
    public static G9CSyncModelBuilder FromEfCore(this G9CSyncModelBuilder builder, IModel model, Action<G9CEfCoreSyncBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(configure);

        var ef = new G9CEfCoreSyncBuilder();
        configure(ef);

        var errors = new List<string>();
        foreach (var entity in ef.Entities) AddTable(builder, model, ef, entity, errors);

        if (errors.Count > 0)
        {
            throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                "The G9SyncData model cannot be derived from the EF Core model:" + Environment.NewLine
                + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        }

        return builder;
    }

    /// <summary>
    ///     <see cref="FromEfCore(G9CSyncModelBuilder, IModel, Action{G9CEfCoreSyncBuilder})" /> with the model of the application's own
    ///     <typeparamref name="TContext" />, taken from <see cref="IDbContextFactory{TContext}" /> when registered, else from a DI scope -
    ///     so the model is the one the application's contexts use. For the deferred <c>AddG9SyncDataServer((services, model) => …)</c>.
    /// </summary>
    public static G9CSyncModelBuilder FromEfCore<TContext>(this G9CSyncModelBuilder builder, IServiceProvider services, Action<G9CEfCoreSyncBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<IDbContextFactory<TContext>>() is { } factory)
        {
            using var created = factory.CreateDbContext();
            return builder.FromEfCore(created.Model, configure);
        }

        using var scope = services.CreateScope();
        return builder.FromEfCore(scope.ServiceProvider.GetRequiredService<TContext>().Model, configure);
    }

    private static void AddTable(G9CSyncModelBuilder builder, IModel model, G9CEfCoreSyncBuilder ef,
        G9CEfCoreEntityBuilder entity, List<string> errors)
    {
        var clrName = entity.ClrType.Name;
        var entityType = model.FindEntityType(entity.ClrType);
        if (entityType is null)
        {
            errors.Add($"'{clrName}' is not part of the EF Core model.");
            return;
        }

        if (entityType.IsOwned())
        {
            errors.Add($"'{clrName}' is an OWNED entity type: its columns belong to the owner's row. Sync the owner instead.");
            return;
        }

        var tableName = entityType.GetTableName();
        if (string.IsNullOrEmpty(tableName))
        {
            errors.Add($"'{clrName}' is not mapped to a table (it may be a view, a TPC fragment or keyless).");
            return;
        }

        var schema = entityType.GetSchema() ?? model.GetDefaultSchema() ?? ef.DefaultSchema;
        var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema() ?? model.GetDefaultSchema());

        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is null)
        {
            errors.Add($"'{clrName}' has no primary key.");
            return;
        }

        if (primaryKey.Properties.Count != 1)
        {
            var columns = string.Join(", ", primaryKey.Properties.Select(p => p.Name));
            errors.Add($"'{clrName}' has a composite primary key ({columns}); G9SyncData requires a single-column key.");
            return;
        }

        var keyProperty = primaryKey.Properties[0];
        var keyColumn = keyProperty.GetColumnName(storeObject) ?? keyProperty.Name;
        if (!G9CEfCoreTypeMap.TryMap(G9CEfCoreTypeMap.EffectiveClrType(keyProperty), out var keyType))
        {
            errors.Add($"'{clrName}.{keyProperty.Name}' is the key but its type '{G9CEfCoreTypeMap.EffectiveClrType(keyProperty).Name}' has no sync mapping.");
            return;
        }

        var tableErrorsBefore = errors.Count;
        var columnsToAdd = new List<Action<G9CSyncTableBuilder>>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { keyColumn };
        var table = new TableContext(model.GetRelationalModel().FindTable(tableName, entityType.GetSchema() ?? model.GetDefaultSchema()), storeObject, entity,
            columnsToAdd, seen, errors);

        foreach (var property in entityType.GetProperties())
        {
            if (property == keyProperty) continue;
            AddScalarColumn(table, property, property.Name, clrName);
        }

        // Owned types and complex properties keep columns in this row too (LES-0034): a JSON document column, or the columns of a
        // type split into this table. Reading only scalar properties dropped them silently - AgriPad's pilot found AttachmentCount,
        // a NOT NULL jsonb column, missing from the sync model of every attachable entity.
        AddStructuralColumns(table, entityType, string.Empty, clrName, tableName, entityType.GetSchema() ?? model.GetDefaultSchema());

        if (errors.Count > tableErrorsBefore) return;

        builder.Table(schema, tableName, table =>
        {
            table.Key(keyColumn, keyType)
                .Direction(entity.DirectionOverride ?? ef.DefaultDirection)
                .Conflicts(entity.ConflictsOverride ?? ef.DefaultConflicts);

            if (entity.OrderedApplyOverride is { } ordered) table.OrderedApply(ordered);
            if (entity.WriteRuleSql is { } writeRule) table.WriteRule(writeRule);

            foreach (var add in columnsToAdd) add(table);
        });
    }

    /// <summary>What every column of one table is added with.</summary>
    private sealed record TableContext(ITable? Table, StoreObjectIdentifier StoreObject, G9CEfCoreEntityBuilder Entity,
        List<Action<G9CSyncTableBuilder>> Columns, HashSet<string> Seen, List<string> Errors)
    {
        /// <summary>
        ///     The database column's nullability when the relational model knows it: an optional owned or complex type makes the
        ///     columns of its NOT NULL properties nullable.
        /// </summary>
        public bool IsNullable(string columnName, bool fallback) => Table?.FindColumn(columnName)?.IsNullable ?? fallback;

        /// <summary>Excluded by CLR member path (<c>Note</c>, <c>Address.City</c>) or by column name.</summary>
        public bool IsExcluded(string memberPath, string columnName) => Entity.Excluded.Contains(memberPath) || Entity.Excluded.Contains(columnName);

        public G9CEfCorePropertyBuilder? Overrides(string memberPath, string columnName) =>
            Entity.Properties.TryGetValue(memberPath, out var byMember) ? byMember : Entity.Properties.GetValueOrDefault(columnName);

        public void Add(string columnName, G9ESyncColumnType columnType, bool nullable, string? readSql, string? writeSql, bool readOnly) =>
            Columns.Add(table => table.Column(columnName, columnType, nullable, column =>
            {
                if (readSql is not null) column.ReadAs(readSql);
                if (writeSql is not null) column.WriteAs(writeSql);
                if (readOnly) column.ReadOnly();
            }));
    }

    /// <summary>One column for a scalar property; <paramref name="memberPath" /> is <c>Note</c>, or <c>Address.City</c> inside an owned/complex type.</summary>
    private static void AddScalarColumn(TableContext table, IProperty property, string memberPath, string clrName)
    {
        var columnName = property.GetColumnName(table.StoreObject) ?? property.Name;
        if (table.IsExcluded(memberPath, columnName)) return;
        if (!table.Seen.Add(columnName)) return;   // a column two mappings share (e.g. an owned type's copy of the key) is synced once

        var overrides = table.Overrides(memberPath, columnName);
        var isSpatial = G9CEfCoreTypeMap.IsSpatial(property);
        G9ESyncColumnType columnType;
        if (overrides?.TypeOverride is { } forced)
        {
            columnType = forced;
        }
        else if (isSpatial)
        {
            // PostGIS values travel as text (WKB hex) and are cast back on write - the same shape the
            // current Dotmim path uses, so the device's stored representation does not change.
            columnType = G9ESyncColumnType.String;
        }
        else if (!G9CEfCoreTypeMap.TryMap(G9CEfCoreTypeMap.EffectiveClrType(property), out columnType))
        {
            table.Errors.Add(
                $"'{clrName}.{memberPath}' maps to store type '{property.GetColumnType()}' / CLR type "
                + $"'{G9CEfCoreTypeMap.EffectiveClrType(property).Name}', which has no sync mapping. "
                + $"Give it an explicit type with Property(\"{memberPath}\", p => p.As(...)) or Exclude it.");
            return;
        }

        var readOnly = overrides?.IsReadOnly == true || IsStoreGenerated(property);
        var readSql = overrides?.ReadSql ?? (isSpatial ? $"{{row}}.\"{columnName}\"::text" : null);
        var writeSql = overrides?.WriteSql ?? (isSpatial ? "{value}::geometry" : null);
        table.Add(columnName, columnType, table.IsNullable(columnName, property.IsNullable), readSql, writeSql, readOnly);
    }

    /// <summary>
    ///     Columns an owner's row holds for its owned types and complex properties:
    ///     <list type="bullet">
    ///         <item>a type mapped to JSON (<c>OwnsOne(...).ToJson()</c>, <c>ComplexProperty(...).ToJson()</c>) is ONE text column
    ///         carrying the document, cast back to its store type on write;</item>
    ///         <item>a type split into the owner's table contributes its own columns (recursively);</item>
    ///         <item>an owned type in a table of its own is a separate table and is not part of this row.</item>
    ///     </list>
    /// </summary>
    private static void AddStructuralColumns(TableContext table, ITypeBase owner, string pathPrefix, string clrName, string tableName, string? schema)
    {
        if (owner is IEntityType ownerEntity)
        {
            foreach (var navigation in ownerEntity.GetNavigations())
            {
                if (navigation.IsOnDependent || !navigation.ForeignKey.IsOwnership) continue;
                var owned = navigation.TargetEntityType;
                var path = pathPrefix + navigation.Name;
                if (owned.IsMappedToJson())
                {
                    AddJsonColumn(table, path, owned.GetContainerColumnName());
                }
                else if (string.Equals(owned.GetTableName(), tableName, StringComparison.Ordinal) && string.Equals(owned.GetSchema(), schema, StringComparison.Ordinal))
                {
                    foreach (var property in owned.GetProperties())
                    {
                        if (property.IsPrimaryKey()) continue;   // the owner's key, shared
                        AddScalarColumn(table, property, path + "." + property.Name, clrName);
                    }

                    AddStructuralColumns(table, owned, path + ".", clrName, tableName, schema);
                }
            }
        }

        foreach (var complex in owner.GetComplexProperties())
        {
            var path = pathPrefix + complex.Name;
            if (complex.ComplexType.IsMappedToJson())
            {
                AddJsonColumn(table, path, complex.ComplexType.GetContainerColumnName());
                continue;
            }

            foreach (var property in complex.ComplexType.GetProperties()) AddScalarColumn(table, property, path + "." + property.Name, clrName);
            AddStructuralColumns(table, complex.ComplexType, path + ".", clrName, tableName, schema);
        }
    }

    private static void AddJsonColumn(TableContext table, string memberPath, string? containerColumn)
    {
        if (string.IsNullOrEmpty(containerColumn) || table.IsExcluded(memberPath, containerColumn) || !table.Seen.Add(containerColumn)) return;

        var overrides = table.Overrides(memberPath, containerColumn);
        var storeType = table.Table?.FindColumn(containerColumn)?.StoreType ?? "jsonb";

        // Text on the wire (the device stores the document as TEXT, as Dotmim did); cast back to json/jsonb on write.
        table.Add(containerColumn, overrides?.TypeOverride ?? G9ESyncColumnType.String, table.IsNullable(containerColumn, fallback: true),
            overrides?.ReadSql ?? $"{{row}}.\"{containerColumn}\"::text", overrides?.WriteSql ?? $"{{value}}::{storeType}", overrides?.IsReadOnly == true);
    }

    /// <summary>
    ///     True when the database produces the value, so an upload must never be allowed to set it: computed columns,
    ///     identity/serial columns and rowversion-style concurrency tokens.
    /// </summary>
    private static bool IsStoreGenerated(IProperty property)
        => property.GetComputedColumnSql() is not null
           || property.ValueGenerated is ValueGenerated.OnAddOrUpdate or ValueGenerated.OnUpdate
           || (property.IsConcurrencyToken && property.ClrType == typeof(byte[]));

    /// <summary>
    ///     The indexes EF declares for the given entity types, so the device can create the same ones.
    ///     <para>
    ///         This exists because the engine being replaced created none: AgriPad had to add eighteen foreign-key
    ///         indexes by hand in client migrations 1.0.0.14 and 1.0.0.15 after measuring a 49.8 ms query that an
    ///         index took to 6.7 ms. Reading them off the model means a new index ships with the schema.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<G9CEfCoreTableIndex> ExtractIndexes(IModel model, IEnumerable<Type> clrTypes)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(clrTypes);

        var result = new List<G9CEfCoreTableIndex>();
        foreach (var clrType in clrTypes)
        {
            var entityType = model.FindEntityType(clrType);
            var tableName = entityType?.GetTableName();
            if (entityType is null || string.IsNullOrEmpty(tableName)) continue;

            var schema = entityType.GetSchema() ?? model.GetDefaultSchema() ?? "public";
            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema() ?? model.GetDefaultSchema());

            foreach (var index in entityType.GetIndexes())
            {
                var columns = index.Properties
                    .Select(p => p.GetColumnName(storeObject) ?? p.Name)
                    .ToArray();

                result.Add(new G9CEfCoreTableIndex(
                    schema + "." + tableName,
                    index.GetDatabaseName(storeObject) ?? "IX_" + tableName + "_" + string.Join("_", columns),
                    columns,
                    index.IsUnique,
                    index.GetFilter(storeObject)));
            }
        }

        return result;
    }
}

/// <summary>One index read off the EF model, ready to be created on the device.</summary>
/// <param name="LogicalName"><c>schema.table</c>.</param>
/// <param name="Name">The index name EF would use on the server.</param>
/// <param name="Columns">Indexed columns, in order.</param>
/// <param name="IsUnique">Whether the index is unique.</param>
/// <param name="Filter">The partial-index predicate, or null. A filtered unique index is also a claim key - see the replant post-mortem.</param>
public sealed record G9CEfCoreTableIndex(
    string LogicalName,
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    string? Filter);
