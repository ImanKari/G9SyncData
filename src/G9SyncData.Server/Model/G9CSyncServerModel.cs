namespace G9SyncData.Server.Model;

/// <summary>Whether a column is a stored table column or an expression evaluated on read.</summary>
public enum G9ESyncColumnKind : byte
{
    /// <summary>A real column of the table. Captured by the change triggers; writable unless marked read-only.</summary>
    Physical = 0,

    /// <summary>A SQL expression evaluated on every read (e.g. an authorisation flag or a removed column's default). Never written, never captured, always part of the reconciliation hash.</summary>
    Computed = 1
}

/// <summary>One synced column of a server table.</summary>
public sealed class G9CSyncServerColumn
{
    internal G9CSyncServerColumn(string name, G9ESyncColumnType type, bool nullable, G9ESyncColumnKind kind, string? readSql, string? writeSql, bool readOnly)
    {
        Name = name;
        Type = type;
        Nullable = nullable;
        Kind = kind;
        ReadSql = readSql;
        WriteSql = writeSql;
        IsReadOnly = readOnly || kind == G9ESyncColumnKind.Computed;
    }

    public string Name { get; }
    public G9ESyncColumnType Type { get; }
    public bool Nullable { get; }
    public G9ESyncColumnKind Kind { get; }
    public bool IsComputed => Kind == G9ESyncColumnKind.Computed;
    public bool IsReadOnly { get; }
    public bool IsWritable => !IsReadOnly;

    /// <summary>SQL template producing the value on read; <c>{row}</c> is the table alias. Null reads the column itself.</summary>
    public string? ReadSql { get; }

    /// <summary>SQL template converting an uploaded value; <c>{value}</c> is the staged value. Null casts to the column type.</summary>
    public string? WriteSql { get; }

    /// <summary>Index among the table's physical columns: the bit in change masks and the slot in column versions. -1 for computed columns.</summary>
    public int CaptureIndex { get; internal set; } = -1;

    public override string ToString() => $"{Name}:{Type}{(Nullable ? "?" : string.Empty)}";
}

/// <summary>A synced server table. Defined once, shared by every subscription that includes it.</summary>
public sealed class G9CSyncServerTable
{
    private readonly Dictionary<string, G9CSyncServerColumn> _byName;

    internal G9CSyncServerTable(string schema, string name, string keyColumn, G9ESyncColumnType keyType,
        IReadOnlyList<G9CSyncServerColumn> columns, G9ESyncDirection direction, G9EConflictPolicy conflictPolicy,
        bool? orderedApply, string? writeRuleSql, int order)
    {
        Schema = schema;
        Name = name;
        LogicalName = schema + "." + name;
        KeyColumn = keyColumn;
        KeyType = keyType;
        Columns = columns;
        PhysicalColumns = columns.Where(c => !c.IsComputed).ToArray();
        ComputedColumns = columns.Where(c => c.IsComputed).ToArray();
        Direction = direction;
        ConflictPolicy = conflictPolicy;
        OrderedApply = orderedApply;
        WriteRuleSql = writeRuleSql;
        Order = order;
        _byName = columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < PhysicalColumns.Count; i++) PhysicalColumns[i].CaptureIndex = i;
    }

    public string Schema { get; }
    public string Name { get; }

    /// <summary><c>schema.table</c> — the name clients use.</summary>
    public string LogicalName { get; }

    public string KeyColumn { get; }
    public G9ESyncColumnType KeyType { get; }

    /// <summary>All synced columns except the key, in model order.</summary>
    public IReadOnlyList<G9CSyncServerColumn> Columns { get; }

    public IReadOnlyList<G9CSyncServerColumn> PhysicalColumns { get; }
    public IReadOnlyList<G9CSyncServerColumn> ComputedColumns { get; }
    public G9ESyncDirection Direction { get; }
    public G9EConflictPolicy ConflictPolicy { get; }

    /// <summary>True forces ordered (row-by-row, device order) apply; false forces set-based; null lets the provider decide (it orders tables with extra unique indexes).</summary>
    public bool? OrderedApply { get; }

    /// <summary>Optional extra write predicate over <c>{row}</c>, AND-ed with subscription scopes.</summary>
    public string? WriteRuleSql { get; }

    /// <summary>Registration order; the push applies tables in this order (register parents first).</summary>
    public int Order { get; }

    /// <summary>Ordered conflict rules (A-05); null when the table uses its policy alone.</summary>
    public G9CConflictRules? ConflictRules { get; internal init; }

    public bool TracksColumnVersions => ConflictPolicy == G9EConflictPolicy.ColumnMerge || ConflictRules is { Rules.Count: > 0 };
    public bool AcceptsUploads => Direction != G9ESyncDirection.DownloadOnly;

    public G9CSyncServerColumn? FindColumn(string name) => _byName.GetValueOrDefault(name);

    public override string ToString() => LogicalName;
}

/// <summary>A subscription parameter (client-supplied or server-injected).</summary>
public sealed class G9CSyncParameterDefinition
{
    internal G9CSyncParameterDefinition(string name, G9ESyncColumnType type, bool required, bool isList, bool isServer)
    {
        Name = name;
        Type = type;
        Required = required;
        IsList = isList;
        IsServer = isServer;
    }

    public string Name { get; }
    public G9ESyncColumnType Type { get; }
    public bool Required { get; }
    public bool IsList { get; }

    /// <summary>True when the value comes from the caller's <c>G9CSyncAuthContext</c>, never from the client.</summary>
    public bool IsServer { get; }
}

/// <summary>How one table participates in a subscription.</summary>
public sealed class G9CSyncSubscriptionTable
{
    internal G9CSyncSubscriptionTable(G9CSyncServerTable table, string scopeSql, string? whenSql, bool allowUpload,
        IG9SyncScopeProvider? scopeProvider = null)
    {
        Table = table;
        ScopeSql = scopeSql;
        WhenSql = whenSql;
        AllowUpload = allowUpload;
        ScopeProvider = scopeProvider;
    }

    public G9CSyncServerTable Table { get; }

    /// <summary>
    ///     Which rows BELONG to the subscription (e.g. "this farm's pots"). A changed row inside the scope that fails
    ///     <see cref="WhenSql" /> is sent as a move-out; a changed row outside the scope is not sent at all.
    /// </summary>
    public string ScopeSql { get; }

    /// <summary>Row-state filter inside the scope (e.g. <c>{row}."IsCurrent"</c>). Null means every scoped row.</summary>
    public string? WhenSql { get; }

    /// <summary>
    ///     When set, membership comes from this provider's per-request key query instead of <see cref="ScopeSql" /> (which then
    ///     only carries the provider's fingerprint, for the definition hash).
    /// </summary>
    public IG9SyncScopeProvider? ScopeProvider { get; }

    /// <summary>Whether clients may upload rows of this table under this subscription.</summary>
    public bool AllowUpload { get; }
}

public sealed class G9CSyncSubscriptionDependency
{
    internal G9CSyncSubscriptionDependency(string name, bool required)
    {
        Name = name;
        Required = required;
    }

    public string Name { get; }
    public bool Required { get; }
}

/// <summary>A named, parameterised set of rows over one or more tables.</summary>
public sealed class G9CSyncSubscriptionDefinition
{
    internal G9CSyncSubscriptionDefinition(string name, IReadOnlyList<G9CSyncParameterDefinition> parameters,
        IReadOnlyList<G9CSyncSubscriptionTable> tables, IReadOnlyList<G9CSyncSubscriptionDependency> dependsOn,
        bool ephemeral, TimeSpan ttl, bool heavy, G9EMoveOutPolicy moveOutPolicy, TimeSpan reconcileInterval, string definitionHash)
    {
        Name = name;
        Parameters = parameters;
        ClientParameters = parameters.Where(p => !p.IsServer).ToArray();
        ServerParameters = parameters.Where(p => p.IsServer).ToArray();
        ParameterNames = new HashSet<string>(parameters.Select(p => p.Name), StringComparer.Ordinal);
        Tables = tables;
        DependsOn = dependsOn;
        Ephemeral = ephemeral;
        Ttl = ttl;
        Heavy = heavy;
        MoveOutPolicy = moveOutPolicy;
        ReconcileInterval = reconcileInterval;
        DefinitionHash = definitionHash;
    }

    public string Name { get; }
    public IReadOnlyList<G9CSyncParameterDefinition> Parameters { get; }
    public IReadOnlyList<G9CSyncParameterDefinition> ClientParameters { get; }
    public IReadOnlyList<G9CSyncParameterDefinition> ServerParameters { get; }
    public IReadOnlySet<string> ParameterNames { get; }
    public IReadOnlyList<G9CSyncSubscriptionTable> Tables { get; }
    public IReadOnlyList<G9CSyncSubscriptionDependency> DependsOn { get; }
    public bool Ephemeral { get; }
    public TimeSpan Ttl { get; }
    public bool Heavy { get; }
    public G9EMoveOutPolicy MoveOutPolicy { get; }
    public TimeSpan ReconcileInterval { get; }

    /// <summary>Hash of everything that decides WHICH rows belong (parameters, scopes, filters). A change makes clients reconcile.</summary>
    public string DefinitionHash { get; }

    public G9CSyncSubscriptionTable? FindTable(G9CSyncServerTable table) => Tables.FirstOrDefault(t => ReferenceEquals(t.Table, table));

    public override string ToString() => Name;
}

/// <summary>The immutable server sync model.</summary>
public sealed class G9CSyncServerModel
{
    private readonly Dictionary<string, G9CSyncServerTable> _tables;
    private readonly Dictionary<string, G9CSyncSubscriptionDefinition> _subscriptions;

    internal G9CSyncServerModel(IReadOnlyList<G9CSyncServerTable> tables, IReadOnlyList<G9CSyncSubscriptionDefinition> subscriptions)
    {
        Tables = tables;
        Subscriptions = subscriptions;
        _tables = tables.ToDictionary(t => t.LogicalName, StringComparer.OrdinalIgnoreCase);
        _subscriptions = subscriptions.ToDictionary(s => s.Name, StringComparer.Ordinal);
    }

    /// <summary>Tables in registration (dependency) order.</summary>
    public IReadOnlyList<G9CSyncServerTable> Tables { get; }

    public IReadOnlyList<G9CSyncSubscriptionDefinition> Subscriptions { get; }

    public G9CSyncServerTable? FindTable(string logicalName) => _tables.GetValueOrDefault(logicalName);

    public G9CSyncSubscriptionDefinition? FindSubscription(string name) => _subscriptions.GetValueOrDefault(name);

    public IEnumerable<G9CSyncSubscriptionDefinition> SubscriptionsIncluding(G9CSyncServerTable table) =>
        Subscriptions.Where(s => s.FindTable(table) is not null);
}
