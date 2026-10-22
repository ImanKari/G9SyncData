namespace G9SyncData.Server.PostgreSql;

/// <summary>Options of the PostgreSQL provider.</summary>
public sealed class G9CPostgresSyncOptions
{
    /// <summary>Schema holding every G9SyncData object (tracking tables, functions, metadata). Domain schemas stay untouched except for three statement triggers per synced table.</summary>
    public string Schema { get; set; } = "g9sync";

    /// <summary>
    ///     Install or update capture objects at startup. Turn off in production when DDL is applied by a reviewed script
    ///     (<see cref="G9CPostgresSyncStore.GenerateInstallScriptAsync" />); startup then only verifies the objects are current.
    /// </summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>
    ///     Probe every scope provider at startup (build its key query with null parameters and an anonymous caller, then EXPLAIN
    ///     it). Turn off only for a provider that cannot build a query without a real caller — its mistakes then surface as
    ///     failed device syncs instead of a failed deployment.
    /// </summary>
    public bool ValidateScopeProviders { get; set; } = true;

    /// <summary>LISTEN/NOTIFY channel used to announce committed changes (by the triggers, and by the WAL consumer).</summary>
    public string NotificationChannel { get; set; } = "g9sync";

    /// <summary>
    ///     How long the change listener waits without a notification before it proves its connection is alive with <c>SELECT 1</c>
    ///     (LES-0076). A connection left half-open - a network blip or a failover that never sent the reset - otherwise waits for
    ///     ever: no error, no pokes, until the process restarts. <see cref="TimeSpan.Zero" /> turns the check off. Default 30 s.
    /// </summary>
    public TimeSpan ListenerHeartbeat { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the listener's <c>SELECT 1</c> may take before its connection counts as dead. Default 15 s.</summary>
    public TimeSpan ListenerHeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Command timeout for sync statements.</summary>
    public int CommandTimeoutSeconds { get; set; } = 300;

    /// <summary>Estimated compressed-wire / heap size ratio used for byte estimates in pull plans.</summary>
    public double WireCompressionRatio { get; set; } = 0.35;

    /// <summary>
    ///     How changes are captured (ADR-0021, ADR-0028).
    ///     <para>
    ///         <see cref="G9EPostgresCaptureMode.Triggers" /> is the default because it needs nothing from the server.
    ///         <see cref="G9EPostgresCaptureMode.WriteAheadLog" /> is the recommended mode for any database that can run
    ///         <c>wal_level=logical</c>: writers outside G9SyncData pay ~1% instead of ~100%. It has infrastructure
    ///         prerequisites — read <c>AiGuides/20-Infrastructure-PostgreSQL.md</c> before selecting it.
    ///     </para>
    /// </summary>
    public G9EPostgresCaptureMode CaptureMode { get; set; } = G9EPostgresCaptureMode.Triggers;

    /// <summary>Settings used only in <see cref="G9EPostgresCaptureMode.WriteAheadLog" /> mode.</summary>
    public G9CPostgresWalOptions Wal { get; } = new();

    /// <summary>
    ///     Subscriptions never served from the digest cache (ADR-0024). Needed only for a scope that reads other tables inside a
    ///     FUNCTION, which the cache's EXPLAIN-based dependency check cannot see.
    /// </summary>
    public ISet<string> DigestCacheExcludedSubscriptions { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    ///     How long executed command outcomes are kept for idempotency (ADR-0025). A device that resends a command older than this
    ///     would run it again: keep it longer than the longest time a device may stay offline with an unacknowledged push.
    /// </summary>
    public TimeSpan CommandLogRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Materialised visibility key sets for provider scopes (ADR-0030).</summary>
    public G9CPostgresKeySetOptions KeySets { get; } = new();
}

/// <summary>Which scopes use materialised key sets.</summary>
public enum G9EKeySetScopes
{
    /// <summary>Only provider scopes (e.g. EF Core queries), which cannot be paged efficiently any other way.</summary>
    ProviderScopes = 0,

    /// <summary>Provider scopes and SQL template scopes, except templates that are literally <c>true</c>.</summary>
    All = 1
}

/// <summary>
///     Materialised visibility key sets: the keys a provider scope (e.g. an EF Core query) admits, built ONCE per snapshot
///     part and paged by primary key, so a part costs O(visible rows) instead of O(pages x visible rows). An accelerator,
///     never an authority: a lost set (idle sweep, crash, restart) is rebuilt on the next page and nothing is skipped.
/// </summary>
public sealed class G9CPostgresKeySetOptions
{
    /// <summary>
    ///     Which scopes page through key sets. <see cref="G9EKeySetScopes.All" /> (default) also covers SQL template scopes: measured
    ///     on the Agriwise copy, a farm of 560 pots paged directly scans the table's whole primary key (182 ms per snapshot,
    ///     1.2 s per reconciliation) against 5 ms and 23 ms through a key set. Scopes that are literally <c>true</c> never use one.
    /// </summary>
    public G9EKeySetScopes Scopes { get; set; } = G9EKeySetScopes.All;

    /// <summary>A set nobody has read for this long is deleted by maintenance. A later page of its part rebuilds it.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum interval between two last-used stamps of one set (each stamp is a write).</summary>
    public TimeSpan TouchInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     A build producing more rows than this is logged as a warning (it still completes: correctness never depends on
    ///     size). Raise it for deployments where very large scopes are expected.
    /// </summary>
    public long WarnRows { get; set; } = 2_000_000;
}

/// <summary>Where committed changes are read from.</summary>
public enum G9EPostgresCaptureMode
{
    /// <summary>Statement triggers record every change in the writer's own transaction (v1).</summary>
    Triggers = 0,

    /// <summary>
    ///     A logical replication consumer records changes made outside G9SyncData; guarded triggers record G9SyncData's own
    ///     pushes synchronously. Requires <c>wal_level=logical</c>, a replication slot and a publication.
    /// </summary>
    WriteAheadLog = 1
}

/// <summary>Write-ahead-log capture settings.</summary>
public sealed class G9CPostgresWalOptions
{
    /// <summary>The publication naming the synced tables. Created and kept in step with the model when AutoInstall is on.</summary>
    public string PublicationName { get; set; } = "g9sync";

    /// <summary>The replication slot this deployment owns. Unique per database cluster; one slot per deployment.</summary>
    public string SlotName { get; set; } = "g9sync";

    /// <summary>
    ///     Connection string for the replication connection. Null uses the data source's own connection string; the role
    ///     needs the REPLICATION attribute.
    /// </summary>
    public string? ReplicationConnectionString { get; set; }

    /// <summary>
    ///     How long a push waits for the consumer to confirm a sync point before failing with a retryable error. A push
    ///     never proceeds without it: its conflict check would otherwise read tracking rows that are missing changes the
    ///     consumer has not applied yet (ADR-0028).
    /// </summary>
    public TimeSpan SyncPointTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delay before the consumer reconnects after a failure. Doubles up to <see cref="MaxRetryDelay" />.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for the consumer's reconnect back-off.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Sync-point rows older than this are purged by the consumer.</summary>
    public TimeSpan SyncPointRetention { get; set; } = TimeSpan.FromMinutes(10);
}
