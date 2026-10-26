using System.Globalization;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Sqlite.Native;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Client.Sqlite;

/// <summary>Cut-over from Dotmim.Sync on the device (ADR-0038).</summary>
public sealed partial class G9CSqliteSyncStore
{
    private static readonly string[] DotmimTriggerSuffixes = ["_insert_trigger", "_update_trigger", "_delete_trigger"];

    /// <summary>
    ///     The triggers in the database that the library did not create. While any exists, downloads apply with trigger execution on
    ///     (ADR-0034) - the reason is usually an app trigger (legitimate) or a retired sync engine's (see <see cref="RetireDotmimCaptureAsync" />).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetForeignTriggersAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return ForeignTriggerNames(connection, null);
    }

    /// <summary>
    ///     Removes Dotmim.Sync's capture from tables this store now syncs: its triggers (identified by name AND by a body that writes Dotmim's
    ///     tracking table, so an app trigger with a similar name survives), optionally its tracking tables and - once none is left - its scope
    ///     tables. Rows Dotmim had not uploaded yet become pending changes first (<see cref="G9CDotmimRetirementOptions.ImportPendingChanges" />).
    ///     One transaction; running it again is a no-op.
    /// </summary>
    /// <remarks>
    ///     Stop syncing the tables through Dotmim first (a Dotmim scope still listing a table re-creates its triggers on its next sync).
    ///     Imported changes carry no base version: where the server row changed since capture was installed, the table's conflict policy
    ///     decides - the same rule as <see cref="MarkDirtyAsync" />.
    /// </remarks>
    public async Task<G9CDotmimRetirementResult> RetireDotmimCaptureAsync(G9CDotmimRetirementOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new G9CDotmimRetirementOptions();
        EnsureInitialized();
        var tables = options.Tables is null ? _byLocal.Values.ToList() : options.Tables.Select(GetLocal).ToList();
        var result = new G9CDotmimRetirementResult();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                var lastSync = options.ImportPendingChanges ? OldestScopeSync(connection, transaction, options.ScopeInfoTableName + "_client") : null;
                foreach (var table in tables)
                {
                    var tracking = options.TrackingTablesPrefix + table.Model.LocalName + options.TrackingTablesSuffix + "_tracking";
                    var trackingValid = IsDotmimTrackingTable(connection, transaction, tracking, table.Model.KeyColumn);
                    if (options.ImportPendingChanges && trackingValid)
                    {
                        var uploadedUpTo = LastUploadOf(connection, transaction, options.ScopeInfoTableName, table.Model.LocalName, lastSync);
                        ImportDotmimChanges(connection, transaction, table, tracking, uploadedUpTo, result);
                    }

                    foreach (var trigger in DotmimTriggers(connection, transaction, table.Model.LocalName, tracking, options.TriggersPrefix))
                    {
                        Exec(connection, transaction, "DROP TRIGGER " + SqliteSql.Quote(trigger));
                        result.DroppedTriggers.Add(trigger);
                    }

                    if (options.DropTrackingTables && trackingValid)
                    {
                        Exec(connection, transaction, "DROP TABLE " + SqliteSql.Quote(tracking));
                        result.DroppedTrackingTables.Add(tracking);
                    }
                }

                if (options.RetireTablesOutsideModel && options.Tables is null)
                {
                    RetireOutsideModel(connection, transaction, options, lastSync, result);
                }

                if (options.DropScopeTablesWhenUnused && !AnyDotmimTrackingTable(connection, transaction))
                {
                    foreach (var scopeTable in new[] { options.ScopeInfoTableName, options.ScopeInfoTableName + "_client" })
                    {
                        if (Scalar(connection, transaction, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n COLLATE NOCASE", ("$n", scopeTable)) is null) continue;
                        Exec(connection, transaction, "DROP TABLE " + SqliteSql.Quote(scopeTable));
                        result.DroppedScopeTables.Add(scopeTable);
                    }
                }

                transaction.Commit();
            }

            result.RemainingForeignTriggers.AddRange(ForeignTriggerNames(connection, null));
        }
        finally
        {
            _writeLock.Release();
        }

        if (result.ChangedAnything)
        {
            LogDotmimRetired(_logger, result.DroppedTriggers.Count, result.DroppedTrackingTables.Count, result.DroppedScopeTables.Count,
                result.ImportedUpserts.Values.Sum(), result.ImportedDeletes.Values.Sum(), result.RemainingForeignTriggers.Count);
        }

        return result;
    }

    /// <summary>
    ///     Dotmim's capture on tables outside the model (<see cref="G9CDotmimRetirementOptions.RetireTablesOutsideModel" />): each tracking
    ///     table of Dotmim's shape whose base table the model does not hold goes, with that table's Dotmim triggers - unless it still holds
    ///     local changes Dotmim had not uploaded (they cannot be imported, so the capture is kept and reported).
    /// </summary>
    private void RetireOutsideModel(SqliteConnection connection, SqliteTransaction transaction, G9CDotmimRetirementOptions options, long? lastSync,
        G9CDotmimRetirementResult result)
    {
        var candidates = new List<string>();
        using (var command = Command(connection, transaction, @"SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%\_tracking' ESCAPE '\'"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) candidates.Add(reader.GetString(0));
        }

        foreach (var tracking in candidates)
        {
            var baseName = tracking[..^"_tracking".Length];
            if (options.TrackingTablesSuffix.Length > 0)
            {
                if (!baseName.EndsWith(options.TrackingTablesSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                baseName = baseName[..^options.TrackingTablesSuffix.Length];
            }

            if (options.TrackingTablesPrefix.Length > 0)
            {
                if (!baseName.StartsWith(options.TrackingTablesPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                baseName = baseName[options.TrackingTablesPrefix.Length..];
            }

            if (_byLocal.ContainsKey(baseName) || !IsDotmimTrackingTable(connection, transaction, tracking, keyColumn: null)) continue;

            object? last = LastUploadOf(connection, transaction, options.ScopeInfoTableName, baseName, lastSync);
            var pending = Convert.ToInt32(Scalar(connection, transaction,
                $"SELECT count(*) FROM {SqliteSql.Quote(tracking)} WHERE update_scope_id IS NULL AND ($last IS NULL OR [timestamp] > $last)", ("$last", last)),
                CultureInfo.InvariantCulture);
            if (pending > 0)
            {
                result.KeptOutsideModel[baseName] = pending;
                continue;
            }

            foreach (var trigger in DotmimTriggers(connection, transaction, baseName, tracking, options.TriggersPrefix))
            {
                Exec(connection, transaction, "DROP TRIGGER " + SqliteSql.Quote(trigger));
                result.DroppedTriggers.Add(trigger);
            }

            if (options.DropTrackingTables)
            {
                Exec(connection, transaction, "DROP TABLE " + SqliteSql.Quote(tracking));
                result.DroppedTrackingTables.Add(tracking);
            }

            result.RetiredOutsideModel.Add(baseName);
        }
    }

    /// <summary>
    ///     The oldest last-sync stamp over Dotmim's scope clients, or null when any scope never synced (then every local change is pending).
    ///     Dotmim stamps tracking rows and scopes with the same <c>yyyyMMddHHmmssfff</c> integer.
    /// </summary>
    private static long? OldestScopeSync(SqliteConnection connection, SqliteTransaction transaction, string scopeClientTable)
    {
        if (Scalar(connection, transaction, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n COLLATE NOCASE", ("$n", scopeClientTable)) is null) return null;
        return Scalar(connection, transaction,
            $"SELECT CASE WHEN count(*) > 0 AND count(*) = count(scope_last_sync_timestamp) THEN min(scope_last_sync_timestamp) END FROM {SqliteSql.Quote(scopeClientTable)}") is long stamp
            ? stamp
            : null;
    }

    /// <summary>
    ///     The stamp up to which Dotmim had uploaded <paramref name="table" />'s local changes: the NEWEST last sync of the scope clients whose
    ///     scope lists the table (<c>"tn":"&lt;table&gt;"</c> in <c>scope_info.sync_scope_setup</c>, Dotmim 1.3's serialised setup), null when
    ///     none of them ever synced, and <paramref name="oldestFallback" /> when no scope can be matched to the table.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A Dotmim client uploads every local change of a scope's tables made since that scope client's previous sync, filters or not, so a
    ///         change older than the newest sync of ANY scope holding its table has reached the server. The device-wide oldest stamp used before
    ///         (1.5.11 and earlier) treated it as unsent whenever ANY scope - a site opened a month ago - had not synced since: the upgrade
    ///         (pre-release test 2026-10-26, 5.9.7 → 6.2.20) then re-sent work that had long been uploaded, with every column marked changed and
    ///         no base version. On a server whose capture was installed after the old server stopped (every first deploy) that resend wins
    ///         silently, so an office edit the phone had not downloaded yet was overwritten by the phone's old copy; where capture already
    ///         existed it came back as conflicts the user had to settle for work that was never in conflict.
    ///     </para>
    ///     <para>
    ///         No scope matching the table (an older Dotmim layout, or setups without the table names) keeps the old, conservative rule: an
    ///         over-eager resend is recoverable, an unsent change counted as sent is lost.
    ///     </para>
    /// </remarks>
    private static long? LastUploadOf(SqliteConnection connection, SqliteTransaction transaction, string scopeInfoTable, string table, long? oldestFallback)
    {
        var clientTable = scopeInfoTable + "_client";
        foreach (var name in new[] { scopeInfoTable, clientTable })
        {
            if (Scalar(connection, transaction, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n COLLATE NOCASE", ("$n", name)) is null) return oldestFallback;
        }

        using var command = Command(connection, transaction,
            $"SELECT count(*), count(c.scope_last_sync_timestamp), max(c.scope_last_sync_timestamp) FROM {SqliteSql.Quote(clientTable)} AS c " +
            $"JOIN {SqliteSql.Quote(scopeInfoTable)} AS s ON s.sync_scope_name = c.sync_scope_name " +
            "WHERE instr(lower(s.sync_scope_setup), $tn) > 0");
        command.Parameters.AddWithValue("$tn", "\"tn\":\"" + table.ToLowerInvariant() + "\"");
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) == 0) return oldestFallback;
        return reader.GetInt64(1) == 0 ? null : reader.GetInt64(2);
    }

    private void ImportDotmimChanges(SqliteConnection connection, SqliteTransaction transaction, LocalTable table, string tracking, long? lastSync,
        G9CDotmimRetirementResult result)
    {
        var key = SqliteSql.Quote(table.Model.KeyColumn);
        var pending = $"FROM {SqliteSql.Quote(tracking)} AS k WHERE k.update_scope_id IS NULL AND ($last IS NULL OR k.[timestamp] > $last)";
        var present = $"EXISTS (SELECT 1 FROM {table.Quoted} AS b WHERE b.{key} = k.{key})";
        var keys = SqliteSql.KeyExpression(table.Model, "k");
        object? last = lastSync;

        var upserts = MarkPending(connection, transaction, table,
            $"SELECT {keys} AS pk {pending} AND k.sync_row_is_tombstone = 0 AND {present}", [("$last", last)],
            "dirty = 1, dirty_mask = dirty_mask | " + SqliteSql.Literal(SqliteSql.AllColumnsMask(table.Model.Columns.Count)));
        var deletes = MarkPending(connection, transaction, table,
            $"SELECT {keys} AS pk {pending} AND k.sync_row_is_tombstone <> 0 AND NOT {present}", [("$last", last)],
            "dirty = 2, dirty_mask = 0");

        if (upserts > 0) result.ImportedUpserts[table.Model.LocalName] = upserts;
        if (deletes > 0) result.ImportedDeletes[table.Model.LocalName] = deletes;
    }

    /// <summary>Dotmim's triggers on <paramref name="table" />: <c>{prefix}{table}…_{insert|update|delete}_trigger</c> whose body writes the tracking table.</summary>
    private static List<string> DotmimTriggers(SqliteConnection connection, SqliteTransaction transaction, string table, string tracking, string prefix)
    {
        var found = new List<string>();
        using var command = Command(connection, transaction, "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND tbl_name = $t COLLATE NOCASE");
        command.Parameters.AddWithValue("$t", table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var sql = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (!name.StartsWith(prefix + table, StringComparison.OrdinalIgnoreCase)) continue;
            if (!DotmimTriggerSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (!sql.Contains(tracking, StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("update_scope_id", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("sync_row_is_tombstone", StringComparison.OrdinalIgnoreCase)) continue;
            found.Add(name);
        }

        return found;
    }

    /// <summary>A table with Dotmim's tracking shape: the key column plus update_scope_id, timestamp and sync_row_is_tombstone.</summary>
    private static bool IsDotmimTrackingTable(SqliteConnection connection, SqliteTransaction transaction, string name, string? keyColumn)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = Command(connection, transaction, "SELECT name FROM pragma_table_info($n)"))
        {
            command.Parameters.AddWithValue("$n", name);
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(0));
        }

        return columns.Contains("update_scope_id") && columns.Contains("timestamp") && columns.Contains("sync_row_is_tombstone")
               && (keyColumn is null || columns.Contains(keyColumn));
    }

    private static bool AnyDotmimTrackingTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        var candidates = new List<string>();
        using (var command = Command(connection, transaction, "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%\\_tracking' ESCAPE '\\'"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) candidates.Add(reader.GetString(0));
        }

        return candidates.Any(name => IsDotmimTrackingTable(connection, transaction, name, keyColumn: null));
    }

    private List<string> ForeignTriggerNames(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var foreign = new List<string>();
        using var command = Command(connection, transaction, "SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (!IsLibraryTrigger(name)) foreign.Add(name);
        }

        return foreign;
    }

    [LoggerMessage(EventId = 20120, Level = LogLevel.Information,
        Message = "G9SyncData retired Dotmim capture: {Triggers} trigger(s), {TrackingTables} tracking table(s), {ScopeTables} scope table(s) dropped; " +
                  "{Upserts} pending upsert(s) and {Deletes} pending delete(s) imported; {Remaining} foreign trigger(s) remain")]
    private static partial void LogDotmimRetired(ILogger logger, int triggers, int trackingTables, int scopeTables, int upserts, int deletes, int remaining);
}
