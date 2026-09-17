namespace G9SyncData.Client.Storage;

/// <summary>Where a locally recorded command is (ADR-0025 command path).</summary>
public enum G9ELocalCommandState : byte
{
    /// <summary>Recorded on the device, not yet executed by the server.</summary>
    Pending = 0,

    Ok = 1,
    Rejected = 2,
    Superseded = 3
}

/// <summary>A command recorded on this device.</summary>
public sealed class G9CLocalCommand
{
    public required Guid CommandId { get; init; }
    public required long Sequence { get; init; }
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required string Payload { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public G9ELocalCommandState State { get; init; }

    /// <summary>Rejection code (an application code or a <c>G9S-…</c> code).</summary>
    public string? Code { get; init; }

    public string? Message { get; init; }

    /// <summary>JSON the server's handler returned.</summary>
    public string? Result { get; init; }

    public DateTime? CompletedUtc { get; init; }
}

/// <summary>What to record.</summary>
public sealed class G9CCommandRequest
{
    public required string Name { get; init; }
    public int Version { get; init; } = 1;

    /// <summary>JSON payload for the server's handler.</summary>
    public required string Payload { get; init; }
}

/// <summary>
///     Writes the command's local effects in the same SQLite transaction that records it. Rows written here are NOT uploaded as row
///     changes (the server applies the command itself); they are protected from being overwritten by pulls until the server's
///     result arrives, and then replaced by the server's canonical rows — whether the command succeeded or not.
/// </summary>
public interface IG9LocalCommandWriter
{
    /// <summary>Executes SQL; parameters are bound positionally as <c>$p0</c>, <c>$p1</c>, …</summary>
    Task<int> ExecuteAsync(string sql, params object?[] args);

    Task<object?> ScalarAsync(string sql, params object?[] args);
}
