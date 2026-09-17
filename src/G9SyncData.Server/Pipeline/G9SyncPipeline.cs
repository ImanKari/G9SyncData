using System.Data.Common;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Model;

namespace G9SyncData.Server.Pipeline;

/// <summary>A row the pipeline refuses. The row stays on the device, flagged, and never blocks other rows.</summary>
public sealed record G9CRowRejection(string Key, string Code, string? Message = null, string? Details = null);

/// <summary>
///     Context of one table step inside a push transaction. Validators, transformers and delete handlers work on the
///     STAGING TABLE with ONE set-based SQL statement per step (never per row).
/// </summary>
/// <remarks>
///     Staging columns: every pushed column (same names as the table), plus
///     <c>g9_key</c> (the key), <c>g9_op</c> (1 upsert, 2 delete), <c>g9_exists</c> (row already on the server),
///     <c>g9_action</c> (1 = will apply, 0 = skipped), <c>g9_seq</c> (device order).
///     Only consider rows with <c>g9_action = 1</c>.
/// </remarks>
public sealed class G9CUploadStepContext
{
    private readonly Func<IReadOnlyList<G9CRowRejection>, CancellationToken, ValueTask> _reject;

    /// <param name="reject">Provider callback that excludes rows from the apply and records their rejection.</param>
    public G9CUploadStepContext(Func<IReadOnlyList<G9CRowRejection>, CancellationToken, ValueTask> reject) =>
        _reject = reject ?? throw new ArgumentNullException(nameof(reject));

    public required G9CSyncServerTable Table { get; init; }
    public required IReadOnlyList<G9CSyncServerColumn> Columns { get; init; }
    public required Guid ClientId { get; init; }
    public required long BatchId { get; init; }
    public required G9CSyncCaller Caller { get; init; }
    public required G9CSyncAuthContext Auth { get; init; }
    public required DbConnection Connection { get; init; }
    public required DbTransaction Transaction { get; init; }

    /// <summary>Quoted staging table name, ready to embed in SQL.</summary>
    public required string StagingTable { get; init; }

    /// <summary>Scoped services of this push request.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Rejects rows (they are excluded from the apply and reported to the client).</summary>
    public ValueTask RejectAsync(IReadOnlyList<G9CRowRejection> rejections, CancellationToken cancellationToken = default) =>
        rejections.Count == 0 ? ValueTask.CompletedTask : _reject(rejections, cancellationToken);
}

/// <summary>Business-rule validation of uploaded rows, before the apply.</summary>
public interface IG9UploadValidator
{
    bool AppliesTo(G9CSyncServerTable table);

    ValueTask ValidateAsync(G9CUploadStepContext context, CancellationToken cancellationToken);
}

/// <summary>Derives or rewrites staged values (e.g. server-computed geometry), after validation and before the apply.</summary>
public interface IG9UploadTransformer
{
    bool AppliesTo(G9CSyncServerTable table);

    ValueTask TransformAsync(G9CUploadStepContext context, CancellationToken cancellationToken);
}

/// <summary>Cleans up dependents of uploaded deletes that no foreign key cascades (files, polymorphic comments).</summary>
public interface IG9DeleteHandler
{
    bool AppliesTo(G9CSyncServerTable table);

    ValueTask HandleDeletesAsync(G9CUploadStepContext context, IReadOnlyList<string> keys, CancellationToken cancellationToken);
}

/// <summary>Decision of a custom conflict resolver.</summary>
public enum G9EConflictDecision : byte
{
    /// <summary>Keep the server row; report the conflict and return the canonical row.</summary>
    KeepServer = 0,

    /// <summary>Apply the client's changed columns.</summary>
    AcceptClient = 1,

    /// <summary>Reject the row with a code.</summary>
    Reject = 2
}

public sealed record G9CConflictResolution(string Key, G9EConflictDecision Decision, string? RejectCode = null, string? Message = null);

/// <summary>One conflicting row.</summary>
public sealed class G9CConflictRow
{
    public required string Key { get; init; }
    public required bool IsDelete { get; init; }
    public required long BaseVersion { get; init; }
    public required long ServerVersion { get; init; }
    public required bool ServerDeleted { get; init; }

    /// <summary>Changed columns, bit i = <see cref="G9CConflictContext.Columns" />[i].</summary>
    public required long DirtyMask { get; init; }

    public required IReadOnlyDictionary<string, object?> ClientValues { get; init; }

    /// <summary>Null when the server row no longer exists.</summary>
    public IReadOnlyDictionary<string, object?>? ServerValues { get; init; }
}

public sealed class G9CConflictContext
{
    public required G9CSyncServerTable Table { get; init; }
    public required IReadOnlyList<G9CSyncServerColumn> Columns { get; init; }
    public required Guid ClientId { get; init; }
    public required IReadOnlyList<G9CConflictRow> Rows { get; init; }
    public required IServiceProvider Services { get; init; }
}

/// <summary>Resolves conflicts for tables configured with <see cref="G9EConflictPolicy.Custom" />.</summary>
public interface IG9ConflictResolver
{
    bool AppliesTo(G9CSyncServerTable table);

    ValueTask<IReadOnlyList<G9CConflictResolution>> ResolveAsync(G9CConflictContext context, CancellationToken cancellationToken);
}

public sealed class G9CPostCommitContext
{
    public required G9CSyncServerTable Table { get; init; }
    public required Guid ClientId { get; init; }
    public required long BatchId { get; init; }
    public required G9CSyncCaller Caller { get; init; }
    public required G9CSyncAuthContext Auth { get; init; }
    public required IReadOnlyList<string> UpsertedKeys { get; init; }
    public required IReadOnlyList<string> DeletedKeys { get; init; }
    public required IServiceProvider Services { get; init; }
}

/// <summary>
///     Runs after the push transaction committed. Runs at most once per (client, batch): a retried batch is acknowledged
///     from the stored result and never reaches handlers again. A handler failure is logged; it does not fail the push.
/// </summary>
public interface IG9PostCommitHandler
{
    bool AppliesTo(G9CSyncServerTable table);

    ValueTask HandleAsync(G9CPostCommitContext context, CancellationToken cancellationToken);
}

/// <summary>The hooks resolved for one push.</summary>
public sealed class G9CSyncPipeline
{
    public static G9CSyncPipeline Empty { get; } = new();

    public IReadOnlyList<IG9UploadValidator> Validators { get; init; } = [];
    public IReadOnlyList<IG9UploadTransformer> Transformers { get; init; } = [];
    public IReadOnlyList<IG9DeleteHandler> DeleteHandlers { get; init; } = [];
    public IReadOnlyList<IG9ConflictResolver> ConflictResolvers { get; init; } = [];
    public IReadOnlyList<IG9PostCommitHandler> PostCommitHandlers { get; init; } = [];
    public IReadOnlyList<IG9RowWriter> RowWriters { get; init; } = [];
    public IReadOnlyList<IG9CommandHandler> CommandHandlers { get; init; } = [];
}
