using System.Data.Common;
using G9SyncData.Server.Auth;

namespace G9SyncData.Server.Model;

/// <summary>What a conflict rule does with the uploaded row (A-05).</summary>
public enum G9EConflictAction : byte
{
    /// <summary>Keep the server row; report the conflict; the device receives the canonical row.</summary>
    TakeServer = 0,

    /// <summary>Apply every column the device changed, over the server's changes to the same columns.</summary>
    TakeClient = 1,

    /// <summary>Apply the device's changed columns the server did not change; report the overlapping ones.</summary>
    MergeColumns = 2,

    /// <summary>Apply only the named columns (of those the device changed); report the rest.</summary>
    ApplyColumns = 3,

    /// <summary>Reject the row with a code; it stays on the device, flagged.</summary>
    Reject = 4
}

/// <summary>
///     One conflicting upload, as a rule sees it: the server changed the row since the device's base version.
///     <para>
///         Column facts are exact when the table tracks column versions (every table with conflict rules does). When the server row
///         was deleted, or its column history is unknown, <see cref="ServerChanged" /> is null and <see cref="Disjoint" /> is false.
///     </para>
/// </summary>
public sealed class G9CConflictCase
{
    public required G9CSyncServerTable Table { get; init; }

    /// <summary>Canonical key.</summary>
    public required string Key { get; init; }

    /// <summary>The device deletes the row.</summary>
    public required bool ClientDeletes { get; init; }

    /// <summary>The server row was deleted since the device's base version.</summary>
    public required bool ServerDeleted { get; init; }

    public required long BaseVersion { get; init; }
    public required long ServerVersion { get; init; }

    /// <summary>Columns the device changed.</summary>
    public required IReadOnlySet<string> ClientChanged { get; init; }

    /// <summary>Columns the server changed since the device's base version; null when unknown.</summary>
    public required IReadOnlySet<string>? ServerChanged { get; init; }

    /// <summary>When the device made its edit (its hybrid clock, corrected by the server offset); null when unknown.</summary>
    public required DateTime? ClientEditedUtc { get; init; }

    /// <summary>When the server last changed the row.</summary>
    public required DateTime? ServerChangedUtc { get; init; }

    public required IReadOnlyDictionary<string, object?> ClientValues { get; init; }

    /// <summary>Current server values; null when the server row no longer exists.</summary>
    public required IReadOnlyDictionary<string, object?>? ServerValues { get; init; }

    public required Guid ClientId { get; init; }
    public required G9CSyncAuthContext Auth { get; init; }

    /// <summary>The push connection and transaction, for a rule that must look at other data.</summary>
    public required DbConnection Connection { get; init; }

    public required DbTransaction Transaction { get; init; }

    /// <summary>Scoped services of the push request.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Columns both sides changed (empty when <see cref="ServerChanged" /> is unknown).</summary>
    public IReadOnlySet<string> Overlapping =>
        ServerChanged is null ? new HashSet<string>() : ClientChanged.Where(ServerChanged.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Both sides changed different columns (and the server's columns are known).</summary>
    public bool Disjoint => ServerChanged is not null && !ServerDeleted && !ClientChanged.Any(ServerChanged.Contains);

    /// <summary>The device's edit is known to be newer than the server's last change.</summary>
    public bool ClientIsNewer => ClientEditedUtc is { } client && ServerChangedUtc is { } server && client > server;

    /// <summary>The device changed nothing but (a subset of) <paramref name="columns" />.</summary>
    public bool ChangedOnly(params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var allowed = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        return ClientChanged.Count > 0 && ClientChanged.All(allowed.Contains);
    }

    public object? Client(string column) => ClientValues.GetValueOrDefault(column);

    public object? Server(string column) => ServerValues?.GetValueOrDefault(column);
}

/// <summary>A rule's decision.</summary>
public sealed class G9CConflictOutcome
{
    private G9CConflictOutcome(G9EConflictAction action, IReadOnlyList<string>? columns, string? code, string? message)
    {
        Action = action;
        Columns = columns ?? [];
        Code = code;
        Message = message;
    }

    public G9EConflictAction Action { get; }
    public IReadOnlyList<string> Columns { get; }
    public string? Code { get; }
    public string? Message { get; }

    public static G9CConflictOutcome TakeServer { get; } = new(G9EConflictAction.TakeServer, null, null, null);
    public static G9CConflictOutcome TakeClient { get; } = new(G9EConflictAction.TakeClient, null, null, null);
    public static G9CConflictOutcome MergeColumns { get; } = new(G9EConflictAction.MergeColumns, null, null, null);

    public static G9CConflictOutcome ApplyColumns(params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        return new G9CConflictOutcome(G9EConflictAction.ApplyColumns, columns, null, null);
    }

    public static G9CConflictOutcome Reject(string code, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new G9CConflictOutcome(G9EConflictAction.Reject, null, code, message);
    }
}

/// <summary>One named rule: a condition and what to do when it holds.</summary>
public sealed class G9CConflictRule
{
    internal G9CConflictRule(string name, Func<G9CConflictCase, CancellationToken, ValueTask<bool>> condition,
        Func<G9CConflictCase, CancellationToken, ValueTask<G9CConflictOutcome>> decide)
    {
        Name = name;
        Condition = condition;
        Decide = decide;
    }

    /// <summary>Tag for telemetry and logs: which rule decided.</summary>
    public string Name { get; }

    public Func<G9CConflictCase, CancellationToken, ValueTask<bool>> Condition { get; }
    public Func<G9CConflictCase, CancellationToken, ValueTask<G9CConflictOutcome>> Decide { get; }
}

/// <summary>
///     Ordered conflict rules of a table (A-05): the first rule whose condition holds decides; a row no rule matches falls back to the
///     table's <see cref="G9EConflictPolicy" />.
/// </summary>
public sealed class G9CConflictRules
{
    internal G9CConflictRules(IReadOnlyList<G9CConflictRule> rules) => Rules = rules;

    public IReadOnlyList<G9CConflictRule> Rules { get; }
}

/// <summary>Builds <see cref="G9CConflictRules" />.</summary>
public sealed class G9CConflictRulesBuilder
{
    private readonly List<G9CConflictRule> _rules = [];

    /// <summary>A rule over the conflict's facts.</summary>
    public G9CConflictActionBuilder When(string name, Func<G9CConflictCase, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return WhenAsync(name, (@case, _) => ValueTask.FromResult(condition(@case)));
    }

    /// <summary>A rule that may query the database (through <see cref="G9CConflictCase.Connection" />) or services.</summary>
    public G9CConflictActionBuilder WhenAsync(string name, Func<G9CConflictCase, CancellationToken, ValueTask<bool>> condition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(condition);
        return new G9CConflictActionBuilder(this, name, condition);
    }

    /// <summary>The rule for every conflict no earlier rule matched (instead of the table's policy).</summary>
    public G9CConflictActionBuilder Otherwise() => WhenAsync("otherwise", (_, _) => ValueTask.FromResult(true));

    internal void Add(G9CConflictRule rule) => _rules.Add(rule);

    internal G9CConflictRules Build(string table, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in _rules.Where(r => !names.Add(r.Name))) errors.Add($"Table '{table}' declares conflict rule '{rule.Name}' twice.");
        return new G9CConflictRules(_rules.ToArray());
    }
}

/// <summary>Completes a rule with its action.</summary>
public sealed class G9CConflictActionBuilder
{
    private readonly G9CConflictRulesBuilder _owner;
    private readonly string _name;
    private readonly Func<G9CConflictCase, CancellationToken, ValueTask<bool>> _condition;

    internal G9CConflictActionBuilder(G9CConflictRulesBuilder owner, string name, Func<G9CConflictCase, CancellationToken, ValueTask<bool>> condition)
    {
        _owner = owner;
        _name = name;
        _condition = condition;
    }

    public G9CConflictRulesBuilder TakeServer() => Then(G9CConflictOutcome.TakeServer);

    public G9CConflictRulesBuilder TakeClient() => Then(G9CConflictOutcome.TakeClient);

    public G9CConflictRulesBuilder MergeColumns() => Then(G9CConflictOutcome.MergeColumns);

    public G9CConflictRulesBuilder ApplyColumns(params string[] columns) => Then(G9CConflictOutcome.ApplyColumns(columns));

    public G9CConflictRulesBuilder Reject(string code, string? message = null) => Then(G9CConflictOutcome.Reject(code, message));

    /// <summary>Decides per row in code (e.g. compares values, or looks something up).</summary>
    public G9CConflictRulesBuilder Decide(Func<G9CConflictCase, CancellationToken, ValueTask<G9CConflictOutcome>> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        _owner.Add(new G9CConflictRule(_name, _condition, decide));
        return _owner;
    }

    private G9CConflictRulesBuilder Then(G9CConflictOutcome outcome) => Decide((_, _) => ValueTask.FromResult(outcome));
}
