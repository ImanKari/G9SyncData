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
    public const string RejectPermission = "G9S-REJ-PERMISSION";
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

    // Client-local
    public const string LocalStoreError = "G9S-LOCAL-STORE";

    /// <summary>Anything not classified above — treat as a bug to classify.</summary>
    public const string Unknown = "G9S-UNKNOWN";

    /// <summary>True for failures a later attempt may succeed at without any change to data or code.</summary>
    public static bool IsRetryable(string code) =>
        code is NetworkFailure or ServerError or TokenExpired or ResyncEpoch or ResyncHorizon or ResyncAuthChanged;
}
