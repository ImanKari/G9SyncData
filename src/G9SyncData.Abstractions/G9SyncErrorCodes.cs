namespace G9SyncData;

/// <summary>
///     Stable, support-facing error codes. A code is a public API: never repurpose one, only add.
///     The client classifies failures by these codes, never by exception text.
/// </summary>
public static class G9SyncErrorCodes
{
    // Transport / availability
    public const string NetworkFailure = "G9S-NET-FAILURE";
    public const string ServerError = "G9S-SRV-ERROR";
    public const string Canceled = "G9S-CANCELED";

    // Identity / version
    public const string Unauthorized = "G9S-AUTH-401";
    public const string UpgradeRequired = "G9S-VER-UPGRADE";
    public const string ProtocolMismatch = "G9S-VER-PROTOCOL";

    // Model / schema
    public const string ModelInvalid = "G9S-MODEL-INVALID";
    public const string SchemaUpgradeRequired = "G9S-SCH-UPGRADE";
    public const string UnknownTable = "G9S-SCH-UNKNOWN-TABLE";

    /// <summary>
    ///     A column the client declares with a type the server's column does not have (ADR-0061). The column is not negotiated, so it
    ///     syncs in NEITHER direction: downloaded rows arrive without it and uploads go up without it. Reported on every run; a run fails
    ///     with it only when the engine is told to (<c>FailOnTypeMismatch</c>). Fix the client model - match the DATABASE type, not the
    ///     shape of the value (a GUID stored in a varchar is a String column).
    /// </summary>
    public const string ModelTypeMismatch = "G9S-MODEL-TYPE-MISMATCH";

    /// <summary>The server is at its limit of concurrent pulls or pushes (ADR-0062). Retry: the engine's backoff applies.</summary>
    public const string ServerBusy = "G9S-SRV-BUSY";

    // Subscriptions
    public const string UnknownSubscription = "G9S-SUB-UNKNOWN";
    public const string InvalidParameters = "G9S-SUB-PARAM";
    public const string SubscriptionForbidden = "G9S-SUB-FORBIDDEN";

    // Cursors and tokens
    public const string ResyncEpoch = "G9S-RSY-EPOCH";
    public const string ResyncHorizon = "G9S-RSY-HORIZON";
    public const string ResyncAuthChanged = "G9S-RSY-AUTH";
    public const string TokenInvalid = "G9S-TOKEN-INVALID";
    public const string TokenExpired = "G9S-TOKEN-EXPIRED";

    // Row rejections (push)
    public const string RejectValidation = "G9S-REJ-VALIDATION";
    public const string RejectConstraint = "G9S-REJ-CONSTRAINT";
    /// <summary>
    ///     No subscription the caller holds authorises uploads to the row's table. The one permission refusal a later push can pass
    ///     without anyone changing data: the device takes out the subscription it was missing, then sends the row again.
    /// </summary>
    public const string RejectPermission = "G9S-REJ-PERMISSION";

    /// <summary>
    ///     The row, as it is on the SERVER, is outside every subscription the caller may write to: an update or delete of a row that
    ///     is no longer the caller's (moved to a site they cannot write, protected by a write rule). Sending it again changes nothing;
    ///     the user decides between their copy and the server's. Until 1.5.9 this was reported as <see cref="RejectPermission" />.
    /// </summary>
    public const string RejectOutsideScope = "G9S-REJ-OUTSIDE-SCOPE";

    /// <summary>
    ///     After the push the row would be outside every subscription the caller may write to: an insert, or a change that moves the
    ///     row, into something the caller cannot write (a parent they do not hold, a value the write rule refuses). It can pass later
    ///     only if the caller's subscriptions change. Until 1.5.9 this was reported as <see cref="RejectPermission" />.
    /// </summary>
    public const string RejectLeavesScope = "G9S-REJ-LEAVES-SCOPE";

    public const string RejectDirection = "G9S-REJ-DIRECTION";
    public const string RejectTooMany = "G9S-REJ-TOO-MANY";

    /// <summary>
    ///     One row is larger than the server accepts in a push (<c>MaxPushBytes</c>): it can never be uploaded as it is. Rejected on the
    ///     device, so every other row still uploads.
    /// </summary>
    public const string RejectTooLarge = "G9S-REJ-TOO-LARGE";
    public const string RejectMalformed = "G9S-REJ-MALFORMED";

    /// <summary>No handler is registered for the command's name and version.</summary>
    public const string CommandUnknown = "G9S-CMD-UNKNOWN";

    /// <summary>A command handler refused the command without an application code.</summary>
    public const string CommandRejected = "G9S-CMD-REJECTED";

    // Conflicts (push)
    public const string ConflictServerWins = "G9S-CON-SERVER-WINS";
    public const string ConflictColumns = "G9S-CON-COLUMNS";
    public const string ConflictDeleted = "G9S-CON-DELETED";
    public const string ConflictResolverRejected = "G9S-CON-RESOLVER";

    // Snapshot files (ADR-0072)

    /// <summary>
    ///     The server could not build or serve a snapshot file it had planned (its disk, its SQLite, a build that failed). The client
    ///     discards the staged download and plans the same pull again as rows: a file is an optimisation, never the only way to the data.
    /// </summary>
    public const string SnapshotFileUnavailable = "G9S-FILE-UNAVAILABLE";

    /// <summary>
    ///     A downloaded snapshot file failed the device's checks (a segment's or the whole file's SHA-256, its length, its tables). Nothing
    ///     of it was applied; the client plans the pull again as rows.
    /// </summary>
    public const string SnapshotFileCorrupt = "G9S-FILE-CORRUPT";

    // Client-local
    public const string LocalStoreError = "G9S-LOCAL-STORE";

    /// <summary>
    ///     The device database is damaged or is not a database (ADR-0068). Retrying cannot help. The application should export what is
    ///     still readable (<c>ExportPendingChangesAsync</c>) and rebuild the store from the server.
    /// </summary>
    public const string LocalStoreCorrupt = "G9S-LOCAL-CORRUPT";

    /// <summary>The device ran out of storage while the store was writing. What was committed before is intact; free space and sync again.</summary>
    public const string LocalStoreDiskFull = "G9S-LOCAL-DISK-FULL";

    /// <summary>Another connection held the device database's write lock longer than the busy timeout. A later run can succeed.</summary>
    public const string LocalStoreBusy = "G9S-LOCAL-BUSY";

    /// <summary>Anything not classified above — treat as a bug to classify.</summary>
    public const string Unknown = "G9S-UNKNOWN";

    /// <summary>True for failures a later attempt may succeed at without any change to data or code.</summary>
    public static bool IsRetryable(string code) =>
        code is NetworkFailure or ServerError or ServerBusy or TokenExpired or ResyncEpoch or ResyncHorizon or ResyncAuthChanged or LocalStoreBusy;
}
