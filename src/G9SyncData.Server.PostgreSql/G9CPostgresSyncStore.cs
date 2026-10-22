using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql.Internal;
using Microsoft.Extensions.DependencyInjection;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     PostgreSQL implementation of <see cref="IG9SyncServerStore" />. See AiGuides/06-Server-PostgreSql.md for every
///     object it installs and every statement it runs.
/// </summary>
public sealed partial class G9CPostgresSyncStore : IG9SyncServerStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly G9CSyncServerModel _model;
    private readonly G9CPostgresSyncOptions _options;
    private readonly ILogger<G9CPostgresSyncStore> _logger;
    private readonly string _schema;

    /// <summary>Handed to scope providers (e.g. to resolve an EF Core DbContext). Empty when the host supplies none.</summary>
    private readonly IServiceProvider _services;

    private Dictionary<G9CSyncServerTable, PgTable> _tables = [];
    private Dictionary<int, PgTable> _byId = [];
    private Dictionary<string, PgTable> _byLogical = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _epochLock = new();
    private Guid _epoch;
    private volatile bool _initialized;

    public G9CPostgresSyncStore(NpgsqlDataSource dataSource, G9CSyncServerModel model, G9CPostgresSyncOptions options, ILogger<G9CPostgresSyncStore> logger,
        IServiceProvider? services = null)
    {
        _services = services ?? EmptyServiceProvider.Instance;
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Schema);
        _dataSource = dataSource;
        _model = model;
        _options = options;
        _logger = logger;
        _schema = PgNames.Quote(options.Schema);
    }

    public Guid Epoch
    {
        get
        {
            EnsureInitialized();
            return CurrentEpoch;
        }
    }

    /// <summary>
    ///     The capture epoch. Written at startup, re-read with every snapshot, and rotated by the WAL consumer after a lost
    ///     slot - so every server instance notices a rotation on its next pull, not only the one running the consumer.
    /// </summary>
    private Guid CurrentEpoch
    {
        get
        {
            lock (_epochLock) return _epoch;
        }
        set
        {
            lock (_epochLock) _epoch = value;
        }
    }

    public int GetTableId(G9CSyncServerTable table) => Get(table).TableId;

    public G9CSyncServerTable? FindTable(int tableId) => _byId.TryGetValue(tableId, out var table) ? table.Model : null;

    // ================================================================ initialise

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await IntrospectAllAsync(connection, cancellationToken).ConfigureAwait(false);

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(connection, transaction, "SELECT pg_advisory_xact_lock(hashtext(@g9_lock))", cancellationToken,
                P("g9_lock", NpgsqlDbType.Text, "g9sync:install:" + _options.Schema)).ConfigureAwait(false);

            if (_options.AutoInstall)
            {
                await ExecuteAsync(connection, transaction, PgCaptureSql.Base(_schema), cancellationToken).ConfigureAwait(false);
            }
            else if (!await ScalarAsync<bool>(connection, transaction, "SELECT to_regclass(@g9_name) IS NOT NULL", cancellationToken,
                         P("g9_name", NpgsqlDbType.Text, _schema + ".tables")).ConfigureAwait(false))
            {
                throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                    $"G9SyncData capture is not installed in schema {_schema}. Apply the script from GenerateInstallScriptAsync, or enable AutoInstall.");
            }

            CurrentEpoch = await VerifyDatabaseIdentityAsync(connection, transaction, tables, cancellationToken).ConfigureAwait(false);

            foreach (var table in tables)
            {
                await RegisterTableAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _tables = tables.ToDictionary(t => t.Model);
        _byId = tables.ToDictionary(t => t.TableId);
        _byLogical = tables.ToDictionary(t => t.Model.LogicalName, StringComparer.OrdinalIgnoreCase);
        _initialized = true;

        try
        {
            if (IsWalMode) await InitializeWalAsync(connection, cancellationToken).ConfigureAwait(false);
            else if (_options.AutoInstall) await DisableWalArtifactsAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _initialized = false;
            throw;
        }

        var errors = await ValidateSqlAsync(connection, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            _initialized = false;
            throw ModelMismatch("SQL expressions", errors);
        }

        CheckConnectionBudget();
    }

    /// <summary>
    ///     Warns when the engine's admission limits let more requests hold a connection at once than the data source's pool has
    ///     (LES-0074). Over the pool, a burst does not get the quick, retryable "busy" that admission exists to give: requests queue
    ///     inside Npgsql and fail after its timeout - pushes included. A few connections are kept for the host's own work (change
    ///     listener, WAL consumer, maintenance, effects).
    /// </summary>
    private void CheckConnectionBudget()
    {
        if (_services.GetService(typeof(Microsoft.Extensions.Options.IOptions<G9CSyncServerOptions>)) is not Microsoft.Extensions.Options.IOptions<G9CSyncServerOptions> server)
        {
            return;
        }

        var limits = server.Value;
        var pool = new NpgsqlConnectionStringBuilder(_dataSource.ConnectionString).MaxPoolSize;
        const int Reserved = 4;
        var unlimited = limits.MaxConcurrentPulls <= 0 || limits.MaxConcurrentPushes <= 0 || limits.MaxConcurrentPlans <= 0;
        var demand = limits.MaxConcurrentPulls + limits.MaxConcurrentPushes + limits.MaxConcurrentPlans + Reserved;
        if (unlimited || demand > pool)
        {
            LogConnectionBudget(_logger, limits.MaxConcurrentPulls, limits.MaxConcurrentPushes, limits.MaxConcurrentPlans, Reserved, pool);
        }
    }

    [LoggerMessage(EventId = 22030, Level = LogLevel.Warning,
        Message = "G9SyncData admission allows {Pulls} pulls + {Pushes} pushes + {Plans} plans (+{Reserved} for the host) to hold a database connection at " +
                  "once, but the pool holds {Pool} (0 = unlimited). A burst will then wait inside Npgsql and fail after its timeout instead of being " +
                  "refused as busy: lower MaxConcurrentPulls/Pushes/Plans or raise Maximum Pool Size.")]
    private static partial void LogConnectionBudget(ILogger logger, int pulls, int pushes, int plans, int reserved, int pool);

    private async Task RegisterTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table, CancellationToken cancellationToken)
    {
        var capture = PgCaptureSql.Capture(table, _schema, _options);
        var captureHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capture)));
        var columns = table.Model.PhysicalColumns.Select(c => c.Name).ToArray();

        await ExecuteAsync(connection, transaction,
            $"INSERT INTO {_schema}.tables (schema_name, table_name) VALUES (@g9_schema, @g9_table) ON CONFLICT (schema_name, table_name) DO NOTHING",
            cancellationToken, P("g9_schema", NpgsqlDbType.Text, table.Model.Schema), P("g9_table", NpgsqlDbType.Text, table.Model.Name)).ConfigureAwait(false);

        string[] existingColumns;
        string? existingHash;
        await using (var command = Command(connection, transaction,
                         $"SELECT table_id, columns, capture_hash FROM {_schema}.tables WHERE schema_name = @g9_schema AND table_name = @g9_table"))
        {
            command.Parameters.Add(P("g9_schema", NpgsqlDbType.Text, table.Model.Schema));
            command.Parameters.Add(P("g9_table", NpgsqlDbType.Text, table.Model.Name));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            table.TableId = reader.GetInt32(0);
            existingColumns = reader.GetFieldValue<string[]>(1);
            existingHash = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (string.Equals(existingHash, captureHash, StringComparison.Ordinal)) return;
        if (!_options.AutoInstall)
        {
            throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                $"Capture objects for {table.Model.LogicalName} are missing or out of date. Apply the script from GenerateInstallScriptAsync.");
        }

        await ExecuteAsync(connection, transaction, capture, cancellationToken).ConfigureAwait(false);
        if (table.Model.TracksColumnVersions && !existingColumns.SequenceEqual(columns, StringComparer.Ordinal))
        {
            await ExecuteAsync(connection, transaction, $"UPDATE {table.TrackingName} SET col_vers = NULL WHERE col_vers IS NOT NULL", cancellationToken)
                .ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction,
            $"UPDATE {_schema}.tables SET columns = @g9_columns, capture_hash = @g9_hash, updated_at = now() WHERE table_id = @g9_id", cancellationToken,
            P("g9_columns", NpgsqlDbType.Array | NpgsqlDbType.Text, columns), P("g9_hash", NpgsqlDbType.Text, captureHash),
            P("g9_id", NpgsqlDbType.Integer, table.TableId)).ConfigureAwait(false);
        LogCaptureInstalled(_logger, table.Model.LogicalName, table.TableId, table.Ordered);
    }

    private async Task<List<PgTable>> IntrospectAllAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var tables = new List<PgTable>();
        foreach (var table in _model.Tables)
        {
            var pg = await IntrospectAsync(connection, table, errors, cancellationToken).ConfigureAwait(false);
            if (pg is not null) tables.Add(pg);
        }

        if (errors.Count > 0) throw ModelMismatch("schema", errors);
        return tables;
    }

    private async Task<PgTable?> IntrospectAsync(NpgsqlConnection connection, G9CSyncServerTable table, List<string> errors, CancellationToken cancellationToken)
    {
        var baseName = PgNames.Quote(table.Schema) + "." + PgNames.Quote(table.Name);
        var columns = new Dictionary<string, PgColumn>(StringComparer.OrdinalIgnoreCase);
        await using (var command = Command(connection, null, """
                         SELECT a.attname, format_type(a.atttypid, a.atttypmod),
                                CASE WHEN t.typtype = 'd' THEN bt.typname ELSE t.typname END,
                                a.attnotnull, (a.atthasdef OR a.attidentity <> '' OR a.attgenerated <> '')
                         FROM pg_attribute a
                         JOIN pg_type t ON t.oid = a.atttypid
                         LEFT JOIN pg_type bt ON bt.oid = t.typbasetype
                         WHERE a.attrelid = to_regclass(@g9_name) AND a.attnum > 0 AND NOT a.attisdropped
                         ORDER BY a.attnum
                         """))
        {
            command.Parameters.Add(P("g9_name", NpgsqlDbType.Text, baseName));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                columns[name] = new PgColumn
                {
                    DbName = name,
                    Quoted = PgNames.Quote(name),
                    DbType = reader.GetString(1),
                    TypeName = reader.IsDBNull(2) ? "unknown" : reader.GetString(2),
                    NotNull = reader.GetBoolean(3),
                    HasDefault = reader.GetBoolean(4)
                };
            }
        }

        if (columns.Count == 0)
        {
            errors.Add($"{table.LogicalName}: table does not exist.");
            return null;
        }

        var before = errors.Count;
        if (!columns.TryGetValue(table.KeyColumn, out var key))
        {
            errors.Add($"{table.LogicalName}: key column '{table.KeyColumn}' does not exist.");
            return null;
        }

        if (!PgTypes.IsKeyCompatible(table.KeyType, key.TypeName))
        {
            errors.Add($"{table.LogicalName}: key '{key.DbName}' is {key.TypeName}, which cannot be a {table.KeyType} key.");
        }

        foreach (var name in columns.Keys.Where(n => n.StartsWith("g9_", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add($"{table.LogicalName}: column '{name}' uses the reserved prefix g9_.");
        }

        var bound = new Dictionary<string, PgBoundColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.Columns)
        {
            if (column.IsComputed)
            {
                if (!PgTypes.SupportsCanonicalText(column.Type))
                {
                    errors.Add($"{table.LogicalName}: computed column '{column.Name}' must be Boolean, Int32, Int64, String or Guid (it is part of the reconciliation hash).");
                }

                var computedStaging = PgTypes.Staging(column.Type, null);
                bound[column.Name] = new PgBoundColumn
                {
                    Model = column, StagingType = computedStaging.Sql, StagingTypeName = computedStaging.TypeName, StagingDbType = computedStaging.DbType
                };
                continue;
            }

            if (!columns.TryGetValue(column.Name, out var db))
            {
                errors.Add($"{table.LogicalName}: column '{column.Name}' does not exist.");
                continue;
            }

            var compatible = PgTypes.IsCompatible(column.Type, db.TypeName);
            if (!compatible && column.ReadSql is null)
            {
                errors.Add($"{table.LogicalName}.{db.DbName}: {db.TypeName} cannot be read as {column.Type}; declare ReadAs(\"{{row}}.\\\"{db.DbName}\\\"::text\") or similar.");
            }

            if (!compatible && column.IsWritable && table.AcceptsUploads && column.WriteSql is null)
            {
                errors.Add($"{table.LogicalName}.{db.DbName}: {db.TypeName} cannot be written from {column.Type}; declare WriteAs(\"{{value}}::{db.DbType}\") or mark the column ReadOnly().");
            }

            var staging = PgTypes.Staging(column.Type, db.TypeName);
            bound[column.Name] = new PgBoundColumn
            {
                Model = column, Db = db, StagingType = staging.Sql, StagingTypeName = staging.TypeName, StagingDbType = staging.DbType
            };
        }

        // Columns an uploaded INSERT cannot fill. Reported at validation, once the row writers are known: a table whose uploads an
        // application writer performs gets its values from that writer (EF defaults, interceptors), not from the device.
        var requiredUnwritable = new List<string>();
        if (table.AcceptsUploads)
        {
            foreach (var db in columns.Values)
            {
                if (!db.NotNull || db.HasDefault || string.Equals(db.DbName, key.DbName, StringComparison.Ordinal)) continue;
                var modelColumn = table.FindColumn(db.DbName);
                if (modelColumn is null || modelColumn.IsComputed || !modelColumn.IsWritable) requiredUnwritable.Add(db.DbName);
            }
        }

        if (errors.Count > before) return null;

        var uniqueIndexes = await ScalarAsync<int>(connection, null,
            "SELECT count(*)::int FROM pg_index WHERE indrelid = to_regclass(@g9_name) AND indisunique AND NOT indisprimary",
            cancellationToken, P("g9_name", NpgsqlDbType.Text, baseName)).ConfigureAwait(false);
        var averageRowBytes = await ScalarAsync<double>(connection, null,
            "SELECT coalesce((SELECT CASE WHEN c.reltuples > 0 THEN pg_relation_size(c.oid)::float8 / c.reltuples ELSE 0 END FROM pg_class c WHERE c.oid = to_regclass(@g9_name)), 0)",
            cancellationToken, P("g9_name", NpgsqlDbType.Text, baseName)).ConfigureAwait(false);
        if (averageRowBytes <= 0) averageRowBytes = 32 + (24 * table.Columns.Count);

        var bare = PgNames.TrackingBare(table.Schema, table.Name);
        var keyStaging = PgTypes.KeyStaging(table.KeyType);
        return new PgTable
        {
            Model = table,
            BaseName = baseName,
            TrackingName = _schema + "." + PgNames.Quote(bare),
            TrackingBare = bare,
            KeySetName = _schema + "." + PgNames.Quote(bare + "_keys"),
            Key = key,
            KeyStagingType = keyStaging.Sql,
            KeyStagingDbType = keyStaging.DbType,
            Columns = bound,
            RequiredUnwritableColumns = requiredUnwritable,
            ExtraUniqueIndexes = uniqueIndexes,
            AverageRowBytes = averageRowBytes
        };
    }

    private async Task<List<string>> ValidateSqlAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        async Task CheckAsync(string label, string sql, Action<NpgsqlParameterCollection>? bind)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = Command(connection, transaction, "EXPLAIN " + sql);
                bind?.Invoke(command.Parameters);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                errors.Add($"{label}: {exception.MessageText}");
            }
            finally
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var table in _tables.Values)
        {
            var reads = string.Concat(table.Model.Columns.Select(c => ", " + ReadExpression(table, c, "t")));
            var where = table.Model.WriteRuleSql is null ? string.Empty : " WHERE " + BindPlain(table.Model.WriteRuleSql, "t");
            await CheckAsync($"{table.Model.LogicalName} column expressions / write rule",
                $"SELECT t.{table.Key.Quoted}{reads} FROM {table.BaseName} AS t{where}", null).ConfigureAwait(false);
        }

        // Row writers (ADR-0025) are validated for every table they claim, in a scope like a push's.
        var writtenByApplication = new HashSet<PgTable>();
        if (_services.GetService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>() is { } scopes)
        {
            await using var scope = scopes.CreateAsyncScope();
            var writers = new List<G9SyncData.Server.Pipeline.IG9RowWriter>();
            foreach (var writer in scope.ServiceProvider.GetServices<G9SyncData.Server.Pipeline.IG9RowWriter>())
            {
                try
                {
                    await writer.PrepareAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                    writers.Add(writer);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors.Add($"row writer {writer.GetType().Name} failed to prepare: {exception.Message}");
                }
            }

            foreach (var writer in writers.Where(w => !_tables.Values.Any(t => w.AppliesTo(t.Model))))
            {
                // A writer that claims no table is almost certainly misconfigured, and would otherwise do nothing, silently.
                errors.Add($"row writer {writer.GetType().Name} applies to no synced table");
            }

            foreach (var table in _tables.Values)
            {
                foreach (var writer in writers.Where(w => w.AppliesTo(table.Model)))
                {
                    writtenByApplication.Add(table);
                    try
                    {
                        foreach (var problem in await writer.ValidateAsync(table.Model, scope.ServiceProvider, cancellationToken).ConfigureAwait(false))
                        {
                            errors.Add($"{table.Model.LogicalName} row writer {writer.GetType().Name}: {problem}");
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        errors.Add($"{table.Model.LogicalName} row writer {writer.GetType().Name} failed to validate: {exception.Message}");
                    }
                }
            }
        }

        foreach (var table in _tables.Values.Where(t => t.RequiredUnwritableColumns.Count > 0 && !writtenByApplication.Contains(t)))
        {
            foreach (var column in table.RequiredUnwritableColumns)
            {
                errors.Add($"{table.Model.LogicalName}.{column} is NOT NULL without a default, so uploaded inserts need it: add it as a writable column, " +
                           "make the table DownloadOnly, or write its uploads through a row writer that supplies it.");
            }
        }

        foreach (var subscription in _model.Subscriptions)
        {
            foreach (var include in subscription.Tables)
            {
                var table = Get(include.Table);
                var when = include.WhenSql is null ? "true" : Bind(include.WhenSql, "t", subscription);
                var label = $"subscription '{subscription.Name}' / {include.Table.LogicalName}";

                if (include.ScopeProvider is null || !_options.ValidateScopeProviders)
                {
                    var template = include.ScopeProvider is null ? include.ScopeSql : "true";
                    await CheckAsync(label, $"SELECT 1 FROM {table.BaseName} AS t WHERE ({Bind(template, "t", subscription)}) AND ({when})",
                        parameters => BindNulls(parameters, subscription, "p_")).ConfigureAwait(false);
                    continue;
                }

                // A provider's key query exists per request, so it is PROBED: built for sample callers, then EXPLAINed like a model
                // scope. A wrong entity, an untranslatable query, a missing table or a key-type mismatch then stops the deployment
                // instead of failing every device's sync. Two probes, because an ORM's SQL depends on parameter VALUES (EF Core
                // turns `column == null` on a non-nullable column into FALSE and drops the whole predicate - and the table in it):
                //   1. typed non-null samples, which produce the general shape of the query;
                //   2. every value null and an anonymous caller - a missing server parameter binds null at run time too.
                foreach (var (probe, values, auth) in ScopeProbes(subscription))
                {
                    PgScope scope;
                    try
                    {
                        var fragment = await include.ScopeProvider.CreateAsync(new G9CSyncScopeContext
                        {
                            Subscription = subscription,
                            Table = include.Table,
                            Values = values,
                            Auth = auth,
                            Services = _services
                        }, cancellationToken).ConfigureAwait(false);
                        scope = new PgScope(fragment.KeysSql, fragment.Parameters, table.Key.Quoted, IsProvided: true);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        errors.Add($"{label} ({probe}): the scope provider failed to build its key query - {exception.GetType().Name}: {exception.Message}");
                        break;
                    }

                    var before = errors.Count;
                    await CheckAsync($"{label} ({probe})",
                        $"SELECT 1 FROM {table.BaseName} AS t WHERE ({BindScope(scope, "t", subscription, "p_")}) AND ({when})",
                        parameters =>
                        {
                            BindNulls(parameters, subscription, "p_");
                            BindScopeParameters(parameters, scope, "p_");
                        }).ConfigureAwait(false);
                    if (errors.Count > before) break;
                }
            }
        }

        return errors;
    }

    // ================================================================ snapshots, horizon, clients

    public async Task<string> GetCurrentSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, $"SELECT pg_current_snapshot()::text, (SELECT epoch FROM {_schema}.install WHERE id = 1)");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = reader.GetString(0);
        if (_initialized && !reader.IsDBNull(1)) CurrentEpoch = reader.GetGuid(1);
        return snapshot;
    }

    /// <summary>
    ///     The epoch, after making sure it still describes THIS database. The install row travels with the data: a restored backup, a
    ///     <c>CREATE DATABASE … TEMPLATE</c> copy or a dump loaded elsewhere carries the epoch of the database it came from, and every
    ///     device cursor issued by that database after the backup would be accepted as current — deltas would then silently skip
    ///     whatever the restore rolled back or the device holds beyond it. A database's OID changes in all of those cases, so a
    ///     different OID rotates the epoch: every device reconciles once, which is exact whatever the history was.
    /// </summary>
    /// <remarks>
    ///     A point-in-time restore or a failover to a lagging standby keeps the OID; there the xid counter goes BACK, which
    ///     <see cref="IsSnapshotAheadAsync" /> detects per cursor and per pull token.
    /// </remarks>
    private async Task<Guid> VerifyDatabaseIdentityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<PgTable> tables,
        CancellationToken cancellationToken)
    {
        var tracked = await ScalarAsync<bool>(connection, transaction,
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = @g9_schema AND table_name = 'install' AND column_name = 'database_oid')",
            cancellationToken, P("g9_schema", NpgsqlDbType.Text, _options.Schema)).ConfigureAwait(false);
        if (!tracked)
        {
            // An install older than this check that nobody upgraded (AutoInstall off): it keeps working, without the protection.
            LogIdentityNotTracked(_logger, _options.Schema);
            return await ScalarAsync<Guid>(connection, transaction, $"SELECT epoch FROM {_schema}.install WHERE id = 1", cancellationToken).ConfigureAwait(false);
        }

        Guid epoch;
        long? recorded;
        long current;
        await using (var command = Command(connection, transaction,
                         $"SELECT epoch, database_oid, (SELECT oid::bigint FROM pg_database WHERE datname = current_database()) FROM {_schema}.install WHERE id = 1"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            epoch = reader.GetGuid(0);
            recorded = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            current = reader.GetInt64(2);
        }

        if (recorded is null)
        {
            await ExecuteAsync(connection, transaction, $"UPDATE {_schema}.install SET database_oid = @g9_oid WHERE id = 1", cancellationToken,
                P("g9_oid", NpgsqlDbType.Bigint, current)).ConfigureAwait(false);
            return epoch;
        }

        if (recorded == current) return epoch;

        var rotated = await ScalarAsync<Guid>(connection, transaction,
            $"""
             UPDATE {_schema}.install SET epoch = gen_random_uuid(), database_oid = @g9_oid, epoch_changed_at = now(),
                    epoch_change_reason = 'database identity changed (restored backup or copy)', wal_rescan_pending = true
             WHERE id = 1 RETURNING epoch
             """,
            cancellationToken, P("g9_oid", NpgsqlDbType.Bigint, current)).ConfigureAwait(false);

        // Key sets were built by the database this data came from: never serve one to a device of this one.
        await ExecuteAsync(connection, transaction, $"DELETE FROM {_schema}.key_sets", cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            // A table added to the model since the backup has no key table yet (registration creates it right after this).
            if (await ScalarAsync<bool>(connection, transaction, "SELECT to_regclass(@g9_name) IS NOT NULL", cancellationToken,
                    P("g9_name", NpgsqlDbType.Text, table.KeySetName)).ConfigureAwait(false))
            {
                await ExecuteAsync(connection, transaction, $"DELETE FROM {table.KeySetName}", cancellationToken).ConfigureAwait(false);
            }
        }
        LogEpochRotated(_logger, recorded.Value, current, epoch, rotated);
        return rotated;
    }

    /// <summary>
    ///     True when <paramref name="snapshot" /> is NEWER than this database: its xmax lies beyond the next xid the database will
    ///     assign. Xids only move forward in a live cluster, so this happens only after the database went back in time — a
    ///     point-in-time restore, or a failover to a standby that had not replayed as far as the device had read.
    /// </summary>
    public async Task<bool> IsSnapshotAheadAsync(string snapshot, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ScalarAsync<bool>(connection, null,
                "SELECT pg_snapshot_xmax(@g9_snapshot::pg_snapshot) > pg_snapshot_xmax(pg_current_snapshot())",
                cancellationToken, P("g9_snapshot", NpgsqlDbType.Text, snapshot)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.InvalidTextRepresentation or PostgresErrorCodes.InvalidParameterValue)
        {
            return true;
        }
    }

    public async Task<bool> IsBeyondTombstoneHorizonAsync(string snapshot, IReadOnlyCollection<G9CSyncServerTable> tables, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tables);
        EnsureInitialized();
        var ids = tables.Select(t => Get(t).TableId).ToArray();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Every purged tombstone must be visible in the snapshot. Purged xids are committed and <= max_removed_xid, so the
            // snapshot is safe when that xid is visible AND none of the snapshot's in-progress xids up to it has committed since.
            // Comparing xmin alone is wrong: xids are cluster-wide, so any long transaction (even in another database) pins xmin
            // of every new snapshot and would push every client into endless reconciliation (2026-09-16 log entry).
            return await ScalarAsync<bool>(connection, null,
                $"""
                 SELECT coalesce(bool_or(t.max_removed_xid IS NOT NULL AND (
                            NOT pg_visible_in_snapshot(t.max_removed_xid, @g9_snapshot::pg_snapshot)
                            OR EXISTS (SELECT 1 FROM pg_snapshot_xip(@g9_snapshot::pg_snapshot) AS x(xid)
                                       WHERE x.xid <= t.max_removed_xid AND coalesce(pg_xact_status(x.xid), 'committed') = 'committed'))), false)
                 FROM {_schema}.tables AS t WHERE t.table_id = ANY(@g9_ids)
                 """,
                cancellationToken, P("g9_snapshot", NpgsqlDbType.Text, snapshot), P("g9_ids", NpgsqlDbType.Array | NpgsqlDbType.Integer, ids)).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.InvalidTextRepresentation or PostgresErrorCodes.InvalidParameterValue)
        {
            return true;
        }
    }

    /// <summary>
    ///     Records the device and binds it to the caller. The first authenticated use claims it; later uses must match, because handing a
    ///     device record to whoever quotes its id would hand over the push receipt stored on it too (ADR-0047).
    /// </summary>
    public async Task TouchClientAsync(Guid clientId, string? userId, string? appVersion, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            $"INSERT INTO {_schema}.clients (client_id, user_id, app_version) VALUES (@g9_client, @g9_user, @g9_version) " +
            "ON CONFLICT (client_id) DO UPDATE SET user_id = coalesce(clients.user_id, EXCLUDED.user_id), app_version = EXCLUDED.app_version, " +
            "last_seen = now() RETURNING user_id");
        command.Parameters.Add(P("g9_client", NpgsqlDbType.Uuid, clientId));
        command.Parameters.Add(P("g9_user", NpgsqlDbType.Text, userId));
        command.Parameters.Add(P("g9_version", NpgsqlDbType.Text, appVersion));
        var owner = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (!string.Equals(owner, userId, StringComparison.Ordinal))
        {
            throw new G9CSyncException(G9SyncErrorCodes.Unauthorized, "This device belongs to a different user.");
        }
    }

    /// <summary>
    ///     The database's own clock (LES-0075): tracking stamps <c>changed_at</c> with <c>now()</c>, so this - not the app server's
    ///     clock - is what a device's edit times must be compared with. <c>clock_timestamp()</c>, not <c>now()</c>: the time of the
    ///     answer, not of a transaction's start.
    /// </summary>
    public async Task<DateTime> GetClockAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ScalarAsync<DateTime>(connection, null, "SELECT clock_timestamp()", cancellationToken).ConfigureAwait(false);
    }

    // ================================================================ shared helpers

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("The PostgreSQL sync store is not initialised; call InitializeAsync first.");
    }

    private PgTable Get(G9CSyncServerTable table)
    {
        EnsureInitialized();
        return _tables.TryGetValue(table, out var pg)
            ? pg
            : throw new InvalidOperationException($"Table {table.LogicalName} is not part of this store's model.");
    }

    private NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = _options.CommandTimeoutSeconds };

    private async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = Command(connection, transaction, sql);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is T typed ? typed : throw new InvalidOperationException($"Expected {typeof(T).Name} from: {sql}");
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    private static string Bind(string sql, string alias, G9CSyncSubscriptionDefinition definition, string prefix = "p_") =>
        G9CSqlTemplate.Bind(sql, alias, prefix, definition.ParameterNames);

    private static string BindPlain(string sql, string alias) => G9CSqlTemplate.Bind(sql, alias, string.Empty, EmptyNames.Instance);

    private static void BindSubscription(NpgsqlParameterCollection parameters, G9CResolvedSubscription subscription, string prefix)
    {
        foreach (var parameter in subscription.Definition.Parameters)
        {
            subscription.Values.TryGetValue(parameter.Name, out var value);
            parameters.Add(P(prefix + parameter.Name, PgTypes.Parameter(parameter.Type, parameter.IsList), value));
        }
    }

    private static void BindNulls(NpgsqlParameterCollection parameters, G9CSyncSubscriptionDefinition definition, string prefix)
    {
        foreach (var parameter in definition.Parameters)
        {
            parameters.Add(P(prefix + parameter.Name, PgTypes.Parameter(parameter.Type, parameter.IsList), null));
        }
    }

    private static string ReadExpression(PgTable table, G9CSyncServerColumn column, string alias)
    {
        if (column.ReadSql is not null) return "(" + BindPlain(column.ReadSql, alias) + ")";
        var db = table.Bound(column).Db!;
        var expression = alias + "." + db.Quoted;
        return column.Type switch
        {
            G9ESyncColumnType.String when !PgTypes.IsPlainText(db.TypeName) => expression + "::text",
            G9ESyncColumnType.Double when db.TypeName == "numeric" => expression + "::double precision",
            G9ESyncColumnType.Decimal when db.TypeName != "numeric" => expression + "::numeric",
            _ => expression
        };
    }

    private static NpgsqlParameter KeyParameter(string name, PgTable table, string key) => table.Model.KeyType switch
    {
        G9ESyncColumnType.Guid => P(name, NpgsqlDbType.Uuid, Guid.Parse(key)),
        G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 => P(name, NpgsqlDbType.Bigint, long.Parse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)),
        _ => P(name, NpgsqlDbType.Text, key)
    };

    private static NpgsqlParameter KeyArrayParameter(string name, PgTable table, IEnumerable<string> keys) => table.Model.KeyType switch
    {
        G9ESyncColumnType.Guid => P(name, NpgsqlDbType.Array | NpgsqlDbType.Uuid, keys.Select(Guid.Parse).ToArray()),
        G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 => P(name, NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            keys.Select(k => long.Parse(k, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)).ToArray()),
        _ => P(name, NpgsqlDbType.Array | NpgsqlDbType.Text, keys.ToArray())
    };

    /// <summary>Reads the key at <paramref name="ordinal" />, starts a value row, and returns the canonical key.</summary>
    private static string BeginRow(G9CRowBatchWriter writer, NpgsqlDataReader reader, PgTable table, long version, int ordinal = 0)
    {
        switch (table.Model.KeyType)
        {
            case G9ESyncColumnType.Guid:
            {
                var key = reader.GetGuid(ordinal);
                writer.BeginRow(key, version);
                return G9CSyncKey.FromGuid(key);
            }
            case G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64:
            {
                var key = reader.GetInt64(ordinal);
                writer.BeginRow(key, version);
                return G9CSyncKey.FromInt64(key);
            }
            default:
            {
                var key = reader.GetString(ordinal);
                writer.BeginRow(key, version);
                return key;
            }
        }
    }

    private static string ReadKey(NpgsqlDataReader reader, PgTable table, int ordinal = 0) => table.Model.KeyType switch
    {
        G9ESyncColumnType.Guid => G9CSyncKey.FromGuid(reader.GetGuid(ordinal)),
        G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64 => G9CSyncKey.FromInt64(reader.GetInt64(ordinal)),
        _ => reader.GetString(ordinal)
    };

    private static void WriteValue(G9CRowBatchWriter writer, NpgsqlDataReader reader, int ordinal, G9ESyncColumnType type)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNull();
            return;
        }

        switch (type)
        {
            case G9ESyncColumnType.Boolean:
                writer.WriteBoolean(reader.GetBoolean(ordinal));
                break;
            case G9ESyncColumnType.Int32:
                writer.WriteInt32(reader.GetInt32(ordinal));
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
                writer.WriteDateTime(reader.GetDateTime(ordinal));
                break;
            case G9ESyncColumnType.DateTimeOffset:
                writer.WriteDateTimeOffset(reader.GetFieldValue<DateTimeOffset>(ordinal));
                break;
            case G9ESyncColumnType.TimeSpan:
                writer.WriteTimeSpan(reader.GetFieldValue<TimeSpan>(ordinal));
                break;
            case G9ESyncColumnType.Bytes:
                writer.WriteBytes(reader.GetFieldValue<byte[]>(ordinal));
                break;
            default:
                throw new InvalidOperationException($"Unsupported column type {type}.");
        }
    }

    private static void AppendKeySection(G9CRowBatchWriter writer, G9ERowBatchOp op, PgTable table, List<(string Key, long Version)>? rows)
    {
        if (rows is not { Count: > 0 }) return;
        writer.BeginSection(op, table.TableId, table.Model.KeyType, []);
        foreach (var (key, version) in rows) writer.AddKey(key, version);
        writer.EndSection();
    }

    private static G9CSyncException ModelMismatch(string what, IEnumerable<string> errors) =>
        new(G9SyncErrorCodes.ModelInvalid,
            $"The G9SyncData model does not match the PostgreSQL database ({what}):" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(e => " - " + e)));

    [LoggerMessage(EventId = 7350, Level = LogLevel.Warning,
        Message = "G9SyncData: this database is not the one capture was installed on (database oid {Recorded} → {Current}: a restored backup or a copy). Epoch {OldEpoch} rotated to {NewEpoch}; every device reconciles once")]
    private static partial void LogEpochRotated(ILogger logger, long recorded, long current, Guid oldEpoch, Guid newEpoch);

    [LoggerMessage(EventId = 7351, Level = LogLevel.Warning,
        Message = "G9SyncData: install in schema {Schema} predates database identity tracking and AutoInstall is off; a restored backup would not be detected until the install script is re-applied")]
    private static partial void LogIdentityNotTracked(ILogger logger, string schema);

    [LoggerMessage(EventId = 22001, Level = LogLevel.Information, Message = "Installed change capture for {Table} (table id {TableId}, ordered apply {Ordered})")]
    private static partial void LogCaptureInstalled(ILogger logger, string table, int tableId, bool ordered);
}
