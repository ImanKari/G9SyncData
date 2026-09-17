using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace G9SyncData.Server.EfCore;

/// <summary>
///     Writes uploaded rows through the application's <see cref="DbContext" /> (ADR-0025 row path): entities are loaded and
///     tracked, only the columns the conflict policy decided to apply are set, and <c>SaveChangesAsync</c> runs — so the
///     application's SaveChanges override, interceptors, audit stamping and validation apply to device writes exactly as to
///     writes made by the application itself.
///     <para>
///         The context is bound to the push connection and transaction (<c>SetDbConnection</c> + <c>UseTransactionAsync</c>);
///         nothing is committed or rolled back by EF. Entities are loaded with <c>IgnoreQueryFilters()</c>: read filters decide
///         what a device may SEE; what it may WRITE is decided by the store's write authorisation, which still runs.
///     </para>
///     <para>
///         ⚠ A context configured with a retrying execution strategy (<c>EnableRetryOnFailure</c>) rejects user transactions.
///         Give the writer a context registration without it (e.g. a separate <c>IDbContextFactory</c> options), or the first
///         push fails with a clear error.
///     </para>
/// </summary>
public sealed class G9CEfCoreRowWriter<TContext> : IG9RowWriter
    where TContext : DbContext
{
    private readonly Dictionary<Type, G9CEfCoreWriteEntity> _entities;
    private readonly ConcurrentDictionary<string, EntityPlan> _plans = new(StringComparer.Ordinal);
    private readonly HashSet<string> _logicalNames = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _resolved;

    internal G9CEfCoreRowWriter(IEnumerable<G9CEfCoreWriteEntity> entities) => _entities = entities.ToDictionary(e => e.ClrType);

    public async ValueTask PrepareAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (_resolved) return;
        await using var bound = await G9CEfCoreBoundContext<TContext>.OpenAsync(services, null, null, cancellationToken).ConfigureAwait(false);
        ResolveNames(bound.Db.Model);
    }

    public bool AppliesTo(G9CSyncServerTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        // Never guess: an unprepared writer that answered "no" would silently leave uploads to the SQL path.
        if (!_resolved) throw new InvalidOperationException($"{GetType().Name} was asked which tables it writes before PrepareAsync resolved the EF model.");
        return _logicalNames.Contains(table.LogicalName);
    }

    public async ValueTask<IReadOnlyList<string>> ValidateAsync(G9CSyncServerTable table, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(services);
        await using var bound = await G9CEfCoreBoundContext<TContext>.OpenAsync(services, null, null, cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        _ = Plan(bound.Db.Model, table, problems);
        return problems;
    }

    public async ValueTask WriteAsync(G9CRowWriteContext context, IReadOnlyList<G9CRowWrite> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return;

        await using var bound = await G9CEfCoreBoundContext<TContext>.OpenAsync(context.Services, context.Connection, context.Transaction, cancellationToken)
            .ConfigureAwait(false);
        var db = bound.Db;
        var problems = new List<string>();
        var plan = Plan(db.Model, context.Table, problems)
                   ?? throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid, string.Join("; ", problems));

        var existing = await plan.LoadAsync(db, rows.Where(r => r.Exists || r.Op == G9ERowWriteOp.Delete).Select(r => plan.ConvertKey(r.KeyValue)).ToList(),
            cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var key = plan.ConvertKey(row.KeyValue);
            existing.TryGetValue(key, out var entity);
            if (row.Op == G9ERowWriteOp.Delete)
            {
                if (entity is not null) db.Remove(entity);
                continue;
            }

            if (entity is null)
            {
                entity = plan.CreateInstance();
                db.Entry(entity).Property(plan.Key.Name).CurrentValue = key;

                // Add, not Entry(entity).State = Added: Add walks the graph, so owned instances the constructor created (a JSON
                // document's default, e.g. Agriwise's AttachmentCount) are tracked and saved with the row. Setting the state tracks
                // the root only, and the owned column is written as NULL (LES-0034).
                db.Add(entity);
                Apply(plan, db.Entry(entity), row);
            }
            else
            {
                Apply(plan, db.Entry(entity), row);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    private static void Apply(EntityPlan plan, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, G9CRowWrite row)
    {
        foreach (var (column, value) in row.Values)
        {
            if (!plan.Columns.TryGetValue(column, out var target)) continue;   // validated at startup; a read-only column is skipped
            object? converted;
            try
            {
                converted = target.Convert(value, row.Key, column);
            }
            catch (Exception exception) when (exception is not (G9CRowRejectedException or OperationCanceledException))
            {
                // A value this row carries cannot become the property (malformed JSON for an owned document, a number out of range,
                // an application converter refusing it): the ROW is wrong, not the push. Rejected alone; the other rows are written.
                throw new G9CRowRejectedException(G9SyncErrorCodes.RejectValidation,
                    $"Column \"{column}\" of row {row.Key} cannot be converted: {exception.Message}");
            }

            switch (target.Member)
            {
                case IProperty property when target.ComplexPath.Count > 0:
                    var complexEntry = entry.ComplexProperty(target.ComplexPath[0].Name);
                    for (var i = 1; i < target.ComplexPath.Count; i++) complexEntry = complexEntry.ComplexProperty(target.ComplexPath[i].Name);
                    complexEntry.Property(property.Name).CurrentValue = converted;
                    break;
                case IProperty property:
                    entry.Property(property.Name).CurrentValue = converted;
                    break;
                case INavigation { IsCollection: true } collection:
                    entry.Collection(collection.Name).CurrentValue = (IEnumerable<object>?)converted;
                    break;
                case INavigation reference:
                    entry.Reference(reference.Name).CurrentValue = converted;
                    break;
                case IComplexProperty complex:
                    entry.ComplexProperty(complex.Name).CurrentValue = converted;
                    break;
            }
        }
    }

    private EntityPlan? Plan(IModel model, G9CSyncServerTable table, List<string> problems)
    {
        if (!_resolved) ResolveNames(model);
        if (_plans.TryGetValue(table.LogicalName, out var cached)) return cached;

        var entityType = _entities.Keys.Select(model.FindEntityType).FirstOrDefault(e =>
            e is not null && string.Equals(e.GetTableName(), table.Name, StringComparison.Ordinal)
                          && string.Equals(e.GetSchema() ?? model.GetDefaultSchema() ?? "public", table.Schema, StringComparison.Ordinal));
        if (entityType is null)
        {
            problems.Add($"no configured entity of {typeof(TContext).Name} maps to {table.LogicalName}");
            return null;
        }

        var configuration = _entities[entityType.ClrType];
        var store = StoreObjectIdentifier.Table(table.Name, entityType.GetSchema() ?? model.GetDefaultSchema());
        var key = entityType.FindPrimaryKey() is { Properties.Count: 1 } primary ? primary.Properties[0] : null;
        if (key is null || !string.Equals(key.GetColumnName(store) ?? key.Name, table.KeyColumn, StringComparison.Ordinal))
        {
            problems.Add($"{entityType.ClrType.Name} must have a single-column key mapped to \"{table.KeyColumn}\"");
            return null;
        }

        var columns = new Dictionary<string, ColumnPlan>(StringComparer.Ordinal);
        foreach (var column in table.PhysicalColumns)
        {
            if (column.IsReadOnly) continue;
            configuration.Converters.TryGetValue(column.Name, out var custom);
            var property = entityType.GetProperties().FirstOrDefault(p => string.Equals(p.GetColumnName(store) ?? p.Name, column.Name, StringComparison.Ordinal));
            if (property is null)
            {
                // A JSON document column belongs to an owned navigation or a complex property, not to a scalar property (LES-0034).
                if (FindJsonMember(entityType, column.Name) is { } document)
                {
                    if (custom is null)
                    {
                        problems.Add($"column \"{column.Name}\" is the JSON document of {entityType.ClrType.Name}.{document.Name}; give it a converter with " +
                                     $"Convert(\"{column.Name}\", json => …) returning a {document.ClrType.Name}, or make it read-only when the server owns it " +
                                     $"(Property(\"{document.Name}\", p => p.ReadOnly()))");
                        continue;
                    }

                    columns[column.Name] = new ColumnPlan(document, custom, []);
                    continue;
                }

                if (FindComplexScalar(entityType, column.Name, store, []) is { } nested)
                {
                    if (custom is null && column.WriteSql is not null)
                    {
                        problems.Add($"column \"{column.Name}\" is written with SQL ({column.WriteSql}), which EF Core cannot run; " +
                                     $"give it a converter with Convert(\"{column.Name}\", value => …)");
                        continue;
                    }

                    columns[column.Name] = new ColumnPlan(nested.Property, custom, nested.Path);
                    continue;
                }

                problems.Add($"column \"{column.Name}\" has no property on {entityType.ClrType.Name} (columns of an owned type split into the table " +
                             "cannot be written through EF Core: exclude them or make them read-only)");
                continue;
            }

            if (custom is null && column.WriteSql is not null)
            {
                problems.Add($"column \"{column.Name}\" is written with SQL ({column.WriteSql}), which EF Core cannot run; " +
                             $"give it a converter with Convert(\"{column.Name}\", value => …)");
                continue;
            }

            columns[column.Name] = new ColumnPlan(property, custom, []);
        }

        if (problems.Count > 0) return null;
        // Nested in a generic class, so the definition also carries TContext.
        var plan = (EntityPlan)Activator.CreateInstance(typeof(EntityPlan<,>).MakeGenericType(typeof(TContext), entityType.ClrType, key.ClrType), key, columns)!;
        _plans[table.LogicalName] = plan;
        return plan;
    }

    /// <summary>A scalar property of a complex type split into the table (<c>Dimensions_Width</c>), with the complex properties leading to it.</summary>
    private static (IProperty Property, IReadOnlyList<IComplexProperty> Path)? FindComplexScalar(ITypeBase owner, string column, StoreObjectIdentifier store,
        IReadOnlyList<IComplexProperty> path)
    {
        foreach (var complex in owner.GetComplexProperties())
        {
            if (complex.ComplexType.IsMappedToJson()) continue;
            var here = path.Append(complex).ToArray();
            var property = complex.ComplexType.GetProperties().FirstOrDefault(p => string.Equals(p.GetColumnName(store) ?? p.Name, column, StringComparison.Ordinal));
            if (property is not null) return (property, here);
            if (FindComplexScalar(complex.ComplexType, column, store, here) is { } deeper) return deeper;
        }

        return null;
    }

    /// <summary>The owned navigation or complex property whose JSON document is stored in <paramref name="column" />, if any.</summary>
    private static IPropertyBase? FindJsonMember(IEntityType entityType, string column)
    {
        foreach (var navigation in entityType.GetNavigations())
        {
            if (!navigation.IsOnDependent && navigation.ForeignKey.IsOwnership && navigation.TargetEntityType.IsMappedToJson()
                && string.Equals(navigation.TargetEntityType.GetContainerColumnName(), column, StringComparison.Ordinal))
            {
                return navigation;
            }
        }

        return entityType.GetComplexProperties().FirstOrDefault(c =>
            c.ComplexType.IsMappedToJson() && string.Equals(c.ComplexType.GetContainerColumnName(), column, StringComparison.Ordinal));
    }

    private void ResolveNames(IModel model)
    {
        lock (_logicalNames)
        {
            if (_resolved) return;
            foreach (var clr in _entities.Keys)
            {
                var entityType = model.FindEntityType(clr);
                if (entityType?.GetTableName() is { } name) _logicalNames.Add((entityType.GetSchema() ?? model.GetDefaultSchema() ?? "public") + "." + name);
            }

            _resolved = true;
        }
    }

    /// <summary>
    ///     How one column is written: to a scalar property (of the entity, or of a complex type reached through
    ///     <c>ComplexPath</c>), or - with a converter - to the owned navigation or complex property of a JSON document column.
    /// </summary>
    private sealed record ColumnPlan(IPropertyBase Member, Func<object?, object?>? Custom, IReadOnlyList<IComplexProperty> ComplexPath)
    {
        private IProperty Property => (IProperty)Member;

        public object? Convert(object? value, string rowKey, string column)
        {
            if (Custom is not null) return Custom(value);
            if (value is null)
            {
                if (!Property.IsNullable)
                {
                    throw new G9CRowRejectedException(G9SyncErrorCodes.RejectValidation, $"Column \"{column}\" of row {rowKey} cannot be null.");
                }

                return null;
            }

            var target = Nullable.GetUnderlyingType(Property.ClrType) ?? Property.ClrType;
            if (target.IsInstanceOfType(value)) return value;
            if (Property.GetValueConverter() is { } converter)
            {
                var provider = Nullable.GetUnderlyingType(converter.ProviderClrType) ?? converter.ProviderClrType;
                return converter.ConvertFromProvider(provider.IsInstanceOfType(value) ? value : ChangeType(value, provider));
            }

            return ChangeType(value, target);
        }

        private static object ChangeType(object value, Type target)
        {
            if (target.IsEnum) return value is string text ? Enum.Parse(target, text) : Enum.ToObject(target, System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
            if (target == typeof(Guid)) return value is string guid ? Guid.Parse(guid) : value;
            if (target == typeof(DateTimeOffset) && value is DateTime dateTime) return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc));
            if (target == typeof(DateOnly) && value is DateTime date) return DateOnly.FromDateTime(date);
            if (target == typeof(TimeOnly) && value is TimeSpan time) return TimeOnly.FromTimeSpan(time);
            return System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
    }

    private abstract class EntityPlan(IProperty key, IReadOnlyDictionary<string, ColumnPlan> columns)
    {
        public IProperty Key { get; } = key;
        public IReadOnlyDictionary<string, ColumnPlan> Columns { get; } = columns;

        public abstract object CreateInstance();

        public abstract object ConvertKey(object keyValue);

        public abstract Task<Dictionary<object, object>> LoadAsync(DbContext db, IReadOnlyList<object> keys, CancellationToken cancellationToken);
    }

    private sealed class EntityPlan<TEntity, TKey>(IProperty key, IReadOnlyDictionary<string, ColumnPlan> columns) : EntityPlan(key, columns)
        where TEntity : class
        where TKey : notnull
    {
        private static readonly ConstructorInfo? Constructor =
            typeof(TEntity).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);

        public override object CreateInstance() =>
            Constructor?.Invoke(null) ?? throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                $"{typeof(TEntity).Name} needs a parameterless constructor (it may be private) to be created from an upload.");

        public override object ConvertKey(object keyValue) =>
            keyValue is TKey typed ? typed : (TKey)System.Convert.ChangeType(keyValue, typeof(TKey), CultureInfo.InvariantCulture);

        public override async Task<Dictionary<object, object>> LoadAsync(DbContext db, IReadOnlyList<object> keys, CancellationToken cancellationToken)
        {
            var result = new Dictionary<object, object>();
            if (keys.Count == 0) return result;
            var typed = keys.Cast<TKey>().Distinct().ToList();
            var row = Expression.Parameter(typeof(TEntity), "e");
            var keyAccess = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(TKey)], row, Expression.Constant(Key.Name));
            var contains = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(TKey)], Expression.Constant(typed), keyAccess);
            var filter = Expression.Lambda<Func<TEntity, bool>>(contains, row);

            var getter = Key.GetGetter();
            foreach (var entity in await db.Set<TEntity>().IgnoreQueryFilters().Where(filter).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                result[getter.GetClrValue(entity)!] = entity;
            }

            return result;
        }
    }
}

/// <summary>Per-entity options of the EF Core row writer.</summary>
public sealed class G9CEfCoreWriteEntity
{
    internal G9CEfCoreWriteEntity(Type clrType) => ClrType = clrType;

    internal Type ClrType { get; }

    internal Dictionary<string, Func<object?, object?>> Converters { get; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Converts the uploaded value of <paramref name="column" /> to the property's value — required for columns the SQL store
    ///     writes with a cast (<c>WriteAs("{value}::geometry")</c>), e.g. WKB hex text to a NetTopologySuite geometry.
    /// </summary>
    public G9CEfCoreWriteEntity Convert(string column, Func<object?, object?> converter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(converter);
        Converters[column] = converter;
        return this;
    }
}

/// <summary>Selects the entities whose uploads go through the EF Core row writer.</summary>
public sealed class G9CEfCoreWriteBuilder
{
    internal List<G9CEfCoreWriteEntity> Entities { get; } = [];

    public G9CEfCoreWriteBuilder Entity<TEntity>(Action<G9CEfCoreWriteEntity>? configure = null)
        where TEntity : class
    {
        var entity = new G9CEfCoreWriteEntity(typeof(TEntity));
        configure?.Invoke(entity);
        Entities.Add(entity);
        return this;
    }
}

public static class G9CEfCoreServerBuilderExtensions
{
    /// <summary>
    ///     Writes uploads of the selected entities through <typeparamref name="TContext" />.SaveChangesAsync (ADR-0025). Validated at
    ///     server start: every uploaded column must map to a property, and columns written with SQL need a converter.
    /// </summary>
    public static G9CSyncServerBuilder WriteThroughEfCore<TContext>(this G9CSyncServerBuilder builder, Action<G9CEfCoreWriteBuilder> configure)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var entities = new G9CEfCoreWriteBuilder();
        configure(entities);
        return builder.AddRowWriter(new G9CEfCoreRowWriter<TContext>(entities.Entities));
    }
}
