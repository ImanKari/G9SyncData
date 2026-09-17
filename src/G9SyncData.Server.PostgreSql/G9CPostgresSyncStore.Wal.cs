using System.Diagnostics;
using G9SyncData.Server.Capture;
using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql.Internal;
using Microsoft.Extensions.Logging;
using Npgsql;
using Npgsql.Replication;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     Write-ahead-log capture mode (ADR-0028). The tracking model is unchanged; this file adds its other writers:
///     <list type="bullet">
///         <item>the WAL consumer, recording changes made outside G9SyncData;</item>
///         <item>the push pre-check, recording the current state of the rows a push is about to check;</item>
///         <item>seeding (a baseline for rows that were never tracked) and rescans (recovery after a capture gap).</item>
///     </list>
///     All of them use one statement shape - "record each key's CURRENT state, skip it if nothing synced changed" - so
///     they are idempotent, commutative and cannot disagree.
/// </summary>
public sealed partial class G9CPostgresSyncStore
{
    private const int WalPageSize = 5_000;

    internal bool IsWalMode => _options.CaptureMode == G9EPostgresCaptureMode.WriteAheadLog;

    internal G9CPostgresSyncOptions Options => _options;

    internal G9CSyncServerModel Model => _model;

    /// <summary>
    ///     The replication connection string. <see cref="G9CPostgresWalOptions.ReplicationConnectionString" /> when set;
    ///     otherwise the data source's own string, which must then carry credentials the role can replicate with.
    /// </summary>
    internal string ReplicationConnectionString => _options.Wal.ReplicationConnectionString ?? _dataSource.ConnectionString;

    // ================================================================ startup

    /// <summary>
    ///     WAL-mode startup, in the only safe order: prerequisites, publication, SLOT, then seeding. The slot must exist
    ///     before the baseline is read, or a change committed between the two would be in neither.
    /// </summary>
    private async Task InitializeWalAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var walLevel = await ScalarAsync<string>(connection, null, "SELECT current_setting('wal_level')", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(walLevel, "logical", StringComparison.Ordinal))
        {
            throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                $"CaptureMode is WriteAheadLog but the server runs wal_level={walLevel}. Set wal_level=logical (a restart is " +
                "required) or use CaptureMode.Triggers. See AiGuides/20-Infrastructure-PostgreSQL.md.");
        }

        await EnsurePublicationAsync(connection, cancellationToken).ConfigureAwait(false);
        var gap = await EnsureSlotAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (var table in _tables.Values.OrderBy(t => t.Model.Order))
        {
            await SeedTableAsync(connection, table, cancellationToken).ConfigureAwait(false);
        }

        if (gap)
        {
            await ExecuteAsync(connection, null, $"UPDATE {_schema}.install SET wal_rescan_pending = true WHERE id = 1", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Trigger mode must not leave a slot behind: an unconsumed slot retains WAL until the disk fills. Seeding is also
    ///     invalidated, because trigger-mode changes do not maintain column hashes.
    /// </summary>
    private async Task DisableWalArtifactsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var slot = await NullableTextAsync(connection, $"SELECT wal_slot FROM {_schema}.install WHERE id = 1", cancellationToken)
            .ConfigureAwait(false);
        if (slot is null) return;

        await ExecuteAsync(connection, null,
            "SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name = @g9_slot AND NOT active", cancellationToken,
            P("g9_slot", NpgsqlDbType.Text, slot)).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"UPDATE {_schema}.install SET wal_slot = NULL, wal_rescan_pending = false WHERE id = 1; " +
                                             $"UPDATE {_schema}.tables SET wal_seeded = false", cancellationToken).ConfigureAwait(false);
        LogWalDisabled(_logger, slot);
    }

    private async Task EnsurePublicationAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var publication = _options.Wal.PublicationName;
        var tableList = string.Join(", ", _tables.Values.OrderBy(t => t.TableId).Select(t => t.BaseName));
        var exists = await ScalarAsync<bool>(connection, null, "SELECT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = @g9_name)",
            cancellationToken, P("g9_name", NpgsqlDbType.Text, publication)).ConfigureAwait(false);

        if (!_options.AutoInstall)
        {
            if (!exists)
            {
                throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                    $"Publication '{publication}' does not exist and AutoInstall is off. Create it with: CREATE PUBLICATION {PgNames.Quote(publication)} FOR TABLE {tableList};");
            }

            return;
        }

        // Explicit table list, never FOR ALL TABLES (AiGuides/20 section 3). SET TABLE keeps it in step with the model.
        var ddl = exists
            ? $"ALTER PUBLICATION {PgNames.Quote(publication)} SET TABLE {tableList}"
            : $"CREATE PUBLICATION {PgNames.Quote(publication)} FOR TABLE {tableList}";
        await ExecuteAsync(connection, null, ddl, cancellationToken).ConfigureAwait(false);
        LogPublicationReady(_logger, publication, _tables.Count);
    }

    /// <summary>
    ///     Makes sure the slot exists and has not lost WAL. Returns true when there is a capture GAP: a slot this installation
    ///     previously owned is missing or invalidated, so changes may have been committed that no slot recorded.
    /// </summary>
    private async Task<bool> EnsureSlotAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var slot = _options.Wal.SlotName;
        var previous = await NullableTextAsync(connection, $"SELECT wal_slot FROM {_schema}.install WHERE id = 1", cancellationToken)
            .ConfigureAwait(false);

        string? status;
        await using (var command = Command(connection, null, "SELECT wal_status FROM pg_replication_slots WHERE slot_name = @g9_slot"))
        {
            command.Parameters.Add(P("g9_slot", NpgsqlDbType.Text, slot));
            status = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }

        var healthy = status is "reserved" or "extended";
        if (healthy)
        {
            if (!string.Equals(previous, slot, StringComparison.Ordinal))
            {
                await ExecuteAsync(connection, null, $"UPDATE {_schema}.install SET wal_slot = @g9_slot WHERE id = 1", cancellationToken,
                    P("g9_slot", NpgsqlDbType.Text, slot)).ConfigureAwait(false);
            }

            return false;
        }

        if (status is not null)
        {
            // 'unreserved' or 'lost': the WAL it needed is gone.
            await ExecuteAsync(connection, null,
                "SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name = @g9_slot", cancellationToken,
                P("g9_slot", NpgsqlDbType.Text, slot)).ConfigureAwait(false);
        }

        await using (var replication = new LogicalReplicationConnection(ReplicationConnectionString))
        {
            await replication.Open(cancellationToken).ConfigureAwait(false);
            await replication.CreatePgOutputReplicationSlot(slot, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, null, $"UPDATE {_schema}.install SET wal_slot = @g9_slot WHERE id = 1", cancellationToken,
            P("g9_slot", NpgsqlDbType.Text, slot)).ConfigureAwait(false);

        var gap = status is not null || string.Equals(previous, slot, StringComparison.Ordinal);
        LogSlotCreated(_logger, slot, gap);
        return gap;
    }

    // ================================================================ seeding and rescans

    /// <summary>
    ///     One-time baseline for rows that were never tracked, plus column hashes for tracked rows that lack them. Versions do
    ///     not move and nothing is delivered to devices: the baseline describes rows devices already hold at version 0. Without
    ///     it, a device's first edit of such a row would look like a conflict to the push pre-check.
    /// </summary>
    private async Task SeedTableAsync(NpgsqlConnection connection, PgTable table, CancellationToken cancellationToken)
    {
        var seeded = await ScalarAsync<bool>(connection, null, $"SELECT wal_seeded FROM {_schema}.tables WHERE table_id = @g9_id", cancellationToken,
            P("g9_id", NpgsqlDbType.Integer, table.TableId)).ConfigureAwait(false);
        if (seeded) return;

        var stopwatch = Stopwatch.StartNew();
        var rows = 0L;
        string? after = null;
        var key = table.Key.Quoted;

        while (true)
        {
            string? upto;
            await using (var page = Command(connection, null,
                             // No max() aggregate exists for uuid, so the page's last key is taken by ordering.
                             $"SELECT p.key_value::text FROM (SELECT t.{key} AS key_value FROM {table.BaseName} AS t " +
                             $"WHERE (@g9_after::text IS NULL OR t.{key} > @g9_after) ORDER BY t.{key} LIMIT {WalPageSize}) AS p " +
                             "ORDER BY p.key_value DESC LIMIT 1"))
            {
                page.Parameters.Add(KeyParameterOrNull("g9_after", table, after));
                upto = await page.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            }

            if (upto is null) break;

            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            await using (var seed = Command(connection, transaction, PgCaptureSql.WalSeedRange(table, _schema)))
            {
                seed.Parameters.Add(KeyParameterOrNull("g9_after", table, after));
                seed.Parameters.Add(KeyParameter("g9_upto", table, upto));
                rows += await seed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            after = upto;
        }

        await ExecuteAsync(connection, null, $"UPDATE {_schema}.tables SET wal_seeded = true WHERE table_id = @g9_id", cancellationToken,
            P("g9_id", NpgsqlDbType.Integer, table.TableId)).ConfigureAwait(false);
        LogTableSeeded(_logger, table.Model.LogicalName, rows, stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    ///     Recovery after a capture gap: records the current state of every key, so rows changed, added or deleted while no
    ///     slot was recording reach devices through ordinary pulls. Unchanged rows are skipped by their hashes, so only the
    ///     gap moves - no device has to resynchronise everything.
    /// </summary>
    internal async Task<long> RescanPendingAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var pending = await ScalarAsync<bool>(connection, null, $"SELECT wal_rescan_pending FROM {_schema}.install WHERE id = 1", cancellationToken)
            .ConfigureAwait(false);
        if (!pending) return 0;

        var stopwatch = Stopwatch.StartNew();
        long recorded = 0;
        foreach (var table in _tables.Values.OrderBy(t => t.Model.Order))
        {
            var key = table.Key.Quoted;
            string? after = null;
            while (true)
            {
                var keys = new List<string>(WalPageSize);
                await using (var page = Command(connection, null,
                                 $"SELECT u.key_value::text FROM (SELECT t.{key} AS key_value FROM {table.BaseName} AS t WHERE (@g9_after::text IS NULL OR t.{key} > @g9_after) " +
                                 $"UNION SELECT k.{key} FROM {table.TrackingName} AS k WHERE NOT k.deleted AND (@g9_after::text IS NULL OR k.{key} > @g9_after)) AS u " +
                                 $"ORDER BY u.key_value LIMIT {WalPageSize}"))
                {
                    page.Parameters.Add(KeyParameterOrNull("g9_after", table, after));
                    await using var reader = await page.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) keys.Add(reader.GetString(0));
                }

                if (keys.Count == 0) break;
                recorded += await RecordKeysAsync(table, keys, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
                after = keys[^1];
                if (keys.Count < WalPageSize) break;
            }
        }

        await ExecuteAsync(connection, null, $"UPDATE {_schema}.install SET wal_rescan_pending = false WHERE id = 1", cancellationToken)
            .ConfigureAwait(false);
        LogRescanned(_logger, recorded, stopwatch.ElapsedMilliseconds);
        G9CPostgresWalTelemetry.Rescans.Add(1);
        return recorded;
    }

    /// <summary>
    ///     Called by the consumer before every attach: re-creates a missing or invalidated slot and, when that means changes
    ///     went unrecorded, schedules a rescan. Idempotent and cheap when the slot is healthy.
    /// </summary>
    internal async Task EnsureSlotForConsumerAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (await EnsureSlotAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, null, $"UPDATE {_schema}.install SET wal_rescan_pending = true WHERE id = 1", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // ================================================================ the consumer's write

    /// <summary>
    ///     Records one source transaction's changes and announces them. The caller confirms the WAL position only after this
    ///     returns (LES-0015). The event KIND is deliberately not used - see <see cref="RecordKeysAsync" />.
    /// </summary>
    internal async Task<int> ApplyWalBatchAsync(G9CChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        EnsureInitialized();
        if (batch.Rows.Count == 0) return 0;

        var byTable = new Dictionary<PgTable, List<string>>();
        foreach (var row in batch.Rows)
        {
            if (!_byLogical.TryGetValue(row.TableLogicalName, out var table)) continue;
            if (!byTable.TryGetValue(table, out var keys)) byTable[table] = keys = [];
            keys.Add(row.Key);
        }

        var written = 0;
        foreach (var (table, keys) in byTable.OrderBy(pair => pair.Key.Model.Order))
        {
            written += await RecordKeysAsync(table, keys, batch.CommitTimestampUtc, cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>
    ///     Records each key's CURRENT state - present or deleted, with its column hashes - and skips keys whose synced columns
    ///     did not change. That makes redelivery idempotent, folds a later change already visible in the table into this one,
    ///     and skips updates that touched no synced column, exactly as the triggers do.
    /// </summary>
    private async Task<int> RecordKeysAsync(PgTable table, IReadOnlyList<string> keys, DateTime changedAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int changed;
        await using (var command = Command(connection, transaction, PgCaptureSql.WalRecordKeys(table, _schema)))
        {
            command.Parameters.Add(P("g9_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, keys.ToArray()));
            command.Parameters.Add(P("g9_changed_at", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(changedAtUtc, DateTimeKind.Utc)));
            changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (changed > 0)
        {
            // Same channel and payload as the triggers, so devices are poked exactly as in trigger mode.
            await ExecuteAsync(connection, transaction, "SELECT pg_notify(@g9_channel, @g9_table)", cancellationToken,
                P("g9_channel", NpgsqlDbType.Text, _options.NotificationChannel),
                P("g9_table", NpgsqlDbType.Text, table.Model.LogicalName)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    // ================================================================ the push's pre-check

    /// <summary>
    ///     Marks this transaction as a G9SyncData push in the WAL, so the consumer skips rows the push already tracked. An
    ///     optimisation only: the consumer would find their hashes equal and skip them anyway.
    /// </summary>
    private async Task EmitPushMarkerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid clientId, CancellationToken ct)
    {
        await ExecuteAsync(connection, transaction, "SELECT pg_logical_emit_message(true, @g9_prefix, @g9_client)", ct,
            P("g9_prefix", NpgsqlDbType.Text, G9CChangeMessages.PushPrefix),
            P("g9_client", NpgsqlDbType.Text, clientId.ToString())).ConfigureAwait(false);
    }

    /// <summary>
    ///     Locks the rows the push will check and records their current state in its own transaction (ADR-0028). After this the
    ///     tracking rows the conflict check reads describe every committed change to those rows - whether or not the consumer
    ///     has reached it - and nothing can change them until the push finishes. Push correctness therefore never waits for, or
    ///     depends on, the consumer.
    /// </summary>
    private async Task LockAndRecordCurrentStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table, string stage,
        CancellationToken ct)
    {
        var key = table.Key.Quoted;

        // Sorted lock order in both the push and the consumer: the two can then never deadlock on tracking rows.
        await ExecuteAsync(connection, transaction,
            $"SELECT 1 FROM {table.BaseName} AS t WHERE EXISTS (SELECT 1 FROM {stage} AS s WHERE s.g9_key = t.{key} AND s.g9_action = 1) " +
            $"ORDER BY t.{key} FOR UPDATE OF t", ct).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction, PgCaptureSql.WalRecordStaged(table, _schema, stage), ct,
            P("g9_changed_at", NpgsqlDbType.TimestampTz, DateTime.UtcNow)).ConfigureAwait(false);
    }

    // ================================================================ waiting for capture (applications and tests)

    /// <summary>
    ///     Waits until the WAL consumer has recorded every transaction committed before this call. Useful after a server-side
    ///     write whose rows should reach devices on the very next pull; pushes never need it. Returns immediately in trigger
    ///     mode, where committed means tracked.
    /// </summary>
    /// <exception cref="TimeoutException">The consumer did not confirm within <paramref name="timeout" />.</exception>
    public async Task WaitForCaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!IsWalMode) return;
        EnsureInitialized();

        var stopwatch = Stopwatch.StartNew();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        while (stopwatch.Elapsed < timeout)
        {
            // Re-emit per attempt, so a consumer that attaches late still answers one of them.
            var nonce = Guid.NewGuid();
            await ExecuteAsync(connection, null, "SELECT pg_logical_emit_message(false, @g9_prefix, @g9_nonce)", cancellationToken,
                P("g9_prefix", NpgsqlDbType.Text, G9CChangeMessages.SyncPointPrefix),
                P("g9_nonce", NpgsqlDbType.Text, nonce.ToString())).ConfigureAwait(false);

            var attemptEnds = stopwatch.Elapsed + TimeSpan.FromMilliseconds(750);
            var delay = TimeSpan.FromMilliseconds(2);
            while (stopwatch.Elapsed < attemptEnds && stopwatch.Elapsed < timeout)
            {
                if (await ScalarAsync<bool>(connection, null, $"SELECT EXISTS (SELECT 1 FROM {_schema}.wal_sync_points WHERE nonce = @g9_nonce)",
                        cancellationToken, P("g9_nonce", NpgsqlDbType.Uuid, nonce)).ConfigureAwait(false))
                {
                    G9CPostgresWalTelemetry.CaptureWait.Record(stopwatch.Elapsed.TotalMilliseconds);
                    return;
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(50, delay.TotalMilliseconds * 2));
            }
        }

        throw new TimeoutException($"The G9SyncData WAL consumer did not confirm capture within {timeout.TotalSeconds:0.#} s.");
    }

    /// <summary>Answers a sync point. Idempotent: a redelivered marker is simply recorded again.</summary>
    internal async Task RecordSyncPointAsync(Guid nonce, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            $"INSERT INTO {_schema}.wal_sync_points (nonce) VALUES (@g9_nonce) ON CONFLICT (nonce) DO NOTHING", cancellationToken,
            P("g9_nonce", NpgsqlDbType.Uuid, nonce)).ConfigureAwait(false);
    }

    internal async Task<int> PurgeSyncPointsAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"DELETE FROM {_schema}.wal_sync_points WHERE seen_at < now() - @g9_age");
        command.Parameters.Add(P("g9_age", NpgsqlDbType.Interval, retention));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a text value that may be NULL (the store's ScalarAsync helper rejects DBNull by design).</summary>
    private async Task<string?> NullableTextAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, null, sql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    /// <summary>A key parameter that may be NULL. The NULL must still carry the key's type, or a comparison with the key column fails to resolve.</summary>
    private static NpgsqlParameter KeyParameterOrNull(string name, PgTable table, string? key) =>
        key is not null
            ? KeyParameter(name, table, key)
            : new NpgsqlParameter(name, table.KeyStagingDbType) { Value = DBNull.Value };

    [LoggerMessage(EventId = 7310, Level = LogLevel.Information, Message = "G9SyncData publication '{Publication}' covers {TableCount} table(s)")]
    private static partial void LogPublicationReady(ILogger logger, string publication, int tableCount);

    [LoggerMessage(EventId = 7311, Level = LogLevel.Warning,
        Message = "G9SyncData created replication slot '{SlotName}' (capture gap: {Gap}); a gap is repaired by a rescan")]
    private static partial void LogSlotCreated(ILogger logger, string slotName, bool gap);

    [LoggerMessage(EventId = 7312, Level = LogLevel.Information, Message = "G9SyncData seeded {Table}: {Rows} row(s) in {ElapsedMs} ms")]
    private static partial void LogTableSeeded(ILogger logger, string table, long rows, long elapsedMs);

    [LoggerMessage(EventId = 7313, Level = LogLevel.Warning, Message = "G9SyncData rescan after a capture gap recorded {Rows} changed row(s) in {ElapsedMs} ms")]
    private static partial void LogRescanned(ILogger logger, long rows, long elapsedMs);

    [LoggerMessage(EventId = 7314, Level = LogLevel.Warning, Message = "G9SyncData runs in trigger mode; dropped WAL-mode slot '{SlotName}' so it cannot retain WAL")]
    private static partial void LogWalDisabled(ILogger logger, string slotName);
}
