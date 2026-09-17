namespace G9SyncData.Server;

/// <summary>Server engine options. Defaults are safe for a single instance; multi-instance hosts MUST set <see cref="TokenSigningKey" />.</summary>
public sealed class G9CSyncServerOptions
{
    /// <summary>
    ///     HMAC key (at least 32 bytes) for pull tokens. Null generates a random key per process — fine for one instance,
    ///     but then a pull cannot resume on another instance or after a restart (the client simply re-plans).
    /// </summary>
    public byte[]? TokenSigningKey { get; set; }

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Rows per pull page (one chunk per page).</summary>
    public int PageRows { get; set; } = 2000;

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
