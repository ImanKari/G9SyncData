using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;

namespace G9SyncData.Server.Pipeline;

/// <summary>What a handler decided (ADR-0025 command path).</summary>
public sealed class G9CCommandResult
{
    private G9CCommandResult(G9ECommandStatus status, string? code, string? message, string? result)
    {
        Status = status;
        Code = code;
        Message = message;
        Result = result;
    }

    public G9ECommandStatus Status { get; }
    public string? Code { get; }
    public string? Message { get; }

    /// <summary>Optional JSON returned to the device (e.g. ids the handler generated).</summary>
    public string? Result { get; }

    public static G9CCommandResult Ok(string? resultJson = null) => new(G9ECommandStatus.Ok, null, null, resultJson);

    /// <summary>Refuses the command. Nothing the handler wrote is kept; the device's local effects are replaced by server state.</summary>
    public static G9CCommandResult Rejected(string code, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new G9CCommandResult(G9ECommandStatus.Rejected, code, message, null);
    }

    /// <summary>The command no longer applies. Nothing the handler wrote is kept.</summary>
    public static G9CCommandResult Superseded(string? message = null) => new(G9ECommandStatus.Superseded, null, message, null);
}

/// <summary>Everything a command handler may use: the push transaction, the caller, and the request's scoped services.</summary>
public sealed class G9CCommandContext
{
    public required Guid CommandId { get; init; }
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required long Sequence { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required Guid ClientId { get; init; }
    public required long BatchId { get; init; }
    public required G9CSyncCaller Caller { get; init; }
    public required G9CSyncAuthContext Auth { get; init; }

    /// <summary>The push connection. Writes MUST use it and <see cref="Transaction" /> (the EF adapter binds a DbContext to both).</summary>
    public required DbConnection Connection { get; init; }

    public required DbTransaction Transaction { get; init; }

    /// <summary>Scoped services of this push request.</summary>
    public required IServiceProvider Services { get; init; }
}

/// <summary>
///     Executes one command, inside the push transaction and under its own savepoint (ADR-0025). The same device's commands run in
///     device order before its rows, each exactly once: a command id that already ran returns its stored result.
///     <para>
///         Return <see cref="G9CCommandResult.Rejected" /> or throw <see cref="G9CRowRejectedException" /> to refuse; a database
///         row-level error (a constraint, a data error) rejects the command with its SQLSTATE. In every refusal the savepoint is
///         rolled back, so nothing the handler wrote remains. Any other exception fails the push as a server error.
///     </para>
///     <para>Handlers must be deterministic with respect to the database state they read: a retried push may run them again.</para>
/// </summary>
public interface IG9CommandHandler
{
    string Name { get; }

    /// <summary>Whether this handler executes <paramref name="version" /> of the command.</summary>
    bool Supports(int version);

    ValueTask<G9CCommandResult> ExecuteAsync(G9CCommandContext context, string payload, CancellationToken cancellationToken);
}

/// <summary>A handler over a typed payload, deserialised with source-generated metadata (AOT-safe).</summary>
public sealed class G9CCommandHandler<TCommand> : IG9CommandHandler
{
    private readonly JsonTypeInfo<TCommand> _typeInfo;
    private readonly Func<G9CCommandContext, TCommand, CancellationToken, ValueTask<G9CCommandResult>> _handler;
    private readonly int _minVersion;
    private readonly int _maxVersion;

    public G9CCommandHandler(string name, JsonTypeInfo<TCommand> typeInfo,
        Func<G9CCommandContext, TCommand, CancellationToken, ValueTask<G9CCommandResult>> handler, int minVersion = 1, int maxVersion = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minVersion, maxVersion);
        Name = name;
        _typeInfo = typeInfo;
        _handler = handler;
        _minVersion = minVersion;
        _maxVersion = maxVersion;
    }

    public string Name { get; }

    public bool Supports(int version) => version >= _minVersion && version <= _maxVersion;

    public ValueTask<G9CCommandResult> ExecuteAsync(G9CCommandContext context, string payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        TCommand? command;
        try
        {
            command = JsonSerializer.Deserialize(payload, _typeInfo);
        }
        catch (JsonException exception)
        {
            return ValueTask.FromResult(G9CCommandResult.Rejected(G9SyncErrorCodes.RejectMalformed, $"The payload of {Name} v{context.Version} is not valid: {exception.Message}"));
        }

        return command is null
            ? ValueTask.FromResult(G9CCommandResult.Rejected(G9SyncErrorCodes.RejectMalformed, $"The payload of {Name} is empty."))
            : _handler(context, command, cancellationToken);
    }
}
