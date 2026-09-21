namespace G9SyncData.Protocol;

/// <summary>A table as the client build understands it (generated from its entities).</summary>
public sealed class G9DtClientTableModel
{
    /// <summary>Server logical name, e.g. <c>Operation.Pot</c>.</summary>
    public string Name { get; set; } = string.Empty;

    public string KeyColumn { get; set; } = string.Empty;
    public G9ESyncColumnType KeyType { get; set; }
    public List<G9DtColumnModel> Columns { get; set; } = [];
}

public sealed class G9DtHelloRequest
{
    public Guid ClientId { get; set; }
    public string? AppVersion { get; set; }
    public int ProtocolVersion { get; set; } = G9SyncProtocol.Version;
    public List<G9DtClientTableModel> Tables { get; set; } = [];
    public List<G9DtSubscriptionRef> Subscriptions { get; set; } = [];
}

/// <summary>The column set both sides agreed on for one table (server model order, filtered to the client's columns).</summary>
public sealed class G9DtNegotiatedTable
{
    public int TableId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyColumn { get; set; } = string.Empty;
    public G9ESyncColumnType KeyType { get; set; }
    public List<G9DtColumnModel> Columns { get; set; } = [];

    /// <summary>Negotiated columns computed by the server (read-only, included in reconciliation hashes).</summary>
    public List<string> ComputedColumns { get; set; } = [];

    /// <summary>Negotiated columns the client may not upload.</summary>
    public List<string> ReadOnlyColumns { get; set; } = [];

    /// <summary>Columns the client asked for that the server does not have.</summary>
    public List<string> MissingColumns { get; set; } = [];

    /// <summary>
    ///     Columns both sides have under the same name but with DIFFERENT types (ADR-0061). Not negotiated, exactly like a missing column -
    ///     but a missing column is a newer client against an older server, while this is a wrong client model, and until it had a list of
    ///     its own it was reported as the former and noticed by nobody. A server older than this list still files them under
    ///     <see cref="MissingColumns" />.
    /// </summary>
    public List<G9DtColumnTypeMismatch> TypeMismatchColumns { get; set; } = [];

    public G9ESyncDirection Direction { get; set; }
    public G9EConflictPolicy ConflictPolicy { get; set; }
}

/// <summary>One column whose declared type differs between the client model and the server model.</summary>
public sealed class G9DtColumnTypeMismatch
{
    public string Name { get; set; } = string.Empty;
    public G9ESyncColumnType ClientType { get; set; }
    public G9ESyncColumnType ServerType { get; set; }
}

public sealed class G9DtParameterDefinition
{
    public string Name { get; set; } = string.Empty;
    public G9ESyncColumnType Type { get; set; }
    public bool Required { get; set; }
    public bool IsList { get; set; }
}

public sealed class G9DtSubscriptionDependency
{
    public string Name { get; set; } = string.Empty;
    public bool Required { get; set; } = true;
}

/// <summary>A server subscription definition, sent to clients so they never duplicate it.</summary>
public sealed class G9DtSubscriptionDefinition
{
    public string Name { get; set; } = string.Empty;
    public List<G9DtParameterDefinition> Parameters { get; set; } = [];
    public List<G9DtSubscriptionDependency> DependsOn { get; set; } = [];

    /// <summary>Server logical table names included.</summary>
    public List<string> Tables { get; set; } = [];

    public bool Ephemeral { get; set; }
    public int TtlSeconds { get; set; }
    public bool Heavy { get; set; }
    public G9EMoveOutPolicy MoveOutPolicy { get; set; }
    public int ReconcileIntervalMinutes { get; set; }
    public string DefinitionHash { get; set; } = string.Empty;
}

public sealed class G9DtSubscriptionState
{
    public string Key { get; set; } = string.Empty;
    public G9ESubscriptionAction Action { get; set; }
    public string? DefinitionHash { get; set; }
    public string? AuthHash { get; set; }
    public string? Message { get; set; }
}

public sealed class G9DtHelloResult
{
    public Guid Epoch { get; set; }
    public DateTime ServerTimeUtc { get; set; }
    public int ProtocolVersion { get; set; } = G9SyncProtocol.Version;
    public List<G9DtNegotiatedTable> Tables { get; set; } = [];
    public List<string> UnknownTables { get; set; } = [];
    public List<G9DtSubscriptionDefinition> Definitions { get; set; } = [];
    public List<G9DtSubscriptionState> Subscriptions { get; set; } = [];

    /// <summary>The most rows the server accepts in one push; the client sizes its batches to it (0 = not announced).</summary>
    public int MaxPushRows { get; set; }

    /// <summary>The most frame bytes the server accepts in one push; the client sizes its batches to it (0 = not announced).</summary>
    public long MaxPushBytes { get; set; }

    public G9DtSyncError? Error { get; set; }
}
