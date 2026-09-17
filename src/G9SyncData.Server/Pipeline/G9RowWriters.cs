using System.Data.Common;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Model;

namespace G9SyncData.Server.Pipeline;

/// <summary>What happens to one uploaded row.</summary>
public enum G9ERowWriteOp : byte
{
    Upsert = 1,
    Delete = 2
}

/// <summary>
///     One uploaded row, after authorisation, validators, transformers and the conflict policy decided what to apply.
/// </summary>
public sealed class G9CRowWrite
{
    /// <summary>Canonical key text.</summary>
    public required string Key { get; init; }

    /// <summary>The key as the store read it (<see cref="Guid" />, <see cref="long" /> or <see cref="string" />).</summary>
    public required object KeyValue { get; init; }

    public required G9ERowWriteOp Op { get; init; }

    /// <summary>True when the row already exists on the server (an update), false for an insert.</summary>
    public required bool Exists { get; init; }

    /// <summary>Device order within the push.</summary>
    public required long Seq { get; init; }

    /// <summary>
    ///     The column values to write, by column name: for an update ONLY the columns the conflict policy decided to apply
    ///     (a column-merged edit carries just its changed columns); for an insert every uploaded column; empty for a delete.
    ///     Values are the store's staged values (Guid, string, int, long, decimal, double, bool, DateTime, byte[], …).
    /// </summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
}

/// <summary>Everything a row writer may use: the push transaction, the caller, and the request's scoped services.</summary>
public sealed class G9CRowWriteContext
{
    public required G9CSyncServerTable Table { get; init; }
    public required Guid ClientId { get; init; }
    public required long BatchId { get; init; }
    public required G9CSyncCaller Caller { get; init; }
    public required G9CSyncAuthContext Auth { get; init; }

    /// <summary>The push connection. Writes MUST use it and <see cref="Transaction" />, or they are outside the push.</summary>
    public required DbConnection Connection { get; init; }

    public required DbTransaction Transaction { get; init; }

    /// <summary>Scoped services of this push request.</summary>
    public required IServiceProvider Services { get; init; }
}

/// <summary>
///     Applies uploaded rows of a table through the application's own persistence instead of the store's set-based SQL
///     (ADR-0025 row path): audit stamping, interceptors, domain validation and derived writes run for device writes too.
///     <para>
///         The store calls it inside the push transaction, under a savepoint, with a batch of rows. When the call throws a
///         row-level failure — a database constraint/data error, or <see cref="G9CRowRejectedException" /> — the store rolls the
///         savepoint back and bisects the batch until the failing row is isolated; that row is rejected with its real error and
///         the rest apply. Any other exception fails the push as a server error.
///     </para>
///     <para>
///         Capture, write authorisation (before and after), conflict policies, rejections and results are unchanged: the writer
///         replaces only the INSERT/UPDATE/DELETE. Rows it changes beyond the uploaded values are returned to the device as
///         canonical rows.
///     </para>
/// </summary>
public interface IG9RowWriter
{
    /// <summary>
    ///     Called at server start and before each push, before <see cref="AppliesTo" />: a writer whose table mapping needs services
    ///     (e.g. an ORM model) resolves it here. Must be cheap once prepared.
    /// </summary>
    ValueTask PrepareAsync(IServiceProvider services, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    bool AppliesTo(G9CSyncServerTable table);

    /// <summary>
    ///     Checked at server start for every table the writer applies to; return every problem (e.g. a column the persistence
    ///     model cannot write). A non-empty list stops the server with <c>G9S-MODEL-INVALID</c>.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ValidateAsync(G9CSyncServerTable table, IServiceProvider services, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Writes the rows, in <see cref="G9CRowWrite.Seq" /> order, on the push connection and transaction.</summary>
    ValueTask WriteAsync(G9CRowWriteContext context, IReadOnlyList<G9CRowWrite> rows, CancellationToken cancellationToken);
}

/// <summary>
///     Thrown by application code (a row writer, an EF interceptor, a domain service) to reject the row being written with a
///     stable code. The store isolates the row by bisection and reports the code to the device; the other rows apply.
/// </summary>
public sealed class G9CRowRejectedException : Exception
{
    public G9CRowRejectedException()
        : this(G9SyncErrorCodes.RejectValidation, "The row was rejected.")
    {
    }

    public G9CRowRejectedException(string message)
        : this(G9SyncErrorCodes.RejectValidation, message)
    {
    }

    public G9CRowRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = G9SyncErrorCodes.RejectValidation;
    }

    public G9CRowRejectedException(string code, string message, string? details = null)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Details = details;
    }

    /// <summary>Stable code the device receives (an application code is fine).</summary>
    public string Code { get; } = G9SyncErrorCodes.RejectValidation;

    /// <summary>Optional structured detail (JSON or text) for the device's rejection record.</summary>
    public string? Details { get; }
}
