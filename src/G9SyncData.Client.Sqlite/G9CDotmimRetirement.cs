namespace G9SyncData.Client.Sqlite;

/// <summary>
///     What <see cref="G9CSqliteSyncStore.RetireDotmimCaptureAsync" /> retires. The defaults match Dotmim.Sync 1.x's SQLite provider as
///     AgriPad devices have it: triggers <c>&lt;Table&gt;__insert_trigger</c> / <c>__update_trigger</c> / <c>__delete_trigger</c> (any number of
///     underscores is accepted), tracking tables <c>&lt;Table&gt;_tracking</c>, scope tables <c>scope_info</c> and <c>scope_info_client</c>.
/// </summary>
public sealed class G9CDotmimRetirementOptions
{
    /// <summary>Local tables to retire Dotmim capture from. Null: every table of the store's model. Tables outside the model are refused.</summary>
    public IReadOnlyCollection<string>? Tables { get; init; }

    /// <summary>Dotmim <c>SyncOptions</c> naming, if the app changed it.</summary>
    public string TriggersPrefix { get; init; } = string.Empty;

    public string TrackingTablesPrefix { get; init; } = string.Empty;

    public string TrackingTablesSuffix { get; init; } = string.Empty;

    public string ScopeInfoTableName { get; init; } = "scope_info";

    /// <summary>
    ///     Rows Dotmim still considered changed on the device (not uploaded yet) become pending G9SyncData changes - upserts with every column,
    ///     deletes for rows that are gone - so no field edit is lost at cut-over. Rows changed before the oldest scope's last sync, and rows
    ///     Dotmim downloaded, are not imported. Default true.
    /// </summary>
    public bool ImportPendingChanges { get; init; } = true;

    /// <summary>Drop the retired tables' Dotmim tracking tables. Default true.</summary>
    public bool DropTrackingTables { get; init; } = true;

    /// <summary>Drop Dotmim's scope tables once no Dotmim tracking table remains in the database. Default true.</summary>
    public bool DropScopeTablesWhenUnused { get; init; } = true;

    /// <summary>
    ///     Also retire Dotmim's capture from tables OUTSIDE this store's model - for an app that no longer syncs anything through Dotmim
    ///     (a Dotmim scope may have carried reference tables the new model leaves out). Off by default: a table outside the model may
    ///     still sync through a Dotmim scope running side by side. Only whole-model retirements (<see cref="Tables" /> null) do it, and a
    ///     table whose tracking still holds local changes Dotmim had not uploaded is KEPT (reported in
    ///     <see cref="G9CDotmimRetirementResult.KeptOutsideModel" />): those changes cannot be imported, so dropping the capture would
    ///     lose them in silence. Without this, the leftover triggers keep every later download on the slow path (ADR-0034).
    /// </summary>
    public bool RetireTablesOutsideModel { get; init; }
}

/// <summary>What a retirement did. Running it again on the same database does nothing and reports nothing.</summary>
public sealed class G9CDotmimRetirementResult
{
    public List<string> DroppedTriggers { get; } = [];

    public List<string> DroppedTrackingTables { get; } = [];

    public List<string> DroppedScopeTables { get; } = [];

    /// <summary>Pending upserts imported per local table.</summary>
    public Dictionary<string, int> ImportedUpserts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pending deletes imported per local table.</summary>
    public Dictionary<string, int> ImportedDeletes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Triggers the library did not create that remain in the database afterwards. While any remains, downloads apply with trigger execution
    ///     on (ADR-0034); an app trigger is a legitimate reason, a forgotten sync engine's is not.
    /// </summary>
    public List<string> RemainingForeignTriggers { get; } = [];

    /// <summary>Tables outside the model whose Dotmim capture was retired (<see cref="G9CDotmimRetirementOptions.RetireTablesOutsideModel" />).</summary>
    public List<string> RetiredOutsideModel { get; } = [];

    /// <summary>
    ///     Tables outside the model whose Dotmim capture was KEPT because their tracking holds local changes Dotmim had not uploaded
    ///     (table, how many). The app decides what those changes mean; the library cannot import them.
    /// </summary>
    public Dictionary<string, int> KeptOutsideModel { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ChangedAnything => DroppedTriggers.Count + DroppedTrackingTables.Count + DroppedScopeTables.Count > 0;
}
