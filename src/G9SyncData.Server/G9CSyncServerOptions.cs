namespace G9SyncData.Server;

/// <summary>Server engine options. Defaults are safe for a single instance; multi-instance hosts MUST set <see cref="TokenSigningKey" />.</summary>
public sealed class G9CSyncServerOptions
{
    /// <summary>
    ///     HMAC key (at least 32 bytes) for pull tokens. Null generates a random key per process — fine for one instance,
    ///     but then a pull cannot resume on another instance or after a restart (the client simply re-plans).
    /// </summary>
    public byte[]? TokenSigningKey { get; set; }

    /// <summary>
    ///     Keys that are still ACCEPTED on a pull token but never used to sign one (ADR-0064). To rotate: move the current key here, set
    ///     the new one as <see cref="TokenSigningKey" />, and drop the old one after <see cref="TokenLifetime" /> has passed. Pulls planned
    ///     under the old key finish; without this every pull in flight at the moment of rotation is discarded and planned again.
    /// </summary>
    public IList<byte[]> PreviousTokenSigningKeys { get; } = [];

    /// <summary>
    ///     Refuse to start without a <see cref="TokenSigningKey" /> (ADR-0064). Default false, because one instance with a random key is a
    ///     legitimate development setup. A production host should set it: with a random key every restart and every deploy invalidates
    ///     the pulls in flight, and for a device on a field connection that is a half-downloaded site thrown away and planned again.
    /// </summary>
    public bool RequireTokenSigningKey { get; set; }

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    ///     Pull, reconcile and fetch streams served at the same time; further ones are refused with the retryable
    ///     <c>G9S-SRV-BUSY</c> after <see cref="AdmissionWait" /> (ADR-0062). 0 = unlimited. Default 64.
    /// </summary>
    /// <remarks>
    ///     Every stream holds a database connection and a page of rows in memory. Without a limit a fleet that all opens the same large
    ///     site after a deploy is bounded only by the connection pool, and the first thing to fail is somebody's push. A refused device
    ///     retries with its ordinary backoff and jitter, which is exactly the spreading-out that is wanted.
    /// </remarks>
    public int MaxConcurrentPulls { get; set; } = 64;

    /// <summary>Pushes applied at the same time; see <see cref="MaxConcurrentPulls" />. 0 = unlimited. Default 32.</summary>
    public int MaxConcurrentPushes { get; set; } = 32;

    /// <summary>How long a request waits for a free slot before it is refused as busy.</summary>
    public TimeSpan AdmissionWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Rows per pull page (one chunk per page).</summary>
    public int PageRows { get; set; } = 2000;

    /// <summary>
    ///     The most row bytes (uncompressed) one pull or fetch page carries before it is closed early, whatever <see cref="PageRows" />
    ///     allows. 0 = no byte bound. Default 1 MiB (ADR-0073).
    /// </summary>
    /// <remarks>
    ///     A page is one chunk and one transport message, and a message cannot be split. Bounded by rows alone, a table with an image
    ///     per row made 17.8 MB messages; over LongPolling on a slow link one such message took 25 s, the server saw no poll for more
    ///     than its 15 s disconnect timeout and dropped the connection (404 on the next poll). The bound is checked after each row, so
    ///     one row larger than it still travels, alone. A page closed by bytes resumes after its last row like any other page.
    /// </remarks>
    public int MaxPageBytes { get; set; } = 1024 * 1024;

    public int MaxPushRows { get; set; } = 50_000;

    public int MaxPushBytes { get; set; } = 128 * 1024 * 1024;

    /// <summary>
    ///     The largest DECODED body the server accepts from one pushed frame. <see cref="MaxPushBytes" /> bounds what arrives on the
    ///     wire; a compressed frame declares how large it becomes, and that is what gets allocated, so the two are budgeted separately
    ///     (ADR-0051).
    /// </summary>
    public int MaxDecodedFrameBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>The largest decoded total across every frame in one push.</summary>
    public long MaxDecodedPushBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Effects delivered per claim (ADR-0052).</summary>
    public int EffectBatchSize { get; set; } = 32;

    /// <summary>How long a claimed effect is left alone before another worker may take it, and how long a failed one waits.</summary>
    public TimeSpan EffectRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the dispatcher looks for due effects when nothing nudges it.</summary>
    public TimeSpan EffectPollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>After this many failed deliveries an effect stops being retried and stays visible as failed.</summary>
    public int EffectMaxAttempts { get; set; } = 10;

    /// <summary>A push with more rejected rows than this fails as a whole (<c>G9S-REJ-TOO-MANY</c>): it signals a systematic bug, not bad rows.</summary>
    public int MaxRejectionsPerPush { get; set; } = 50;

    public int MaxFetchKeys { get; set; } = 10_000;

    /// <summary>How long delete tombstones are kept. A client offline longer than this reconciles instead of pulling a delta.</summary>
    public TimeSpan TombstoneRetention { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Listen for committed changes and raise <see cref="G9CSyncChangeNotifier.TablesChanged" /> (drives client pokes).</summary>
    public bool EnableChangeNotifications { get; set; } = true;

    /// <summary>Initialise the store (install/verify capture, validate the model) when the host starts. A failure stops the host.</summary>
    public bool InitializeOnStartup { get; set; } = true;
}
