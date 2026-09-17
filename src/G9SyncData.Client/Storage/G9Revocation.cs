namespace G9SyncData.Client.Storage;

/// <summary>Why a row stopped being held by any subscription on this device (ADR-0026).</summary>
public enum G9ERevocationReason : byte
{
    /// <summary>It left a subscription's visible set: its data changed, or the user's permissions did (a delta move-out or reconciliation).</summary>
    LeftScope = 0,

    /// <summary>The app unsubscribed.</summary>
    Unsubscribed = 1,

    /// <summary>An ephemeral subscription expired.</summary>
    Expired = 2
}

/// <summary>What happens to a row no subscription holds any more.</summary>
public enum G9ERevocationDisposition : byte
{
    /// <summary>Removed from the app's table; a pending local change is discarded. Strict, and the default for rows without pending changes.</summary>
    Delete = 0,

    /// <summary>Kept in the app's table and recorded as revoked (queryable); a pending change is not uploaded.</summary>
    MarkRevoked = 1,

    /// <summary>
    ///     Removed from the app's table into the holding area, values and pending change intact; can be restored (re-uploaded) or discarded.
    ///     The default for rows with pending local changes: the app no longer shows data the user may not see, and the user's work survives.
    /// </summary>
    Quarantine = 2,

    /// <summary>Kept like <see cref="MarkRevoked" /> while the rule's condition holds; re-checked after every sync, then deleted.</summary>
    Retain = 3
}

/// <summary>A row that no subscription holds any more, as a revocation rule sees it.</summary>
public sealed class G9CRevokedRow
{
    public required string LocalTable { get; init; }

    /// <summary>Canonical key.</summary>
    public required string Key { get; init; }

    public required G9ERevocationReason Reason { get; init; }

    /// <summary>The subscription whose change released the row, when one did.</summary>
    public string? SubscriptionKey { get; init; }

    /// <summary>The row has local changes not yet accepted by the server.</summary>
    public required bool HasPendingChanges { get; init; }

    /// <summary>The server rejected the row's pending change.</summary>
    public required bool IsRejected { get; init; }

    /// <summary>Column values (key included), as stored locally; empty when the row no longer exists locally.</summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
}

/// <summary>Lets a custom rule change the row before its disposition applies (e.g. redact a column it may keep).</summary>
public interface IG9RevokedRowEditor
{
    void Set(string column, object? value);
}

/// <summary>A rule: a condition, and the disposition when it holds.</summary>
public sealed class G9CRevocationRule
{
    internal G9CRevocationRule(string name, Func<G9CRevokedRow, bool> condition, Func<G9CRevokedRow, IG9RevokedRowEditor, G9ERevocationDisposition> decide,
        Func<G9CRevokedRow, bool>? retainWhile)
    {
        Name = name;
        Condition = condition;
        Decide = decide;
        RetainWhile = retainWhile;
    }

    public string Name { get; }
    public Func<G9CRevokedRow, bool> Condition { get; }
    public Func<G9CRevokedRow, IG9RevokedRowEditor, G9ERevocationDisposition> Decide { get; }

    /// <summary>For <see cref="G9ERevocationDisposition.Retain" />: the row is kept while this holds.</summary>
    public Func<G9CRevokedRow, bool>? RetainWhile { get; }
}

/// <summary>
///     Ordered revocation rules (ADR-0026). The first matching rule decides; a row no rule matches is deleted, or quarantined when it
///     has pending local changes. Evaluated inside the apply transaction, so rules must be quick and must not touch the store.
/// </summary>
public sealed class G9CRevocationPolicy
{
    private G9CRevocationPolicy(IReadOnlyList<G9CRevocationRule> rules) => Rules = rules;

    public IReadOnlyList<G9CRevocationRule> Rules { get; }

    /// <summary>No rules: delete clean rows, quarantine rows with pending changes.</summary>
    public static G9CRevocationPolicy Default { get; } = new([]);

    public static G9CRevocationPolicy Create(Action<G9CRevocationPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new G9CRevocationPolicyBuilder();
        configure(builder);
        return new G9CRevocationPolicy(builder.Rules.ToArray());
    }

    /// <summary>The deciding rule (null when none matched) and the disposition.</summary>
    public (G9CRevocationRule? Rule, G9ERevocationDisposition Disposition) Decide(G9CRevokedRow row, IG9RevokedRowEditor editor)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(editor);
        foreach (var rule in Rules)
        {
            if (rule.Condition(row)) return (rule, rule.Decide(row, editor));
        }

        return (null, row.HasPendingChanges ? G9ERevocationDisposition.Quarantine : G9ERevocationDisposition.Delete);
    }

    /// <summary>The rule a retained row was kept by.</summary>
    public G9CRevocationRule? Find(string name) => Rules.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
}

public sealed class G9CRevocationPolicyBuilder
{
    internal List<G9CRevocationRule> Rules { get; } = [];

    public G9CRevocationActionBuilder When(string name, Func<G9CRevokedRow, bool> condition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(condition);
        if (Rules.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal))) throw new ArgumentException($"Revocation rule '{name}' is declared twice.", nameof(name));
        return new G9CRevocationActionBuilder(this, name, condition);
    }

    /// <summary>Rows of one local table.</summary>
    public G9CRevocationActionBuilder ForTable(string name, string localTable, Func<G9CRevokedRow, bool>? condition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localTable);
        return When(name, row => string.Equals(row.LocalTable, localTable, StringComparison.OrdinalIgnoreCase) && (condition?.Invoke(row) ?? true));
    }
}

public sealed class G9CRevocationActionBuilder
{
    private readonly G9CRevocationPolicyBuilder _owner;
    private readonly string _name;
    private readonly Func<G9CRevokedRow, bool> _condition;

    internal G9CRevocationActionBuilder(G9CRevocationPolicyBuilder owner, string name, Func<G9CRevokedRow, bool> condition)
    {
        _owner = owner;
        _name = name;
        _condition = condition;
    }

    public G9CRevocationPolicyBuilder Delete() => Add((_, _) => G9ERevocationDisposition.Delete, null);

    public G9CRevocationPolicyBuilder MarkRevoked() => Add((_, _) => G9ERevocationDisposition.MarkRevoked, null);

    public G9CRevocationPolicyBuilder Quarantine() => Add((_, _) => G9ERevocationDisposition.Quarantine, null);

    /// <summary>Keeps the row while <paramref name="retainWhile" /> holds; re-checked after every sync, then the row is deleted.</summary>
    public G9CRevocationPolicyBuilder RetainWhile(Func<G9CRevokedRow, bool> retainWhile)
    {
        ArgumentNullException.ThrowIfNull(retainWhile);
        return Add((_, _) => G9ERevocationDisposition.Retain, retainWhile);
    }

    /// <summary>Decides in code, optionally editing the row first (the edit is kept only if the row is).</summary>
    public G9CRevocationPolicyBuilder Custom(Func<G9CRevokedRow, IG9RevokedRowEditor, G9ERevocationDisposition> decide)
    {
        ArgumentNullException.ThrowIfNull(decide);
        return Add(decide, null);
    }

    private G9CRevocationPolicyBuilder Add(Func<G9CRevokedRow, IG9RevokedRowEditor, G9ERevocationDisposition> decide, Func<G9CRevokedRow, bool>? retainWhile)
    {
        _owner.Rules.Add(new G9CRevocationRule(_name, _condition, decide, retainWhile));
        return _owner;
    }
}

/// <summary>A row in the holding area.</summary>
public sealed class G9CQuarantinedRow
{
    public required string LocalTable { get; init; }
    public required string Key { get; init; }
    public required G9ERevocationReason Reason { get; init; }
    public string? Rule { get; init; }
    public required bool HadPendingChanges { get; init; }
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
    public required DateTime AtUtc { get; init; }
}

/// <summary>A row kept in the app's table after revocation (marked or retained).</summary>
public sealed class G9CRetainedRow
{
    public required string LocalTable { get; init; }
    public required string Key { get; init; }
    public required G9ERevocationDisposition Disposition { get; init; }
    public required G9ERevocationReason Reason { get; init; }
    public string? Rule { get; init; }
    public required DateTime AtUtc { get; init; }
}
