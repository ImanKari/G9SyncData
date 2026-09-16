namespace G9SyncData;

/// <summary>
///     The single exception type G9SyncData throws for expected failures. Always carries a stable
///     <see cref="Code" /> from <see cref="G9SyncErrorCodes" />; callers switch on the code, never on the message.
/// </summary>
public class G9CSyncException : Exception
{
    public G9CSyncException()
        : this(G9SyncErrorCodes.ServerError, "Sync failed.")
    {
    }

    public G9CSyncException(string message)
        : this(G9SyncErrorCodes.ServerError, message)
    {
    }

    public G9CSyncException(string message, Exception innerException)
        : this(G9SyncErrorCodes.ServerError, message, innerException)
    {
    }

    public G9CSyncException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Retryable = G9SyncErrorCodes.IsRetryable(code);
    }

    /// <summary>Stable error code (see <see cref="G9SyncErrorCodes" />).</summary>
    public string Code { get; }

    /// <summary>True when retrying without changes may succeed.</summary>
    public bool Retryable { get; init; }

    /// <summary>Optional structured detail (JSON or text) for logs. Never shown to end users.</summary>
    public string? Details { get; init; }
}
