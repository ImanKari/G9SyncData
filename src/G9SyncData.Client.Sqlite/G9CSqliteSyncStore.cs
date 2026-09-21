using System.Globalization;
using System.Text.Json;
using G9SyncData.Client.Model;
using G9SyncData.Client.Sqlite.Internal;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Client.Sqlite.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     SQLite implementation of <see cref="IG9SyncClientStore" />. Every object it creates is listed in
///     AiGuides/08-Client-Sqlite.md. Library writes are serialised in-process; app writes on other connections are
///     captured by triggers and wait on SQLite's busy timeout.
/// </summary>
public sealed partial class G9CSqliteSyncStore : IG9SyncClientStore
{
    private readonly G9CSqliteSyncStoreOptions _options;
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private G9CSyncClientModel? _model;
    private Dictionary<string, LocalTable> _byLocal = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, LocalTable> _byServerId = [];
    private int _foreignTriggersLogged;

    public G9CSqliteSyncStore(G9CSqliteSyncStoreOptions options, ILogger<G9CSqliteSyncStore>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionString = options.BuildConnectionString();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    // ================================================================ initialise

    public async Task InitializeAsync(G9CSyncClientModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            if (_options.UseWriteAheadLog) Exec(connection, null, "PRAGMA journal_mode = WAL");

            var tables = new List<LocalTable>();
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                Exec(connection, transaction, SqliteSql.Metadata);
                if (Convert.ToInt64(Scalar(connection, transaction, "SELECT count(*) FROM pragma_table_info('g9_ctx') WHERE name = 'command_id'"),
                        CultureInfo.InvariantCulture) == 0)
                {
                    // Stores created before commands existed (ADR-0025).
                    Exec(connection, transaction, "ALTER TABLE g9_ctx ADD COLUMN command_id TEXT NULL");
                }

                if (Convert.ToInt64(Scalar(connection, transaction, "SELECT count(*) FROM pragma_table_info('g9_stage_runs') WHERE name = 'generation'"),
                        CultureInfo.InvariantCulture) == 0)
                {
                    // Stores created before stages were validated on resume (ADR-0046). A stage already in flight cannot be described in
                    // the new terms after the fact - nothing recorded when it began - so it is dropped and replanned, not guessed at.
                    Exec(connection, transaction, "ALTER TABLE g9_stage_runs ADD COLUMN generation TEXT NULL");
                    Exec(connection, transaction, "ALTER TABLE g9_stage_runs ADD COLUMN stage_seq INTEGER NOT NULL DEFAULT 0");
                    Exec(connection, transaction, "DELETE FROM g9_stage; DELETE FROM g9_stage_runs;");
                }

                if (Convert.ToInt64(Scalar(connection, transaction, "SELECT count(*) FROM pragma_table_info('g9_stage_runs') WHERE name = 'from_cursors_json'"),
                        CultureInfo.InvariantCulture) == 0)
                {
                    // Stores created before stages were kept per generation (ADR-0060). A stage already in flight has no record of the
                    // cursors it was planned from; its NULL reads back as "move every cursor", which is what it would have done anyway.
                    Exec(connection, transaction, "ALTER TABLE g9_stage_runs ADD COLUMN from_cursors_json TEXT NULL");
                }

                if (Convert.ToInt64(Scalar(connection, transaction, "SELECT count(*) FROM pragma_table_info('g9_subscriptions') WHERE name = 'move_out_policy'"),
                        CultureInfo.InvariantCulture) == 0)
                {
                    // Stores created before the move-out policy was kept per subscription (ADR-0048); the next Hello fills it in.
                    Exec(connection, transaction, "ALTER TABLE g9_subscriptions ADD COLUMN move_out_policy INTEGER NULL");
                }

                if (Meta(connection, transaction, "client_id") is null) SetMeta(connection, transaction, "client_id", Guid.NewGuid().ToString("D"));
                if (Meta(connection, transaction, "next_batch_id") is null) SetMeta(connection, transaction, "next_batch_id", "1");
                VerifyIncarnation(connection, transaction);

                foreach (var table in model.Tables)
                {
                    tables.Add(ProvisionTable(connection, transaction, table));
                }

                transaction.Commit();
            }

            _model = model;
            _byLocal = tables.ToDictionary(t => t.Model.LocalName, StringComparer.OrdinalIgnoreCase);
            _byServerId = tables.Where(t => t.Negotiated is not null).ToDictionary(t => t.Negotiated!.TableId);
            LogInitialized(_logger, tables.Count);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private LocalTable ProvisionTable(SqliteConnection connection, SqliteTransaction transaction, G9CSyncClientTable table)
    {
        var hash = SqliteSql.ModelHash(table);
        long tid;
        string? existingHash = null;
        string? negotiatedJson = null;
        string? syncedJson = null;
        var isNew = false;

        using (var command = Command(connection, transaction, "SELECT tid, model_hash, negotiated_json, synced_columns FROM g9_tables WHERE local_name = $n"))
        {
            command.Parameters.AddWithValue("$n", table.LocalName);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                tid = reader.GetInt64(0);
                existingHash = reader.GetString(1);
                negotiatedJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                syncedJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            }
            else
            {
                tid = -1;
            }
        }

        if (tid < 0)
        {
            Exec(connection, transaction, "INSERT INTO g9_tables (local_name, server_name, model_hash) VALUES ($n, $s, '')",
                ("$n", table.LocalName), ("$s", table.ServerName));
            tid = (long)Scalar(connection, transaction, "SELECT last_insert_rowid()")!;
            isNew = true;
        }
        else
        {
            Exec(connection, transaction, "UPDATE g9_tables SET server_name = $s WHERE tid = $t", ("$s", table.ServerName), ("$t", tid));
        }

        var exists = Scalar(connection, transaction, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n COLLATE NOCASE", ("$n", table.LocalName)) is not null;
        if (!exists)
        {
            Exec(connection, transaction, SqliteSql.CreateTable(table, _options));
        }
        else
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = Command(connection, transaction, "SELECT name FROM pragma_table_info($n)"))
            {
                command.Parameters.AddWithValue("$n", table.LocalName);
                using var reader = command.ExecuteReader();
                while (reader.Read()) existing.Add(reader.GetString(0));
            }

            foreach (var column in table.Columns.Where(c => !existing.Contains(c.Name)))
            {
                Exec(connection, transaction, "ALTER TABLE " + SqliteSql.Quote(table.LocalName) + " ADD COLUMN " + SqliteSql.ColumnDefinition(column, _options));
                LogColumnAdded(_logger, table.LocalName, column.Name);
            }
        }

        foreach (var index in table.Indexes)
        {
            Exec(connection, transaction,
                $"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}INDEX IF NOT EXISTS {SqliteSql.Quote(index.Name)} ON {SqliteSql.Quote(table.LocalName)} ({string.Join(", ", index.Columns.Select(SqliteSql.Quote))})");
        }

        var negotiated = negotiatedJson is null ? null : JsonSerializer.Deserialize(negotiatedJson, G9SyncJsonContext.Default.G9DtNegotiatedTable);
        var captureWanted = negotiated?.Direction != G9ESyncDirection.DownloadOnly;
        var triggerCount = Convert.ToInt64(Scalar(connection, transaction,
            "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ($a, $b, $c)",
            ("$a", "g9_" + table.LocalName + "_ai"), ("$b", "g9_" + table.LocalName + "_au"), ("$c", "g9_" + table.LocalName + "_ad")), CultureInfo.InvariantCulture);
        var commandTriggerCount = Convert.ToInt64(Scalar(connection, transaction,
            "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name IN ($a, $b, $c)",
            ("$a", "g9_" + table.LocalName + "_ci"), ("$b", "g9_" + table.LocalName + "_cu"), ("$c", "g9_" + table.LocalName + "_cd")), CultureInfo.InvariantCulture);

        if (!string.Equals(existingHash, hash, StringComparison.Ordinal) || (captureWanted && triggerCount != 3) || commandTriggerCount != 3)
        {
            Exec(connection, transaction, SqliteSql.DropTriggers(table));
            if (captureWanted) Exec(connection, transaction, SqliteSql.CreateTriggers(table, tid));
            Exec(connection, transaction, SqliteSql.CreateCommandTriggers(table, tid));
            if (existingHash is { Length: > 0 } && !string.Equals(existingHash, hash, StringComparison.Ordinal))
            {
                // Column order may have changed: pending rows upload every column rather than trusting old mask bits.
                Exec(connection, transaction, "UPDATE g9_rows SET dirty_mask = $m WHERE tid = $t AND dirty = 1",
                    ("$m", SqliteSql.AllColumnsMask(table.Columns.Count)), ("$t", tid));
            }

            Exec(connection, transaction, "UPDATE g9_tables SET model_hash = $h WHERE tid = $t", ("$h", hash), ("$t", tid));
        }

        if (isNew && exists)
        {
            var adopted = Exec(connection, transaction,
                $"INSERT OR IGNORE INTO g9_rows (tid, pk) SELECT $t, {SqliteSql.KeyExpression(table, "b")} FROM {SqliteSql.Quote(table.LocalName)} AS b",
                ("$t", tid));
            if (adopted > 0) LogAdopted(_logger, table.LocalName, adopted);
        }

        return new LocalTable
        {
            Model = table,
            Tid = tid,
            Negotiated = negotiated,
            SyncedColumns = syncedJson is null ? [] : JsonSerializer.Deserialize(syncedJson, G9SyncClientJsonContext.Default.ListString) ?? []
        };
    }

    public async Task<Guid> GetClientIdAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return Guid.Parse(Meta(connection, null, "client_id") ?? throw new InvalidOperationException("The store is not initialised."));
    }

    // ================================================================ server state

    public async Task<G9CClientServerState> GetServerStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var epoch = Meta(connection, null, "epoch");
        var lastHello = Meta(connection, null, "last_hello_utc");
        var offset = Meta(connection, null, "server_offset_ms");
        var definitions = Meta(connection, null, "definitions_json");
        var tables = Meta(connection, null, "tables_json");
        return new G9CClientServerState
        {
            Epoch = epoch is null ? null : Guid.Parse(epoch),
            LastHelloUtc = lastHello is null ? null : FromUnixMs(long.Parse(lastHello, CultureInfo.InvariantCulture)),
            ServerClockOffset = offset is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(long.Parse(offset, CultureInfo.InvariantCulture)),
            Definitions = definitions is null ? [] : JsonSerializer.Deserialize(definitions, G9SyncJsonContext.Default.ListG9DtSubscriptionDefinition) ?? [],
            Tables = tables is null ? [] : JsonSerializer.Deserialize(tables, G9SyncJsonContext.Default.ListG9DtNegotiatedTable) ?? []
        };
    }

    public async Task SaveHelloAsync(G9DtHelloResult hello, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hello);
        var model = EnsureInitialized();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            SetMeta(connection, transaction, "epoch", hello.Epoch.ToString("D"));
            SetMeta(connection, transaction, "last_hello_utc", NowMs().ToString(CultureInfo.InvariantCulture));
            SetMeta(connection, transaction, "server_offset_ms", ((long)(hello.ServerTimeUtc - DateTime.UtcNow).TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
            SetMeta(connection, transaction, "definitions_json", JsonSerializer.Serialize(hello.Definitions, G9SyncJsonContext.Default.ListG9DtSubscriptionDefinition));
            SetMeta(connection, transaction, "tables_json", JsonSerializer.Serialize(hello.Tables, G9SyncJsonContext.Default.ListG9DtNegotiatedTable));

            // Kept per subscription, not only in the definition list: a caller who loses access stops being told the definition, and the
            // rows still have to be disposed of the way the server said to dispose of them (ADR-0048).
            foreach (var definition in hello.Definitions)
            {
                Exec(connection, transaction, "UPDATE g9_subscriptions SET move_out_policy = $p WHERE name = $n",
                    ("$p", (long)definition.MoveOutPolicy), ("$n", definition.Name));
            }

            var updates = new List<(LocalTable Table, G9DtNegotiatedTable Negotiated, List<string> Synced)>();
            foreach (var negotiated in hello.Tables)
            {
                var clientTable = model.FindByServerName(negotiated.Name);
                if (clientTable is null || !_byLocal.TryGetValue(clientTable.LocalName, out var local)) continue;

                var names = negotiated.Columns.Select(c => c.Name).ToList();
                List<string> synced;
                if (local.Negotiated is null)
                {
                    synced = names;
                }
                else
                {
                    synced = local.SyncedColumns.ToList();
                    var added = names.Where(n => !synced.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (added.Count > 0 && Scalar(connection, transaction, "SELECT 1 FROM g9_rows WHERE tid = $t AND deleted = 0 LIMIT 1", ("$t", local.Tid)) is null)
                    {
                        synced.AddRange(added);
                    }
                }

                Exec(connection, transaction, "UPDATE g9_tables SET server_table_id = $s, negotiated_json = $j, synced_columns = $c WHERE tid = $t",
                    ("$s", negotiated.TableId), ("$j", JsonSerializer.Serialize(negotiated, G9SyncJsonContext.Default.G9DtNegotiatedTable)),
                    ("$c", JsonSerializer.Serialize(synced, G9SyncClientJsonContext.Default.ListString)), ("$t", local.Tid));

                if (negotiated.Direction == G9ESyncDirection.DownloadOnly)
                {
                    Exec(connection, transaction, SqliteSql.DropTriggers(local.Model));
                }
                else if (local.Negotiated?.Direction == G9ESyncDirection.DownloadOnly)
                {
                    Exec(connection, transaction, SqliteSql.DropTriggers(local.Model) + SqliteSql.CreateTriggers(local.Model, local.Tid));
                }

                updates.Add((local, negotiated, synced));
            }

            transaction.Commit();
            foreach (var (local, negotiated, synced) in updates)
            {
                local.Negotiated = negotiated;
                local.SyncedColumns = synced;
            }

            _byServerId = _byLocal.Values.Where(t => t.Negotiated is not null).ToDictionary(t => t.Negotiated!.TableId);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task<IReadOnlyList<G9DtBackfillRequest>> GetPendingBackfillsAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var backfills = new List<G9DtBackfillRequest>();
        foreach (var table in _byLocal.Values)
        {
            if (table.Negotiated is null) continue;
            var missing = table.Negotiated.Columns.Select(c => c.Name)
                .Where(n => !table.SyncedColumns.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0) backfills.Add(new G9DtBackfillRequest { TableId = table.Negotiated.TableId, Columns = missing });
        }

        return Task.FromResult<IReadOnlyList<G9DtBackfillRequest>>(backfills);
    }

    // ================================================================ subscriptions

    public async Task<IReadOnlyList<G9CSyncSubscriptionRecord>> GetSubscriptionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return ReadSubscriptions(connection, null, key: null);
    }

    public async Task<G9CSyncSubscriptionRecord> EnsureSubscriptionAsync(string name, G9CSyncParameters parameters, bool ephemeral, TimeSpan? ttl,
        bool adoptExistingRows, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parameters);
        var key = G9CSyncParameters.BuildSubscriptionKey(name, parameters);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var existing = ReadSubscriptions(connection, transaction, key);
            if (existing.Count > 0) return existing[0];

            var slot = SqliteSql.EphemeralSlot;
            if (!ephemeral)
            {
                var used = new HashSet<long>();
                using (var command = Command(connection, transaction, "SELECT slot FROM g9_subscriptions WHERE ephemeral = 0"))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read()) used.Add(reader.GetInt64(0));
                }

                slot = Enumerable.Range(0, SqliteSql.EphemeralSlot).FirstOrDefault(s => !used.Contains(s), -1);
                if (slot < 0)
                {
                    throw new G9CSyncException(G9SyncErrorCodes.LocalStoreError,
                        "At most 63 persistent subscriptions per database; declare per-item subscriptions Ephemeral(ttl) on the server.");
                }
            }

            Exec(connection, transaction,
                "INSERT INTO g9_subscriptions (key, name, params, slot, ephemeral, ttl_seconds, state, created_utc, last_access_utc) VALUES ($k, $n, $p, $s, $e, $t, $st, $c, $c)",
                ("$k", key), ("$n", name), ("$p", JsonSerializer.Serialize(new Dictionary<string, string>(parameters.Values, StringComparer.Ordinal), G9SyncClientJsonContext.Default.DictionaryStringString)),
                ("$s", slot), ("$e", ephemeral ? 1 : 0), ("$t", ttl is null ? null : (long)ttl.Value.TotalSeconds),
                ("$st", (int)(adoptExistingRows ? G9ESubscriptionState.NeedsReconcile : G9ESubscriptionState.Active)), ("$c", NowMs()));
            var created = ReadSubscriptions(connection, transaction, key)[0];
            transaction.Commit();
            return created;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task RemoveSubscriptionAsync(string key, G9EMoveOutPolicy policy, CancellationToken cancellationToken) =>
        RemoveSubscriptionAsync(key, policy, G9ERevocationReason.Unsubscribed, keepSubscription: false, cancellationToken);

    public async Task<G9CApplyReport> ReleaseSubscriptionRowsAsync(string key, CancellationToken cancellationToken)
    {
        // The policy comes from the last definition the server announced for this subscription, kept on the record: a caller who has just
        // lost access is not told the definition any more, and falling back to Delete there would quietly discard retained data.
        var report = new G9CApplyReport();
        await RemoveSubscriptionAsync(key, policy: null, G9ERevocationReason.AccessRevoked, keepSubscription: true, cancellationToken, report)
            .ConfigureAwait(false);
        return report;
    }

    private async Task RemoveSubscriptionAsync(string key, G9EMoveOutPolicy? policy, G9ERevocationReason reason, bool keepSubscription,
        CancellationToken cancellationToken, G9CApplyReport? report = null)
    {
        var model = EnsureInitialized();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var records = ReadSubscriptions(connection, transaction, key);
            if (records.Count == 0) return;
            var record = records[0];
            var effective = policy ?? record.MoveOutPolicy ?? G9EMoveOutPolicy.Delete;

            Exec(connection, transaction, "UPDATE g9_ctx SET applying = 1 WHERE id = 1");
            Exec(connection, transaction, "DROP TABLE IF EXISTS temp.g9_affected; CREATE TEMP TABLE g9_affected (tid INTEGER NOT NULL, pk TEXT NOT NULL, PRIMARY KEY (tid, pk)) WITHOUT ROWID");
            if (record.Ephemeral)
            {
                Exec(connection, transaction, "INSERT INTO temp.g9_affected (tid, pk) SELECT tid, pk FROM g9_ephemeral_rows WHERE sub_key = $k", ("$k", key));
                Exec(connection, transaction, "DELETE FROM g9_ephemeral_rows WHERE sub_key = $k", ("$k", key));
                Exec(connection, transaction,
                    "UPDATE g9_rows SET subs = subs & $n WHERE (tid, pk) IN (SELECT tid, pk FROM temp.g9_affected) " +
                    "AND NOT EXISTS (SELECT 1 FROM g9_ephemeral_rows e WHERE e.tid = g9_rows.tid AND e.pk = g9_rows.pk)",
                    ("$n", ~SqliteSql.Bit(SqliteSql.EphemeralSlot)));
            }
            else
            {
                var bit = SqliteSql.Bit(record.Slot);
                Exec(connection, transaction, "INSERT INTO temp.g9_affected (tid, pk) SELECT tid, pk FROM g9_rows WHERE (subs & $b) <> 0", ("$b", bit));
                Exec(connection, transaction, "UPDATE g9_rows SET subs = subs & $n WHERE (subs & $b) <> 0", ("$n", ~bit), ("$b", bit));
            }

            if (effective == G9EMoveOutPolicy.Delete)
            {
                // Rows no other subscription holds go through the revocation policy (ADR-0026), pending changes included.
                var released = new List<(long Tid, string Key)>();
                using (var command = Command(connection, transaction,
                           "SELECT r.tid, r.pk FROM g9_rows r JOIN temp.g9_affected a ON a.tid = r.tid AND a.pk = r.pk WHERE r.subs = 0 AND r.deleted = 0"))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read()) released.Add((reader.GetInt64(0), reader.GetString(1)));
                }

                var sequence = Convert.ToInt64(Scalar(connection, transaction, "SELECT seq FROM g9_clock WHERE id = 1"), CultureInfo.InvariantCulture);
                var removed = 0;
                foreach (var (tid, rowKey) in released)
                {
                    var table = _byLocal.Values.FirstOrDefault(t => t.Tid == tid);
                    if (table is not null && DisposeReleasedRow(connection, transaction, table, rowKey, reason, key, ++sequence, report)) removed++;
                }

                Exec(connection, transaction, "UPDATE g9_clock SET seq = $s WHERE id = 1", ("$s", sequence));
                LogSubscriptionRemoved(_logger, key, removed);
            }

            Exec(connection, transaction,
                keepSubscription
                    // Access can come back; the subscription stays so a regrant refills it, and without a cursor it refills from scratch.
                    ? "UPDATE g9_subscriptions SET cursor = NULL, last_success_utc = NULL, last_reconcile_utc = NULL WHERE key = $k"
                    : "DELETE FROM g9_subscriptions WHERE key = $k", ("$k", key));
            Exec(connection, transaction, "DROP TABLE temp.g9_affected; UPDATE g9_ctx SET applying = 0 WHERE id = 1");
            transaction.Commit();
            _ = model;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateSubscriptionAsync(string key, G9CSubscriptionUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var sets = new List<string>();
        var parameters = new List<(string, object?)> { ("$k", key) };
        if (update.Cursor is not null)
        {
            sets.Add("cursor = $cursor");
            parameters.Add(("$cursor", update.Cursor));
        }

        if (update.ClearCursor) sets.Add("cursor = NULL");
        if (update.DefinitionHash is not null)
        {
            sets.Add("definition_hash = $def");
            parameters.Add(("$def", update.DefinitionHash));
        }

        if (update.AuthHash is not null)
        {
            sets.Add("auth_hash = $auth");
            parameters.Add(("$auth", update.AuthHash));
        }

        if (update.State is not null)
        {
            sets.Add("state = $state");
            parameters.Add(("$state", (int)update.State.Value));
        }

        if (update.ErrorCode is not null)
        {
            sets.Add("error_code = $error");
            parameters.Add(("$error", update.ErrorCode));
        }

        if (update.ClearError) sets.Add("error_code = NULL");
        var now = NowMs();
        if (update.Succeeded) sets.Add("last_success_utc = " + now.ToString(CultureInfo.InvariantCulture));
        if (update.Reconciled) sets.Add("last_reconcile_utc = " + now.ToString(CultureInfo.InvariantCulture));
        if (update.Accessed) sets.Add("last_access_utc = " + now.ToString(CultureInfo.InvariantCulture));
        if (sets.Count == 0) return;

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            Exec(connection, null, "UPDATE g9_subscriptions SET " + string.Join(", ", sets) + " WHERE key = $k", parameters.ToArray());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> CollectExpiredEphemeralAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var expired = new List<string>();
        await using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
        using (var command = Command(connection, null,
                   "SELECT key FROM g9_subscriptions WHERE ephemeral = 1 AND ttl_seconds IS NOT NULL AND coalesce(last_access_utc, created_utc) + ttl_seconds * 1000 < $now"))
        {
            command.Parameters.AddWithValue("$now", ToUnixMs(utcNow));
            using var reader = command.ExecuteReader();
            while (reader.Read()) expired.Add(reader.GetString(0));
        }

        foreach (var key in expired)
        {
            await RemoveSubscriptionAsync(key, G9EMoveOutPolicy.Delete, G9ERevocationReason.Expired, keepSubscription: false, cancellationToken).ConfigureAwait(false);
        }
        return expired.Count;
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();

        // Release THIS database's pooled connections (and their file handles), so the file can be moved or deleted. Never
        // SqliteConnection.ClearAllPools(): it is process-wide and disposes connections other stores are using (LES-0030).
        using (var connection = new SqliteConnection(_connectionString)) SqliteConnection.ClearPool(connection);
        return ValueTask.CompletedTask;
    }

    // ================================================================ helpers

    private G9CSyncClientModel EnsureInitialized() =>
        _model ?? throw new InvalidOperationException("The SQLite sync store is not initialised; call InitializeAsync first.");

    private LocalTable GetLocal(string localName) =>
        _byLocal.TryGetValue(localName, out var table) ? table : throw new ArgumentException($"'{localName}' is not a synced table.", nameof(localName));

    private LocalTable? FindByServerName(string serverName)
    {
        var table = EnsureInitialized().FindByServerName(serverName);
        return table is not null && _byLocal.TryGetValue(table.LocalName, out var local) ? local : null;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            Exec(connection, null, "PRAGMA busy_timeout = " + _options.BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture) + "; PRAGMA foreign_keys = OFF; " +
                                   "PRAGMA journal_size_limit = " + _options.WalSizeLimitBytes.ToString(CultureInfo.InvariantCulture) + ";");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static int Exec(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static string? Meta(SqliteConnection connection, SqliteTransaction? transaction, string key) =>
        Scalar(connection, transaction, "SELECT value FROM g9_meta WHERE key = $k", ("$k", key)) as string;

    private static void SetMeta(SqliteConnection connection, SqliteTransaction? transaction, string key, string? value) =>
        Exec(connection, transaction, "INSERT INTO g9_meta (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$k", key), ("$v", value));

    private static List<G9CSyncSubscriptionRecord> ReadSubscriptions(SqliteConnection connection, SqliteTransaction? transaction, string? key)
    {
        var records = new List<G9CSyncSubscriptionRecord>();
        using var command = Command(connection, transaction,
            "SELECT key, name, params, slot, ephemeral, ttl_seconds, cursor, definition_hash, auth_hash, state, error_code, created_utc, " +
            "last_success_utc, last_reconcile_utc, last_access_utc, move_out_policy FROM g9_subscriptions" + (key is null ? string.Empty : " WHERE key = $k") + " ORDER BY created_utc");
        if (key is not null) command.Parameters.AddWithValue("$k", key);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var parameters = JsonSerializer.Deserialize(reader.GetString(2), G9SyncClientJsonContext.Default.DictionaryStringString);
            records.Add(new G9CSyncSubscriptionRecord
            {
                Key = reader.GetString(0),
                Name = reader.GetString(1),
                Parameters = G9CSyncParameters.FromValues(parameters),
                Slot = reader.GetInt32(3),
                Ephemeral = reader.GetInt64(4) != 0,
                Ttl = reader.IsDBNull(5) ? null : TimeSpan.FromSeconds(reader.GetInt64(5)),
                Cursor = reader.IsDBNull(6) ? null : reader.GetString(6),
                DefinitionHash = reader.IsDBNull(7) ? null : reader.GetString(7),
                AuthHash = reader.IsDBNull(8) ? null : reader.GetString(8),
                State = (G9ESubscriptionState)reader.GetInt32(9),
                LastErrorCode = reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedUtc = FromUnixMs(reader.GetInt64(11)),
                LastSuccessUtc = reader.IsDBNull(12) ? null : FromUnixMs(reader.GetInt64(12)),
                LastReconcileUtc = reader.IsDBNull(13) ? null : FromUnixMs(reader.GetInt64(13)),
                LastAccessUtc = reader.IsDBNull(14) ? null : FromUnixMs(reader.GetInt64(14)),
                MoveOutPolicy = reader.IsDBNull(15) ? null : (G9EMoveOutPolicy)reader.GetInt32(15)
            });
        }

        return records;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static long ToUnixMs(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private static object KeyValue(LocalTable table, string canonicalKey) =>
        table.Model.KeyType is G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64
            ? long.Parse(canonicalKey, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
            : canonicalKey;

    /// <summary>Converts a frame value to its local storage representation.</summary>
    private object ToDbValue(G9CRowBatchReader reader, int column, G9ESyncColumnType type)
    {
        if (reader.IsNull(column)) return DBNull.Value;
        return type switch
        {
            G9ESyncColumnType.Boolean => reader.GetBoolean(column) ? 1L : 0L,
            G9ESyncColumnType.Int32 => (long)reader.GetInt32(column),
            G9ESyncColumnType.Int64 => reader.GetInt64(column),
            G9ESyncColumnType.Double => reader.GetDouble(column),
            G9ESyncColumnType.Decimal => _options.DecimalStorage == G9ESqliteDecimalStorage.Real
                ? (double)reader.GetDecimal(column)
                : reader.GetDecimal(column).ToString(CultureInfo.InvariantCulture),
            G9ESyncColumnType.String => reader.GetString(column),
            G9ESyncColumnType.Guid => G9CSyncKey.FromGuid(reader.GetGuid(column)),
            G9ESyncColumnType.DateTime => _options.DateTimeStorage == G9ESqliteDateTimeStorage.Ticks
                ? reader.GetDateTime(column).Ticks
                : reader.GetDateTime(column).ToString(_options.DateTimeFormat, CultureInfo.InvariantCulture),
            G9ESyncColumnType.DateTimeOffset => reader.GetDateTimeOffset(column).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
            G9ESyncColumnType.TimeSpan => reader.GetTimeSpan(column).Ticks,
            G9ESyncColumnType.Bytes => reader.GetBytes(column).ToArray(),
            _ => throw new InvalidOperationException($"Unsupported column type {type}.")
        };
    }

    /// <summary>Writes a local column value into a push row.</summary>
    private void WriteLocalValue(G9CRowBatchWriter writer, SqliteDataReader reader, int ordinal, G9ESyncColumnType type)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNull();
            return;
        }

        switch (type)
        {
            case G9ESyncColumnType.Boolean:
                writer.WriteBoolean(reader.GetInt64(ordinal) != 0);
                break;
            case G9ESyncColumnType.Int32:
                writer.WriteInt32(checked((int)reader.GetInt64(ordinal)));
                break;
            case G9ESyncColumnType.Int64:
                writer.WriteInt64(reader.GetInt64(ordinal));
                break;
            case G9ESyncColumnType.Double:
                writer.WriteDouble(reader.GetDouble(ordinal));
                break;
            case G9ESyncColumnType.Decimal:
                writer.WriteDecimal(reader.GetDecimal(ordinal));
                break;
            case G9ESyncColumnType.String:
                writer.WriteString(reader.GetString(ordinal));
                break;
            case G9ESyncColumnType.Guid:
                writer.WriteGuid(reader.GetGuid(ordinal));
                break;
            case G9ESyncColumnType.DateTime:
                writer.WriteDateTime(_options.DateTimeStorage == G9ESqliteDateTimeStorage.Ticks || reader.GetFieldType(ordinal) == typeof(long)
                    ? new DateTime(reader.GetInt64(ordinal), DateTimeKind.Unspecified)
                    : DateTime.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                break;
            case G9ESyncColumnType.DateTimeOffset:
                writer.WriteDateTimeOffset(DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture));
                break;
            case G9ESyncColumnType.TimeSpan:
                writer.WriteTimeSpan(TimeSpan.FromTicks(reader.GetInt64(ordinal)));
                break;
            case G9ESyncColumnType.Bytes:
                writer.WriteBytes((byte[])reader.GetValue(ordinal));
                break;
            default:
                throw new InvalidOperationException($"Unsupported column type {type}.");
        }
    }

    internal sealed class LocalTable
    {
        public required G9CSyncClientTable Model { get; init; }
        public required long Tid { get; init; }
        public G9DtNegotiatedTable? Negotiated { get; set; }
        public List<string> SyncedColumns { get; set; } = [];
        public string Quoted => SqliteSql.Quote(Model.LocalName);
        public string KeyQuoted => SqliteSql.Quote(Model.KeyColumn);
    }

    [LoggerMessage(EventId = 20101, Level = LogLevel.Information, Message = "G9SyncData SQLite store initialised with {Tables} tables")]
    private static partial void LogInitialized(ILogger logger, int tables);

    [LoggerMessage(EventId = 20102, Level = LogLevel.Information, Message = "Added column {Table}.{Column} from the client model")]
    private static partial void LogColumnAdded(ILogger logger, string table, string column);

    [LoggerMessage(EventId = 20103, Level = LogLevel.Information, Message = "Adopted {Count} existing rows of {Table} into change capture")]
    private static partial void LogAdopted(ILogger logger, string table, int count);

    [LoggerMessage(EventId = 20104, Level = LogLevel.Information, Message = "Removed subscription {Key}; deleted {Rows} rows no other subscription held")]
    private static partial void LogSubscriptionRemoved(ILogger logger, string key, int rows);
}
