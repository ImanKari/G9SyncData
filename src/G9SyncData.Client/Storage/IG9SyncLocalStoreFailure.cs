namespace G9SyncData.Client.Storage;

/// <summary>What went wrong inside a client store, in terms an application can act on.</summary>
public enum G9ELocalStoreFailure : byte
{
    /// <summary>Anything not classified below.</summary>
    Other = 0,

    /// <summary>The database file is damaged or is not a database. Nothing fixes this but rebuilding the store from the server.</summary>
    Corrupt = 1,

    /// <summary>The device ran out of storage. Free space and sync again; what was committed before is intact.</summary>
    DiskFull = 2,

    /// <summary>Another connection held the write lock longer than the busy timeout. A later attempt can succeed.</summary>
    Busy = 3
}

/// <summary>
///     Implemented by the exceptions a client store throws, so the engine can give a run's failure a stable code without knowing the
///     store's database (ADR-0068). Until this existed a SQLite error inside a run surfaced as <c>G9S-UNKNOWN</c>: a full disk, a locked
///     database and a corrupt file were indistinguishable to the application, and only one of them is worth retrying.
/// </summary>
public interface IG9SyncLocalStoreFailure
{
    G9ELocalStoreFailure Failure { get; }
}
