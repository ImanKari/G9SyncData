namespace G9SyncData.Server.EfCore;

/// <summary>
///     Selects which EF Core entity types are synced, and carries the few decisions the EF model cannot express
///     (direction, conflict policy, per-column overrides).
///     <para>
///         Everything else — table and column names, keys, store types, nullability, value converters, indexes — is
///         READ from the EF model rather than restated here. That is the point: a column renamed in the DbContext
///         cannot silently disagree with the sync model, because there is only one declaration of it.
///     </para>
/// </summary>
public sealed class G9CEfCoreSyncBuilder
{
    private readonly List<G9CEfCoreEntityBuilder> _entities = [];

    /// <summary>Schema used when neither the entity nor the model declares one.</summary>
    public string DefaultSchema { get; set; } = "public";

    /// <summary>Direction applied to every entity that does not set its own.</summary>
    public G9ESyncDirection DefaultDirection { get; set; } = G9ESyncDirection.Bidirectional;

    /// <summary>Conflict policy applied to every entity that does not set its own.</summary>
    public G9EConflictPolicy DefaultConflicts { get; set; } = G9EConflictPolicy.ServerWins;

    internal IReadOnlyList<G9CEfCoreEntityBuilder> Entities => _entities;

    /// <summary>Syncs <typeparamref name="TEntity" />. Register parents before children: uploads apply in registration order.</summary>
    public G9CEfCoreSyncBuilder Entity<TEntity>(Action<G9CEfCoreEntityBuilder>? configure = null)
        where TEntity : class
        => Entity(typeof(TEntity), configure);

    /// <inheritdoc cref="Entity{TEntity}" />
    public G9CEfCoreSyncBuilder Entity(Type clrType, Action<G9CEfCoreEntityBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        var builder = new G9CEfCoreEntityBuilder(clrType);
        configure?.Invoke(builder);
        _entities.Add(builder);
        return this;
    }
}

/// <summary>Per-entity overrides on top of what the EF model already says.</summary>
public sealed class G9CEfCoreEntityBuilder
{
    private readonly Dictionary<string, G9CEfCorePropertyBuilder> _properties = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    internal G9CEfCoreEntityBuilder(Type clrType) => ClrType = clrType;

    internal Type ClrType { get; }
    internal G9ESyncDirection? DirectionOverride { get; private set; }
    internal G9EConflictPolicy? ConflictsOverride { get; private set; }
    internal bool? OrderedApplyOverride { get; private set; }
    internal string? WriteRuleSql { get; private set; }
    internal IReadOnlyCollection<string> Excluded => _excluded;
    internal IReadOnlyDictionary<string, G9CEfCorePropertyBuilder> Properties => _properties;

    /// <summary>Keeps these properties (by CLR property name or column name) out of the sync model entirely.</summary>
    public G9CEfCoreEntityBuilder Exclude(params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(propertyNames);
        foreach (var name in propertyNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _excluded.Add(name);
        }

        return this;
    }

    public G9CEfCoreEntityBuilder Direction(G9ESyncDirection direction)
    {
        DirectionOverride = direction;
        return this;
    }

    public G9CEfCoreEntityBuilder Conflicts(G9EConflictPolicy policy)
    {
        ConflictsOverride = policy;
        return this;
    }

    public G9CEfCoreEntityBuilder OrderedApply(bool ordered)
    {
        OrderedApplyOverride = ordered;
        return this;
    }

    /// <summary>An extra predicate over <c>{row}</c> every uploaded row must satisfy before and after the change.</summary>
    public G9CEfCoreEntityBuilder WriteRule(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        WriteRuleSql = sql;
        return this;
    }

    /// <summary>Overrides one property's derived column type or SQL. Use for types the map cannot infer.</summary>
    public G9CEfCoreEntityBuilder Property(string propertyName, Action<G9CEfCorePropertyBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentNullException.ThrowIfNull(configure);
        if (!_properties.TryGetValue(propertyName, out var builder))
        {
            builder = new G9CEfCorePropertyBuilder();
            _properties[propertyName] = builder;
        }

        configure(builder);
        return this;
    }
}

/// <summary>Overrides for a single property.</summary>
public sealed class G9CEfCorePropertyBuilder
{
    internal G9ESyncColumnType? TypeOverride { get; private set; }
    internal string? ReadSql { get; private set; }
    internal string? WriteSql { get; private set; }
    internal bool IsReadOnly { get; private set; }

    /// <summary>Forces the wire type, for a store type the map cannot infer (jsonb, arrays, a custom converter).</summary>
    public G9CEfCorePropertyBuilder As(G9ESyncColumnType columnType)
    {
        TypeOverride = columnType;
        return this;
    }

    /// <summary>Read expression with <c>{row}</c>, e.g. <c>{row}."Location"::text</c>.</summary>
    public G9CEfCorePropertyBuilder ReadAs(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ReadSql = sql;
        return this;
    }

    /// <summary>Write expression with <c>{value}</c>, e.g. <c>{value}::geometry</c>.</summary>
    public G9CEfCorePropertyBuilder WriteAs(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        WriteSql = sql;
        return this;
    }

    /// <summary>Downloaded but never accepted from an upload (a server-owned column).</summary>
    public G9CEfCorePropertyBuilder ReadOnly()
    {
        IsReadOnly = true;
        return this;
    }
}
