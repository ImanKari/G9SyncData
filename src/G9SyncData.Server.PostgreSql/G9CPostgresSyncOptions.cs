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

    /// <summary>LISTEN/NOTIFY channel used by the capture triggers.</summary>
    public string NotificationChannel { get; set; } = "g9sync";

    /// <summary>Command timeout for sync statements.</summary>
    public int CommandTimeoutSeconds { get; set; } = 300;

    /// <summary>Estimated compressed-wire / heap size ratio used for byte estimates in pull plans.</summary>
    public double WireCompressionRatio { get; set; } = 0.35;
}
