using G9SyncData.Protocol;

namespace G9SyncData.Client.Model;

/// <summary>A synced column as the client build understands it.</summary>
public sealed class G9CSyncClientColumn(string name, G9ESyncColumnType type, bool nullable)
{
    public string Name { get; } = name;
    public G9ESyncColumnType Type { get; } = type;
    public bool Nullable { get; } = nullable;
}

/// <summary>A secondary index the client store creates (Dotmim created none).</summary>
public sealed class G9CSyncClientIndex(string name, IReadOnlyList<string> columns, bool unique)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> Columns { get; } = columns;
    public bool Unique { get; } = unique;
}

/// <summary>
///     A synced table of the client model. Column order is the bit order of the local dirty mask; it is local and never
///     sent over the wire (the push maps bits to the negotiated column order).
/// </summary>
public sealed class G9CSyncClientTable
{
    private readonly Dictionary<string, int> _index;

    internal G9CSyncClientTable(string serverName, string localName, string keyColumn, G9ESyncColumnType keyType,
        IReadOnlyList<G9CSyncClientColumn> columns, IReadOnlyList<G9CSyncClientIndex> indexes)
    {
        ServerName = serverName;
        LocalName = localName;
        KeyColumn = keyColumn;
        KeyType = keyType;
        Columns = columns;
        Indexes = indexes;
        _index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++) _index[columns[i].Name] = i;
    }

    /// <summary>Server logical name, e.g. <c>Operation.Pot</c>.</summary>
    public string ServerName { get; }

    /// <summary>Local table name, e.g. <c>Pot</c>.</summary>
    public string LocalName { get; }

    public string KeyColumn { get; }
    public G9ESyncColumnType KeyType { get; }

    /// <summary>Synced columns except the key.</summary>
    public IReadOnlyList<G9CSyncClientColumn> Columns { get; }

    public IReadOnlyList<G9CSyncClientIndex> Indexes { get; }

    /// <summary>Index of a column (its dirty-mask bit), or -1.</summary>
    public int IndexOf(string column) => _index.GetValueOrDefault(column, -1);

    public G9DtClientTableModel ToWire() => new()
    {
        Name = ServerName,
        KeyColumn = KeyColumn,
        KeyType = KeyType,
        Columns = Columns.Select(c => new G9DtColumnModel { Name = c.Name, Type = c.Type, Nullable = c.Nullable }).ToList()
    };
}

/// <summary>The immutable client model. Build it with <see cref="G9CSyncClientModelBuilder" /> or use the source-generated one.</summary>
public sealed class G9CSyncClientModel
{
    private readonly Dictionary<string, G9CSyncClientTable> _byServer;
    private readonly Dictionary<string, G9CSyncClientTable> _byLocal;

    internal G9CSyncClientModel(IReadOnlyList<G9CSyncClientTable> tables)
    {
        Tables = tables;
        _byServer = tables.ToDictionary(t => t.ServerName, StringComparer.OrdinalIgnoreCase);
        _byLocal = tables.ToDictionary(t => t.LocalName, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<G9CSyncClientTable> Tables { get; }

    public G9CSyncClientTable? FindByServerName(string serverName) => _byServer.GetValueOrDefault(serverName);

    public G9CSyncClientTable? FindByLocalName(string localName) => _byLocal.GetValueOrDefault(localName);
}

public sealed class G9CSyncClientModelBuilder
{
    public const int MaxColumns = 62;
    private readonly List<(string Server, string Local, G9CSyncClientTableBuilder Builder)> _tables = [];

    public G9CSyncClientModelBuilder Table(string serverName, string localName, Action<G9CSyncClientTableBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(localName);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new G9CSyncClientTableBuilder();
        configure(builder);
        _tables.Add((serverName, localName, builder));
        return this;
    }

    public G9CSyncClientModel Build()
    {
        var errors = new List<string>();
        var tables = new List<G9CSyncClientTable>();
        var servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (server, local, builder) in _tables)
        {
            if (!servers.Add(server)) errors.Add($"Server table '{server}' is mapped twice.");
            if (!locals.Add(local)) errors.Add($"Local table '{local}' is mapped twice.");
            if (local.StartsWith("g9_", StringComparison.OrdinalIgnoreCase)) errors.Add($"Local table '{local}' uses the reserved prefix g9_.");
            var table = builder.Build(server, local, errors);
            if (table is not null) tables.Add(table);
        }

        if (errors.Count > 0)
        {
            throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                "The G9SyncData client model is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)));
        }

        return new G9CSyncClientModel(tables);
    }
}

public sealed class G9CSyncClientTableBuilder
{
    private readonly List<G9CSyncClientColumn> _columns = [];
    private readonly List<G9CSyncClientIndex> _indexes = [];
    private string? _key;
    private G9ESyncColumnType _keyType;

    internal G9CSyncClientTableBuilder()
    {
    }

    public G9CSyncClientTableBuilder Key(string column, G9ESyncColumnType type = G9ESyncColumnType.Guid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        _key = column;
        _keyType = type;
        return this;
    }

    public G9CSyncClientTableBuilder Column(string name, G9ESyncColumnType type, bool nullable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _columns.Add(new G9CSyncClientColumn(name, type, nullable));
        return this;
    }

    public G9CSyncClientTableBuilder Index(string? name, bool unique, params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _indexes.Add(new G9CSyncClientIndex(name ?? string.Empty, columns, unique));
        return this;
    }

    internal G9CSyncClientTable? Build(string server, string local, List<string> errors)
    {
        var before = errors.Count;
        if (_key is null) errors.Add($"{local}: no key.");
        else if (!G9CSyncKey.IsSupportedKeyType(_keyType)) errors.Add($"{local}: key type {_keyType} is not supported.");
        if (_columns.Count > G9CSyncClientModelBuilder.MaxColumns) errors.Add($"{local}: {_columns.Count} columns; the maximum is {G9CSyncClientModelBuilder.MaxColumns}.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_key is not null) names.Add(_key);
        foreach (var column in _columns)
        {
            if (!names.Add(column.Name)) errors.Add($"{local}: column '{column.Name}' is declared twice (or as the key).");
            if (column.Type == G9ESyncColumnType.Unspecified || !Enum.IsDefined(column.Type)) errors.Add($"{local}.{column.Name}: invalid type.");
        }

        var indexes = new List<G9CSyncClientIndex>();
        foreach (var index in _indexes)
        {
            if (index.Columns.Count == 0) errors.Add($"{local}: an index has no columns.");
            foreach (var column in index.Columns)
            {
                if (!names.Contains(column)) errors.Add($"{local}: index column '{column}' is not a synced column.");
            }

            var name = string.IsNullOrWhiteSpace(index.Name) ? "IX_" + local + "_" + string.Join("_", index.Columns) : index.Name;
            indexes.Add(new G9CSyncClientIndex(name, index.Columns, index.Unique));
        }

        return errors.Count > before ? null : new G9CSyncClientTable(server, local, _key!, _keyType, _columns.ToArray(), indexes);
    }
}
