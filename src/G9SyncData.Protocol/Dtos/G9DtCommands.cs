namespace G9SyncData.Protocol;

/// <summary>Outcome of a command executed by the server (ADR-0025 command path).</summary>
public enum G9ECommandStatus : byte
{
    /// <summary>The handler ran and its writes committed with the push.</summary>
    Ok = 0,

    /// <summary>The handler refused the command, or it failed with a row-level error; nothing it wrote was kept.</summary>
    Rejected = 1,

    /// <summary>The handler decided the command no longer applies (e.g. overtaken by later state); nothing was written.</summary>
    Superseded = 2
}

/// <summary>
///     A named, versioned intent recorded on the device and executed on the server by the application's own handler, inside the
///     push transaction. Commands of one device run in <see cref="Sequence" /> order, before the pushed rows.
/// </summary>
public sealed class G9DtCommand
{
    /// <summary>Identity for idempotency: a command id already executed for this client returns its stored result.</summary>
    public Guid CommandId { get; set; }

    /// <summary>Device order.</summary>
    public long Sequence { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    /// <summary>JSON payload, interpreted by the handler registered for <see cref="Name" /> and <see cref="Version" />.</summary>
    public string Payload { get; set; } = "{}";

    public long CreatedUtcMs { get; set; }

    /// <summary>Rows the device changed when it applied the command locally; the server returns their canonical state.</summary>
    public List<G9DtRowRef> Touched { get; set; } = [];
}

/// <summary>A row by table id and canonical key.</summary>
public sealed class G9DtRowRef
{
    public int TableId { get; set; }
    public string Key { get; set; } = string.Empty;
}

public sealed class G9DtCommandResult
{
    public Guid CommandId { get; set; }
    public G9ECommandStatus Status { get; set; }

    /// <summary>Stable code for a rejection (an application code, or one of <see cref="G9SyncErrorCodes" />).</summary>
    public string? Code { get; set; }

    public string? Message { get; set; }

    /// <summary>Optional JSON the handler returned (e.g. ids it generated).</summary>
    public string? Result { get; set; }
}
