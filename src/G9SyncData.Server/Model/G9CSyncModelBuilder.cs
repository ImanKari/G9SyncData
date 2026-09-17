using System.Globalization;
using System.Text;

namespace G9SyncData.Server.Model;

/// <summary>
///     Fluent builder for the server sync model. <see cref="Build" /> validates everything that can be validated
///     without a database; the provider validates the rest (columns exist, SQL compiles) at server boot, so a bad
///     model fails the SERVER, never a device sync (P-07).
/// </summary>
public sealed class G9CSyncModelBuilder
{
    /// <summary>Physical columns per table are limited by the 64-bit change mask (one bit reserved).</summary>
    public const int MaxPhysicalColumns = 62;

    private readonly List<G9CSyncTableBuilder> _tables = [];
    private readonly List<G9CSyncSubscriptionBuilder> _subscriptions = [];

    /// <summary>Registers a table. Register parents before children: uploads apply in registration order.</summary>
    public G9CSyncModelBuilder Table(string schema, string name, Action<G9CSyncTableBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new G9CSyncTableBuilder(schema, name);
        configure(builder);
        _tables.Add(builder);
        return this;
    }

    public G9CSyncModelBuilder Subscription(string name, Action<G9CSyncSubscriptionBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new G9CSyncSubscriptionBuilder(name);
        configure(builder);
        _subscriptions.Add(builder);
        return this;
    }

    /// <exception cref="G9CSyncException">With code <see cref="G9SyncErrorCodes.ModelInvalid" /> listing every problem.</exception>
    public G9CSyncServerModel Build()
    {
        var errors = new List<string>();
        var tables = new List<G9CSyncServerTable>();
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < _tables.Count; i++)
        {
            var table = _tables[i].Build(i, errors);
            if (table is null) continue;
            if (!tableNames.Add(table.LogicalName)) errors.Add($"Table '{table.LogicalName}' is registered twice.");
            tables.Add(table);
        }

        var byName = tables.GroupBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var subscriptions = new List<G9CSyncSubscriptionDefinition>();
        var subscriptionNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var builder in _subscriptions)
        {
            var subscription = builder.Build(byName, errors);
            if (subscription is null) continue;
            if (!subscriptionNames.Add(subscription.Name)) errors.Add($"Subscription '{subscription.Name}' is registered twice.");
            subscriptions.Add(subscription);
        }

        ValidateDependencies(subscriptions, errors);

        if (errors.Count > 0)
        {
            throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                "The G9SyncData server model is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        }

        return new G9CSyncServerModel(tables, subscriptions);
    }

    private static void ValidateDependencies(List<G9CSyncSubscriptionDefinition> subscriptions, List<string> errors)
    {
        var byName = subscriptions.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var subscription in subscriptions)
        {
            foreach (var dependency in subscription.DependsOn)
            {
                if (!byName.TryGetValue(dependency.Name, out var parent))
                {
                    errors.Add($"Subscription '{subscription.Name}' depends on unknown subscription '{dependency.Name}'.");
                }
                else if (parent.ClientParameters.Count > 0)
                {
                    errors.Add($"Subscription '{subscription.Name}' depends on '{parent.Name}', which takes client parameters. Dependencies must be parameterless.");
                }
            }
        }

        // cycle detection
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        bool Visit(string name, Stack<string> path)
        {
            if (state.TryGetValue(name, out var s))
            {
                if (s == 1)
                {
                    errors.Add("Subscription dependency cycle: " + string.Join(" -> ", path.Reverse()) + " -> " + name);
                    return false;
                }

                return true;
            }

            state[name] = 1;
            path.Push(name);
            if (byName.TryGetValue(name, out var subscription))
            {
                foreach (var dependency in subscription.DependsOn)
                {
                    if (byName.ContainsKey(dependency.Name) && !Visit(dependency.Name, path)) return false;
                }
            }

            path.Pop();
            state[name] = 2;
            return true;
        }

        foreach (var subscription in subscriptions)
        {
            if (!Visit(subscription.Name, new Stack<string>())) break;
        }
    }
}

/// <summary>Configures one table.</summary>
public sealed class G9CSyncTableBuilder
{
    private readonly string _schema;
    private readonly string _name;
    private readonly List<G9CSyncColumnBuilder> _columns = [];
    private string? _keyColumn;
    private G9ESyncColumnType _keyType;
    private G9ESyncDirection _direction = G9ESyncDirection.Bidirectional;
    private G9EConflictPolicy _conflictPolicy = G9EConflictPolicy.ServerWins;
    private bool? _orderedApply;
    private string? _writeRule;
    private G9CConflictRulesBuilder? _conflictRules;

    internal G9CSyncTableBuilder(string schema, string name)
    {
        _schema = schema;
        _name = name;
    }

    /// <summary>The single-column primary key.</summary>
    public G9CSyncTableBuilder Key(string column, G9ESyncColumnType type = G9ESyncColumnType.Guid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        _keyColumn = column;
        _keyType = type;
        return this;
    }

    /// <summary>A physical column.</summary>
    public G9CSyncTableBuilder Column(string name, G9ESyncColumnType type, bool nullable = true, Action<G9CSyncColumnBuilder>? configure = null)
    {
        var column = new G9CSyncColumnBuilder(name, type, nullable, G9ESyncColumnKind.Physical, null);
        configure?.Invoke(column);
        _columns.Add(column);
        return this;
    }

    /// <summary>
    ///     A computed column: <paramref name="readSql" /> (with <c>{row}</c>) is evaluated on every read and folded into the
    ///     reconciliation hash, so when its value flips (e.g. an authorisation flag) reconciliation re-sends the row.
    ///     Also the way to keep serving a column that was removed from the table: <c>Computed("Type", Int32, "0")</c>.
    /// </summary>
    public G9CSyncTableBuilder Computed(string name, G9ESyncColumnType type, string readSql, bool nullable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(readSql);
        _columns.Add(new G9CSyncColumnBuilder(name, type, nullable, G9ESyncColumnKind.Computed, readSql));
        return this;
    }

    public G9CSyncTableBuilder Direction(G9ESyncDirection direction)
    {
        _direction = direction;
        return this;
    }

    public G9CSyncTableBuilder Conflicts(G9EConflictPolicy policy)
    {
        _conflictPolicy = policy;
        return this;
    }

    /// <summary>
    ///     Ordered conflict rules (A-05): the first rule whose condition holds decides what happens to a conflicting upload; the rest
    ///     fall back to <see cref="Conflicts" />. A table with rules tracks per-column versions, so rules see exact column facts.
    /// </summary>
    public G9CSyncTableBuilder OnConflict(Action<G9CConflictRulesBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _conflictRules ??= new G9CConflictRulesBuilder();
        configure(_conflictRules);
        return this;
    }

    /// <summary>Forces (true) or forbids (false) device-order row-by-row apply. Default: automatic.</summary>
    public G9CSyncTableBuilder OrderedApply(bool ordered)
    {
        _orderedApply = ordered;
        return this;
    }

    /// <summary>An extra predicate over <c>{row}</c> that every uploaded row must satisfy before and after the change.</summary>
    public G9CSyncTableBuilder WriteRule(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        _writeRule = sql;
        return this;
    }

    internal G9CSyncServerTable? Build(int order, List<string> errors)
    {
        var logical = _schema + "." + _name;
        var before = errors.Count;
        if (_keyColumn is null) errors.Add($"Table '{logical}' has no Key(...).");
        else if (!G9CSyncKey.IsSupportedKeyType(_keyType)) errors.Add($"Table '{logical}' key type {_keyType} is not supported (Guid, String, Int32, Int64).");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_keyColumn is not null) names.Add(_keyColumn);
        foreach (var column in _columns)
        {
            if (!names.Add(column.Name)) errors.Add($"Table '{logical}' declares column '{column.Name}' twice (or as the key).");
            if (column.Type == G9ESyncColumnType.Unspecified || !Enum.IsDefined(column.Type)) errors.Add($"Table '{logical}' column '{column.Name}' has no valid type.");
        }

        var physical = _columns.Count(c => c.Kind == G9ESyncColumnKind.Physical);
        if (physical > G9CSyncModelBuilder.MaxPhysicalColumns)
        {
            errors.Add($"Table '{logical}' has {physical} physical columns; the maximum is {G9CSyncModelBuilder.MaxPhysicalColumns}.");
        }

        var rules = _conflictRules?.Build(logical, errors);
        if (rules is not null && _conflictPolicy == G9EConflictPolicy.Custom)
        {
            errors.Add($"Table '{logical}' combines conflict rules with the Custom policy; express the resolver as a rule's Decide(...) instead.");
        }

        if (errors.Count > before) return null;
        return new G9CSyncServerTable(_schema, _name, _keyColumn!, _keyType, _columns.Select(c => c.Build()).ToArray(),
            _direction, _conflictPolicy, _orderedApply, _writeRule, order) { ConflictRules = rules };
    }
}

/// <summary>Configures one physical column.</summary>
public sealed class G9CSyncColumnBuilder
{
    private string? _readSql;
    private string? _writeSql;
    private bool _readOnly;

    internal G9CSyncColumnBuilder(string name, G9ESyncColumnType type, bool nullable, G9ESyncColumnKind kind, string? readSql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Type = type;
        Nullable = nullable;
        Kind = kind;
        _readSql = readSql;
    }

    internal string Name { get; }
    internal G9ESyncColumnType Type { get; }
    internal bool Nullable { get; }
    internal G9ESyncColumnKind Kind { get; }

    /// <summary>Read expression with <c>{row}</c>, e.g. <c>{row}."Location"::text</c> for a PostGIS geometry.</summary>
    public G9CSyncColumnBuilder ReadAs(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        _readSql = sql;
        return this;
    }

    /// <summary>Write expression with <c>{value}</c>, e.g. <c>{value}::geometry</c>.</summary>
    public G9CSyncColumnBuilder WriteAs(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        _writeSql = sql;
        return this;
    }

    /// <summary>Downloaded but never accepted from uploads (server-owned column).</summary>
    public G9CSyncColumnBuilder ReadOnly()
    {
        _readOnly = true;
        return this;
    }

    internal G9CSyncServerColumn Build() => new(Name, Type, Nullable, Kind, _readSql, _writeSql, _readOnly);
}

/// <summary>Configures one subscription.</summary>
public sealed class G9CSyncSubscriptionBuilder
{
    private readonly string _name;
    private readonly List<G9CSyncParameterDefinition> _parameters = [];
    private readonly List<(string Table, string Scope, string? When, bool AllowUpload, IG9SyncScopeProvider? Provider)> _includes = [];
    private readonly List<G9CSyncSubscriptionDependency> _dependencies = [];
    private bool _ephemeral;
    private TimeSpan _ttl;
    private bool _heavy;
    private G9EMoveOutPolicy _moveOut = G9EMoveOutPolicy.Delete;
    private TimeSpan _reconcileInterval = TimeSpan.FromHours(24);

    internal G9CSyncSubscriptionBuilder(string name) => _name = name;

    /// <summary>A client-supplied parameter, referenced in SQL as <c>@Name</c>.</summary>
    public G9CSyncSubscriptionBuilder Parameter(string name, G9ESyncColumnType type, bool required = true, bool isList = false)
    {
        _parameters.Add(new G9CSyncParameterDefinition(name, type, required, isList, isServer: false));
        return this;
    }

    /// <summary>A server-injected parameter whose value comes from the caller's auth context (never from the client).</summary>
    public G9CSyncSubscriptionBuilder ServerParameter(string name, G9ESyncColumnType type, bool isList = false)
    {
        _parameters.Add(new G9CSyncParameterDefinition(name, type, required: false, isList, isServer: true));
        return this;
    }

    /// <summary>Includes a table. See <see cref="G9CSyncSubscriptionTable.ScopeSql" /> and <see cref="G9CSyncSubscriptionTable.WhenSql" />.</summary>
    public G9CSyncSubscriptionBuilder Include(string table, string scopeSql = "true", string? whenSql = null, bool allowUpload = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeSql);
        _includes.Add((table, scopeSql, whenSql, allowUpload, null));
        return this;
    }

    /// <summary>
    ///     Includes a table whose membership is computed per request by <paramref name="scope" /> - for example an EF Core query
    ///     composed with the application's own authorisation filters (<c>G9SyncData.Server.EfCore</c>).
    ///     <para>
    ///         ⚠ If the query's result depends on the caller's permissions, surface those permissions as a
    ///         <see cref="ServerParameter" /> (a permission stamp is enough). A changed server parameter is what makes a device
    ///         reconcile; a permission read only inside the query changes the rows it returns but tells no device to look again.
    ///     </para>
    /// </summary>
    public G9CSyncSubscriptionBuilder Include(string table, IG9SyncScopeProvider scope, string? whenSql = null, bool allowUpload = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Fingerprint);
        _includes.Add((table, "{scope:" + scope.Fingerprint + "}", whenSql, allowUpload, scope));
        return this;
    }

    /// <summary>Another (parameterless) subscription that must sync first.</summary>
    public G9CSyncSubscriptionBuilder DependsOn(string subscription, bool required = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscription);
        _dependencies.Add(new G9CSyncSubscriptionDependency(subscription, required));
        return this;
    }

    /// <summary>On-demand subscription whose rows are garbage-collected when unused for <paramref name="ttl" />.</summary>
    public G9CSyncSubscriptionBuilder Ephemeral(TimeSpan ttl)
    {
        _ephemeral = true;
        _ttl = ttl;
        return this;
    }

    /// <summary>Marks the subscription as expensive: clients never start it in the background.</summary>
    public G9CSyncSubscriptionBuilder Heavy()
    {
        _heavy = true;
        return this;
    }

    public G9CSyncSubscriptionBuilder MoveOut(G9EMoveOutPolicy policy)
    {
        _moveOut = policy;
        return this;
    }

    /// <summary>How often clients run a safety reconciliation (detects move-outs the delta cannot see).</summary>
    public G9CSyncSubscriptionBuilder ReconcileEvery(TimeSpan interval)
    {
        _reconcileInterval = interval;
        return this;
    }

    internal G9CSyncSubscriptionDefinition? Build(IReadOnlyDictionary<string, G9CSyncServerTable> tables, List<string> errors)
    {
        var before = errors.Count;
        var parameterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in _parameters)
        {
            if (!parameterNames.Add(parameter.Name)) errors.Add($"Subscription '{_name}' declares parameter '{parameter.Name}' twice.");
            if (parameter.Name.StartsWith("g9", StringComparison.OrdinalIgnoreCase)) errors.Add($"Subscription '{_name}' parameter '{parameter.Name}': names starting with 'g9' are reserved.");
            if (parameter.Type is not (G9ESyncColumnType.Boolean or G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 or G9ESyncColumnType.String or G9ESyncColumnType.Guid))
            {
                errors.Add($"Subscription '{_name}' parameter '{parameter.Name}' has unsupported type {parameter.Type}.");
            }
        }

        var includes = new List<G9CSyncSubscriptionTable>();
        foreach (var (tableName, scope, when, allowUpload, provider) in _includes)
        {
            if (!tables.TryGetValue(tableName, out var table))
            {
                errors.Add($"Subscription '{_name}' includes unknown table '{tableName}'.");
                continue;
            }

            if (includes.Any(i => ReferenceEquals(i.Table, table))) errors.Add($"Subscription '{_name}' includes '{tableName}' twice.");
            includes.Add(new G9CSyncSubscriptionTable(table, scope, when, allowUpload && table.AcceptsUploads, provider));
        }

        if (includes.Count == 0) errors.Add($"Subscription '{_name}' includes no tables.");
        if (_ephemeral && _ttl <= TimeSpan.Zero) errors.Add($"Subscription '{_name}' is ephemeral but has no positive TTL.");
        if (_reconcileInterval <= TimeSpan.Zero) errors.Add($"Subscription '{_name}' needs a positive reconcile interval.");
        if (errors.Count > before) return null;

        // Tables are kept in model (dependency) order regardless of Include order.
        var ordered = includes.OrderBy(i => i.Table.Order).ToArray();
        return new G9CSyncSubscriptionDefinition(_name, _parameters.ToArray(), ordered, _dependencies.ToArray(), _ephemeral, _ttl, _heavy,
            _moveOut, _reconcileInterval, ComputeHash(ordered));
    }

    private string ComputeHash(IReadOnlyList<G9CSyncSubscriptionTable> tables)
    {
        var text = new StringBuilder("v1|").Append(_name);
        foreach (var p in _parameters.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            text.Append("|p:").Append(p.Name).Append(':').Append((int)p.Type).Append(':').Append(p.Required).Append(':').Append(p.IsList).Append(':').Append(p.IsServer);
        }

        foreach (var t in tables)
        {
            text.Append("|t:").Append(t.Table.LogicalName).Append('|').Append(t.ScopeSql).Append('|').Append(t.WhenSql).Append('|').Append(t.AllowUpload);
            foreach (var c in t.Table.ComputedColumns) text.Append("|c:").Append(c.Name).Append('=').Append(c.ReadSql);
        }

        text.Append("|m:").Append(((int)_moveOut).ToString(CultureInfo.InvariantCulture));
        return G9CHash.Sha256Hex(text.ToString());
    }
}
