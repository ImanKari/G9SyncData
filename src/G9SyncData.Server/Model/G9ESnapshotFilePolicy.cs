namespace G9SyncData.Server.Model;

/// <summary>Whether a subscription's first download may go as a snapshot file (ADR-0072).</summary>
public enum G9ESnapshotFilePolicy : byte
{
    /// <summary>
    ///     When the device offers it, the server has snapshot files and the snapshot is large enough. The default for every subscription
    ///     that is not ephemeral; files are cached by what they hold, so a subscription many devices share (a site) is built once.
    /// </summary>
    Allowed = 0,

    /// <summary>Always rows - for a subscription whose rows are different for nearly every caller, where a file would rarely be reused.</summary>
    Never = 1
}
