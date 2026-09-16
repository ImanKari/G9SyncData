using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
using G9SyncData.Server.PostgreSql.Internal;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     The upload apply. One transaction per push: idempotency lock → per table (in model order): COPY into a temp
///     staging table → write authorisation → validators → transformers → conflict policy → delete handlers → apply with
///     bisection isolation → post-write authorisation → results. Rejected rows are excluded, never fatal (D-10).
/// </summary>
public sealed partial class G9CPostgresSyncStore
{
    private const string StageMeta = "g9_seq, g9_op, g9_key, g9_base_ver, g9_dirty_mask, g9_hlc";
    private const string ArrayFilter = " AND s.g9_seq = ANY(@g9_seqs)";
    private const string RowFilter = " AND s.g9_seq = @g9_seq";

    public async Task<G9DtPushResult> ApplyPushAsync(G9CStorePushRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureInitialized();
        var result = new G9DtPushResult { BatchId = request.BatchId };
        var work = PlanPush(request, result);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, transaction,
            $"INSERT INTO {_schema}.clients (client_id, user_id) VALUES (@g9_client, @g9_user) ON CONFLICT (client_id) DO UPDATE SET last_seen = now()",
            cancellationToken, P("g9_client", NpgsqlDbType.Uuid, request.ClientId), P("g9_user", NpgsqlDbType.Text, request.Auth.UserId)).ConfigureAwait(false);

        long lastBatch;
        byte[]? stored;
        await using (var command = Command(connection, transaction,
                         $"SELECT last_push_batch, last_push_result FROM {_schema}.clients WHERE client_id = @g9_client FOR UPDATE"))
        {
            command.Parameters.Add(P("g9_client", NpgsqlDbType.Uuid, request.ClientId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            lastBatch = reader.GetInt64(0);
            stored = reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1);
        }

        if (request.BatchId <= lastBatch)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var replay = request.BatchId == lastBatch && stored is { Length: > 0 }
                ? JsonSerializer.Deserialize(stored, G9SyncJsonContext.Default.G9DtPushResult) ?? new G9DtPushResult()
                : new G9DtPushResult();
            replay.BatchId = request.BatchId;
            replay.AlreadyApplied = true;
            return replay;
        }

        await ExecuteAsync(connection, transaction, "SELECT set_config('g9sync.origin', @g9_origin, true)", cancellationToken,
            P("g9_origin", NpgsqlDbType.Text, request.ClientId.ToString())).ConfigureAwait(false);

        foreach (var tableWork in work.OrderBy(w => w.Table.Model.Order))
        {
            await ApplyTableAsync(connection, transaction, request, tableWork, result, cancellationToken).ConfigureAwait(false);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(result, G9SyncJsonContext.Default.G9DtPushResult);
        await ExecuteAsync(connection, transaction,
            $"UPDATE {_schema}.clients SET last_push_batch = @g9_batch, last_push_result = @g9_result, last_seen = now() WHERE client_id = @g9_client",
            cancellationToken, P("g9_batch", NpgsqlDbType.Bigint, request.BatchId), P("g9_result", NpgsqlDbType.Bytea, json),
            P("g9_client", NpgsqlDbType.Uuid, request.ClientId)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    // ================================================================ planning

    private List<PushWork> PlanPush(G9CStorePushRequest request, G9DtPushResult result)
    {
        var work = new Dictionary<int, PushWork>();
        var total = 0;
        for (var frameIndex = 0; frameIndex < request.Frames.Count; frameIndex++)
        {
            try
            {
                using var reader = G9CRowBatchReader.Open(request.Frames[frameIndex]);
                var sectionIndex = -1;
                while (reader.NextSection())
                {
                    sectionIndex++;
                    total += reader.RowCount;
                    if (total > request.MaxRows)
                    {
                        throw new G9CSyncException(G9SyncErrorCodes.RejectTooMany, $"The push carries more than {request.MaxRows} rows; send smaller batches.");
                    }

                    var problem = ValidatePushSection(reader, request, out var table, out var shape);
                    if (problem is not null)
                    {
                        while (reader.ReadRow())
                        {
                            result.Rejections.Add(new G9DtRowRejection
                            {
                                TableId = reader.TableId, Key = reader.Key, Code = problem.Value.Code, Message = problem.Value.Message
                            });
                        }

                        continue;
                    }

                    if (!work.TryGetValue(reader.TableId, out var tableWork))
                    {
                        work[reader.TableId] = tableWork = new PushWork { Table = table!, Shape = shape! };
                    }

                    tableWork.Sections.Add((frameIndex, sectionIndex));
                }
            }
            catch (FormatException exception)
            {
                throw new G9CSyncException(G9SyncErrorCodes.RejectMalformed, "A push frame is malformed: " + exception.Message);
            }
        }

        if (result.Rejections.Count > request.MaxRejections) throw TooManyRejections(request.MaxRejections);
        return work.Values.ToList();
    }

    private (string Code, string Message)? ValidatePushSection(G9CRowBatchReader reader, G9CStorePushRequest request, out PgTable? table, out G9CPushTableShape? shape)
    {
        table = null;
        shape = null;
        if (reader.Op != G9ERowBatchOp.Push) return (G9SyncErrorCodes.RejectMalformed, $"Push frames may only contain push sections, not {reader.Op}.");
        if (!_byId.TryGetValue(reader.TableId, out table)) return (G9SyncErrorCodes.UnknownTable, $"Unknown table id {reader.TableId}.");
        if (!request.Tables.TryGetValue(reader.TableId, out shape))
        {
            return (G9SyncErrorCodes.RejectMalformed, $"The push did not declare a valid writable column list for {table.Model.LogicalName}.");
        }

        if (!table.Model.AcceptsUploads) return (G9SyncErrorCodes.RejectDirection, $"{table.Model.LogicalName} is download-only.");
        if (reader.KeyType != table.Model.KeyType) return (G9SyncErrorCodes.RejectMalformed, $"Key type {reader.KeyType} does not match {table.Model.KeyType}.");
        if (reader.ColumnCount != shape.Columns.Count) return (G9SyncErrorCodes.RejectMalformed, "Column count does not match the declared column list.");
        for (var i = 0; i < shape.Columns.Count; i++)
        {
            if (reader.ColumnTypes[i] != shape.Columns[i].Type)
            {
                return (G9SyncErrorCodes.RejectMalformed, $"Column '{shape.Columns[i].Name}' is {shape.Columns[i].Type}, the push sent {reader.ColumnTypes[i]}.");
            }
        }

        return null;
    }

    // ================================================================ one table

    private async Task ApplyTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, G9CStorePushRequest request, PushWork work,
        G9DtPushResult result, CancellationToken ct)
    {
        var table = work.Table;
        var model = table.Model;
        var columns = work.Shape.Columns.Select(table.Bound).ToArray();
        var key = table.Key.Quoted;
        var stage = PgNames.Quote("g9_stage_" + table.TableId.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..12]);

        // 1. staging
        var ddl = new StringBuilder()
            .Append("CREATE TEMP TABLE ").Append(stage).Append(" (g9_seq bigint NOT NULL, g9_op smallint NOT NULL, g9_key ").Append(table.KeyStagingType)
            .Append(" NOT NULL, g9_base_ver bigint NOT NULL, g9_dirty_mask bigint NOT NULL, g9_hlc bigint NOT NULL, g9_exists boolean NOT NULL DEFAULT false,")
            .Append(" g9_action smallint NOT NULL DEFAULT 1, g9_apply_mask bigint NULL, g9_conflict_mask bigint NULL, g9_server_ver bigint NULL,")
            .Append(" g9_server_deleted boolean NOT NULL DEFAULT false, g9_server_changed_ms bigint NULL, g9_partial boolean NOT NULL DEFAULT false,")
            .Append(" g9_code text NULL, g9_message text NULL, g9_sqlstate text NULL, g9_constraint text NULL, g9_column text NULL, g9_details text NULL");
        foreach (var column in columns) ddl.Append(", ").Append(column.Db!.Quoted).Append(' ').Append(column.StagingType);
        ddl.Append(") ON COMMIT DROP");
        await ExecuteAsync(connection, transaction, ddl.ToString(), ct).ConfigureAwait(false);
        await CopyPushRowsAsync(connection, request, work, stage, columns, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction,
            $"CREATE INDEX ON {stage} (g9_key); CREATE UNIQUE INDEX ON {stage} (g9_seq); ANALYZE {stage}; " +
            $"UPDATE {stage} AS s SET g9_exists = true FROM {table.BaseName} AS t WHERE t.{key} = s.g9_key", ct).ConfigureAwait(false);

        // 2. write authorisation for rows that already exist (update / delete must start inside a scope)
        var authBindings = new List<(G9CResolvedSubscription Subscription, string Prefix)>();
        var authPredicate = BuildAuthPredicate(table, request.Subscriptions, "t", authBindings);
        if (authPredicate is null)
        {
            await ExecuteAsync(connection, transaction,
                $"UPDATE {stage} SET g9_action = 0, g9_code = @g9_code, g9_message = @g9_message WHERE g9_action = 1", ct,
                P("g9_code", NpgsqlDbType.Text, G9SyncErrorCodes.RejectPermission),
                P("g9_message", NpgsqlDbType.Text, "No subscription of the caller authorises uploads to this table.")).ConfigureAwait(false);
        }
        else
        {
            await using var command = Command(connection, transaction,
                $"UPDATE {stage} AS s SET g9_action = 0, g9_code = @g9_code, g9_message = @g9_message WHERE s.g9_action = 1 AND s.g9_exists " +
                $"AND NOT EXISTS (SELECT 1 FROM {table.BaseName} AS t WHERE t.{key} = s.g9_key AND coalesce({authPredicate}, false))");
            command.Parameters.Add(P("g9_code", NpgsqlDbType.Text, G9SyncErrorCodes.RejectPermission));
            command.Parameters.Add(P("g9_message", NpgsqlDbType.Text, "The row is outside every subscription the caller may write to."));
            foreach (var (subscription, prefix) in authBindings) BindSubscription(command.Parameters, subscription, prefix);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 3. validators and transformers
        var context = new G9CUploadStepContext((rejections, token) => RejectRowsAsync(connection, transaction, stage, table, rejections, token))
        {
            Table = model,
            Columns = work.Shape.Columns,
            ClientId = request.ClientId,
            BatchId = request.BatchId,
            Caller = request.Caller,
            Auth = request.Auth,
            Connection = connection,
            Transaction = transaction,
            StagingTable = stage,
            Services = request.Services
        };

        foreach (var validator in request.Pipeline.Validators)
        {
            if (validator.AppliesTo(model)) await validator.ValidateAsync(context, ct).ConfigureAwait(false);
        }

        var transformed = false;
        foreach (var transformer in request.Pipeline.Transformers)
        {
            if (!transformer.AppliesTo(model)) continue;
            transformed = true;
            await transformer.TransformAsync(context, ct).ConfigureAwait(false);
        }

        // 4. conflicts
        await ExecuteAsync(connection, transaction, $"""
            UPDATE {stage} AS s
            SET g9_conflict_mask = CASE WHEN k.deleted THEN -1 ELSE {_schema}.changed_mask(k.col_vers, s.g9_base_ver, @g9_idx) END,
                g9_server_ver = k.ver,
                g9_server_deleted = k.deleted,
                g9_server_changed_ms = (extract(epoch FROM k.changed_at) * 1000)::bigint
            FROM {table.TrackingName} AS k
            WHERE k.{key} = s.g9_key AND s.g9_action = 1 AND k.ver <> s.g9_base_ver AND NOT (k.deleted AND s.g9_op = 2)
            """, ct, P("g9_idx", NpgsqlDbType.Array | NpgsqlDbType.Integer, columns.Select(c => c.Model.CaptureIndex + 1).ToArray())).ConfigureAwait(false);
        await ApplyConflictPolicyAsync(connection, transaction, request, table, columns, stage, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, $"UPDATE {stage} SET g9_apply_mask = g9_dirty_mask WHERE g9_action = 1 AND g9_apply_mask IS NULL", ct)
            .ConfigureAwait(false);

        // 5. delete handlers
        var deleteHandlers = request.Pipeline.DeleteHandlers.Where(h => h.AppliesTo(model)).ToArray();
        if (deleteHandlers.Length > 0)
        {
            var deletedKeys = await ReadKeysAsync(connection, transaction, $"SELECT g9_key::text FROM {stage} WHERE g9_action = 1 AND g9_op = 2 AND g9_exists", ct)
                .ConfigureAwait(false);
            if (deletedKeys.Count > 0)
            {
                foreach (var handler in deleteHandlers) await handler.HandleDeletesAsync(context, deletedKeys, ct).ConfigureAwait(false);
            }
        }

        // 6. apply with isolation, then post-write authorisation
        var rows = new List<ApplyRow>();
        await using (var command = Command(connection, transaction, $"SELECT g9_seq, g9_op, g9_exists FROM {stage} WHERE g9_action = 1 ORDER BY g9_seq"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(new ApplyRow(reader.GetInt64(0), reader.GetInt16(1), reader.GetBoolean(2)));
        }

        var state = BuildApplyState(connection, transaction, table, columns, stage, request.MaxRejections, result.Rejections.Count);
        var permissionFailures = new HashSet<long>();
        for (var attempt = 0; ; attempt++)
        {
            var savepoint = "g9_table_" + attempt.ToString(CultureInfo.InvariantCulture);
            await transaction.SaveAsync(savepoint, ct).ConfigureAwait(false);
            if (attempt > 0) await RemarkFailuresAsync(state, ct).ConfigureAwait(false);
            await ApplyIsolatedAsync(state, rows.Where(r => !state.Failures.ContainsKey(r.Seq) && !permissionFailures.Contains(r.Seq)).ToList(), ct)
                .ConfigureAwait(false);

            var violations = authPredicate is null
                ? []
                : await FindWriteViolationsAsync(connection, transaction, table, stage, authPredicate, authBindings, ct).ConfigureAwait(false);
            if (violations.Count == 0)
            {
                await transaction.ReleaseAsync(savepoint, ct).ConfigureAwait(false);
                break;
            }

            if (attempt >= 2) throw new G9CSyncException(G9SyncErrorCodes.ServerError, "Write authorisation did not converge for " + model.LogicalName + ".");
            await transaction.RollbackAsync(savepoint, ct).ConfigureAwait(false);
            permissionFailures.UnionWith(violations);
            await ExecuteAsync(connection, transaction,
                $"UPDATE {stage} SET g9_action = 0, g9_code = @g9_code, g9_message = @g9_message WHERE g9_seq = ANY(@g9_seqs)", ct,
                P("g9_code", NpgsqlDbType.Text, G9SyncErrorCodes.RejectPermission),
                P("g9_message", NpgsqlDbType.Text, "After the change the row would be outside every subscription the caller may write to."),
                P("g9_seqs", NpgsqlDbType.Array | NpgsqlDbType.Bigint, violations.ToArray())).ConfigureAwait(false);
        }

        // 7. results
        await CollectResultsAsync(connection, transaction, table, work, stage, transformed, result, ct).ConfigureAwait(false);
        if (result.Rejections.Count > request.MaxRejections) throw TooManyRejections(request.MaxRejections);
    }

    private static string? BuildAuthPredicate(PgTable table, IReadOnlyList<G9CResolvedSubscription> subscriptions, string alias,
        List<(G9CResolvedSubscription Subscription, string Prefix)> bindings)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < subscriptions.Count; i++)
        {
            var subscription = subscriptions[i];
            var include = subscription.Definition.FindTable(table.Model);
            if (include is null || !include.AllowUpload || !seen.Add(subscription.Key)) continue;
            var prefix = "a" + i.ToString(CultureInfo.InvariantCulture) + "_";
            parts.Add("(" + Bind(include.ScopeSql, alias, subscription.Definition, prefix) + ")");
            bindings.Add((subscription, prefix));
        }

        if (parts.Count == 0) return null;
        var predicate = "(" + string.Join(" OR ", parts) + ")";
        if (table.Model.WriteRuleSql is not null) predicate = "(" + predicate + " AND (" + BindPlain(table.Model.WriteRuleSql, alias) + "))";
        return predicate;
    }

    private async ValueTask RejectRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string stage, PgTable table,
        IReadOnlyList<G9CRowRejection> rejections, CancellationToken ct)
    {
        var keys = new string?[rejections.Count];
        var codes = new string?[rejections.Count];
        var messages = new string?[rejections.Count];
        var details = new string?[rejections.Count];
        for (var i = 0; i < rejections.Count; i++)
        {
            var rejection = rejections[i];
            keys[i] = G9CSyncKey.TryNormalize(table.Model.KeyType, rejection.Key, out var canonical) ? canonical : rejection.Key;
            codes[i] = string.IsNullOrWhiteSpace(rejection.Code) ? G9SyncErrorCodes.RejectValidation : rejection.Code;
            messages[i] = rejection.Message;
            details[i] = rejection.Details;
        }

        await ExecuteAsync(connection, transaction,
            $"UPDATE {stage} AS s SET g9_action = 0, g9_code = x.code, g9_message = x.message, g9_details = x.details " +
            "FROM unnest(@g9_keys, @g9_codes, @g9_messages, @g9_details) AS x(key, code, message, details) " +
            "WHERE s.g9_key::text = x.key AND s.g9_action = 1", ct,
            P("g9_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, keys), P("g9_codes", NpgsqlDbType.Array | NpgsqlDbType.Text, codes),
            P("g9_messages", NpgsqlDbType.Array | NpgsqlDbType.Text, messages),
            P("g9_details", NpgsqlDbType.Array | NpgsqlDbType.Text, details)).ConfigureAwait(false);
    }

    // ================================================================ COPY

    private static async Task CopyPushRowsAsync(NpgsqlConnection connection, G9CStorePushRequest request, PushWork work, string stage,
        PgBoundColumn[] columns, CancellationToken ct)
    {
        var names = string.Concat(columns.Select(c => ", " + c.Db!.Quoted));
        var allBits = columns.Length >= 63 ? -1L : (1L << columns.Length) - 1;
        var importer = await connection.BeginBinaryImportAsync($"COPY {stage} ({StageMeta}{names}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false);
        await using (importer.ConfigureAwait(false))
        {
            foreach (var (frameIndex, sectionIndex) in work.Sections)
            {
                using var reader = G9CRowBatchReader.Open(request.Frames[frameIndex]);
                for (var i = 0; i <= sectionIndex; i++) reader.NextSection();
                while (reader.ReadRow())
                {
                    await importer.StartRowAsync(ct).ConfigureAwait(false);
                    await importer.WriteAsync(reader.Sequence, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                    await importer.WriteAsync((short)(reader.PushOp == G9EPushRowOp.Delete ? 2 : 1), NpgsqlDbType.Smallint, ct).ConfigureAwait(false);
                    switch (work.Table.Model.KeyType)
                    {
                        case G9ESyncColumnType.Guid:
                            await importer.WriteAsync(reader.KeyGuid, NpgsqlDbType.Uuid, ct).ConfigureAwait(false);
                            break;
                        case G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64:
                            await importer.WriteAsync(reader.KeyInt64, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                            break;
                        default:
                            await importer.WriteAsync(reader.Key, NpgsqlDbType.Text, ct).ConfigureAwait(false);
                            break;
                    }

                    await importer.WriteAsync(reader.BaseVersion, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                    await importer.WriteAsync(reader.DirtyMask & allBits, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);
                    await importer.WriteAsync(reader.HlcMs, NpgsqlDbType.Bigint, ct).ConfigureAwait(false);

                    for (var i = 0; i < columns.Length; i++)
                    {
                        if (!reader.HasValues || reader.IsNull(i))
                        {
                            await importer.WriteNullAsync(ct).ConfigureAwait(false);
                            continue;
                        }

                        await WriteStagedValueAsync(importer, reader, i, columns[i], ct).ConfigureAwait(false);
                    }
                }
            }

            await importer.CompleteAsync(ct).ConfigureAwait(false);
        }
    }

    private static Task WriteStagedValueAsync(NpgsqlBinaryImporter importer, G9CRowBatchReader reader, int column, PgBoundColumn bound, CancellationToken ct)
    {
        var type = bound.StagingDbType;
        return bound.Model.Type switch
        {
            G9ESyncColumnType.Boolean => importer.WriteAsync(reader.GetBoolean(column), type, ct),
            G9ESyncColumnType.Int32 => importer.WriteAsync(reader.GetInt32(column), type, ct),
            G9ESyncColumnType.Int64 => importer.WriteAsync(reader.GetInt64(column), type, ct),
            G9ESyncColumnType.Double => importer.WriteAsync(reader.GetDouble(column), type, ct),
            G9ESyncColumnType.Decimal => importer.WriteAsync(reader.GetDecimal(column), type, ct),
            G9ESyncColumnType.String => importer.WriteAsync(reader.GetString(column), type, ct),
            G9ESyncColumnType.Guid => importer.WriteAsync(reader.GetGuid(column), type, ct),
            G9ESyncColumnType.DateTime => importer.WriteAsync(NormalizeDateTime(reader.GetDateTime(column), type), type, ct),
            G9ESyncColumnType.DateTimeOffset => importer.WriteAsync(reader.GetDateTimeOffset(column).ToUniversalTime(), type, ct),
            G9ESyncColumnType.TimeSpan => importer.WriteAsync(reader.GetTimeSpan(column), type, ct),
            G9ESyncColumnType.Bytes => importer.WriteAsync(reader.GetBytes(column).ToArray(), type, ct),
            _ => throw new InvalidOperationException($"Unsupported column type {bound.Model.Type}.")
        };
    }

    private static DateTime NormalizeDateTime(DateTime value, NpgsqlDbType type) => type == NpgsqlDbType.Timestamp
        ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
        : value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    // ================================================================ conflicts

    private async Task ApplyConflictPolicyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, G9CStorePushRequest request, PgTable table,
        PgBoundColumn[] columns, string stage, CancellationToken ct)
    {
        var serverWins = $"UPDATE {stage} SET g9_action = 0, g9_code = CASE WHEN g9_server_deleted THEN '{G9SyncErrorCodes.ConflictDeleted}' ELSE '{G9SyncErrorCodes.ConflictServerWins}' END " +
                         "WHERE g9_conflict_mask IS NOT NULL AND g9_action = 1";
        switch (table.Model.ConflictPolicy)
        {
            case G9EConflictPolicy.ClientWins:
                return;
            case G9EConflictPolicy.ColumnMerge:
                await ExecuteAsync(connection, transaction, serverWins + " AND (g9_conflict_mask = -1 OR g9_op = 2 OR NOT g9_exists)", ct).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, $"""
                    UPDATE {stage}
                    SET g9_apply_mask = g9_dirty_mask & ~g9_conflict_mask,
                        g9_partial = (g9_dirty_mask & g9_conflict_mask) <> 0 AND (g9_dirty_mask & ~g9_conflict_mask) <> 0,
                        g9_code = CASE WHEN (g9_dirty_mask & g9_conflict_mask) <> 0 THEN '{G9SyncErrorCodes.ConflictColumns}' ELSE NULL END
                    WHERE g9_conflict_mask IS NOT NULL AND g9_action = 1;
                    UPDATE {stage} SET g9_action = 0, g9_code = coalesce(g9_code, '{G9SyncErrorCodes.ConflictColumns}')
                    WHERE g9_conflict_mask IS NOT NULL AND g9_action = 1 AND g9_apply_mask = 0
                    """, ct).ConfigureAwait(false);
                return;
            case G9EConflictPolicy.LastWriterWins:
                await ExecuteAsync(connection, transaction, serverWins + " AND g9_hlc <= coalesce(g9_server_changed_ms, 0)", ct).ConfigureAwait(false);
                return;
            case G9EConflictPolicy.Custom:
                if (await ResolveCustomConflictsAsync(connection, transaction, request, table, columns, stage, ct).ConfigureAwait(false)) return;
                await ExecuteAsync(connection, transaction, serverWins, ct).ConfigureAwait(false);
                return;
            default:
                await ExecuteAsync(connection, transaction, serverWins, ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task<bool> ResolveCustomConflictsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, G9CStorePushRequest request,
        PgTable table, PgBoundColumn[] columns, string stage, CancellationToken ct)
    {
        var resolver = request.Pipeline.ConflictResolvers.FirstOrDefault(r => r.AppliesTo(table.Model));
        if (resolver is null) return false;

        var key = table.Key.Quoted;
        var clientRows = new List<(string Key, bool IsDelete, long Base, long ServerVersion, bool ServerDeleted, long Dirty, Dictionary<string, object?> Values)>();
        await using (var command = Command(connection, transaction,
                         $"SELECT g9_key::text, g9_op, g9_base_ver, coalesce(g9_server_ver, 0), g9_server_deleted, g9_dirty_mask" +
                         $"{string.Concat(columns.Select(c => ", " + c.Db!.Quoted))} FROM {stage} WHERE g9_conflict_mask IS NOT NULL AND g9_action = 1"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < columns.Length; i++) values[columns[i].Model.Name] = reader.IsDBNull(6 + i) ? null : reader.GetValue(6 + i);
                clientRows.Add((reader.GetString(0), reader.GetInt16(1) == 2, reader.GetInt64(2), reader.GetInt64(3), reader.GetBoolean(4), reader.GetInt64(5), values));
            }
        }

        if (clientRows.Count == 0) return true;

        var serverValues = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        await using (var command = Command(connection, transaction,
                         $"SELECT {PgTypes.KeyText(table.Model.KeyType, "t." + key)}{string.Concat(columns.Select(c => ", t." + c.Db!.Quoted))} " +
                         $"FROM {table.BaseName} AS t WHERE t.{key} IN (SELECT g9_key FROM {stage} WHERE g9_conflict_mask IS NOT NULL AND g9_action = 1)"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var i = 0; i < columns.Length; i++) values[columns[i].Model.Name] = reader.IsDBNull(1 + i) ? null : reader.GetValue(1 + i);
                serverValues[reader.GetString(0)] = values;
            }
        }

        var rows = clientRows.Select(r => new G9CConflictRow
        {
            Key = r.Key,
            IsDelete = r.IsDelete,
            BaseVersion = r.Base,
            ServerVersion = r.ServerVersion,
            ServerDeleted = r.ServerDeleted,
            DirtyMask = r.Dirty,
            ClientValues = r.Values,
            ServerValues = serverValues.GetValueOrDefault(r.Key)
        }).ToArray();

        var decisions = await resolver.ResolveAsync(new G9CConflictContext
        {
            Table = table.Model, Columns = columns.Select(c => c.Model).ToArray(), ClientId = request.ClientId, Rows = rows, Services = request.Services
        }, ct).ConfigureAwait(false);

        var byKey = new Dictionary<string, G9CConflictResolution>(StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            byKey[G9CSyncKey.TryNormalize(table.Model.KeyType, decision.Key, out var canonical) ? canonical : decision.Key] = decision;
        }

        var keep = new List<string>();
        var accept = new List<string>();
        var reject = new List<G9CRowRejection>();
        foreach (var row in rows)
        {
            if (!byKey.TryGetValue(row.Key, out var decision) || decision.Decision == G9EConflictDecision.KeepServer) keep.Add(row.Key);
            else if (decision.Decision == G9EConflictDecision.AcceptClient) accept.Add(row.Key);
            else reject.Add(new G9CRowRejection(row.Key, decision.RejectCode ?? G9SyncErrorCodes.ConflictResolverRejected, decision.Message));
        }

        if (accept.Count > 0)
        {
            await ExecuteAsync(connection, transaction, $"UPDATE {stage} SET g9_conflict_mask = NULL WHERE g9_key::text = ANY(@g9_keys)", ct,
                P("g9_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, accept.ToArray())).ConfigureAwait(false);
        }

        if (reject.Count > 0) await RejectRowsAsync(connection, transaction, stage, table, reject, ct).ConfigureAwait(false);

        if (keep.Count > 0)
        {
            await ExecuteAsync(connection, transaction,
                $"UPDATE {stage} SET g9_action = 0, g9_code = CASE WHEN g9_server_deleted THEN '{G9SyncErrorCodes.ConflictDeleted}' ELSE '{G9SyncErrorCodes.ConflictServerWins}' END " +
                "WHERE g9_key::text = ANY(@g9_keys) AND g9_action = 1", ct,
                P("g9_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, keep.ToArray())).ConfigureAwait(false);
        }

        return true;
    }

    // ================================================================ apply + isolation

    private static ApplyState BuildApplyState(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table, PgBoundColumn[] columns,
        string stage, int maxRejections, int priorRejections)
    {
        var key = table.Key.Quoted;
        var delete = $"DELETE FROM {table.BaseName} AS t USING {stage} AS s WHERE t.{key} = s.g9_key AND s.g9_action = 1 AND s.g9_op = 2{{F}}";
        string? update = null;
        if (columns.Length > 0)
        {
            var sets = string.Join(", ", columns.Select((c, i) =>
                $"{c.Db!.Quoted} = CASE WHEN (s.g9_apply_mask & {PgNames.Bits(i)}) <> 0 THEN {ValueExpression(c, "s")} ELSE t.{c.Db.Quoted} END"));
            update = $"UPDATE {table.BaseName} AS t SET {sets} FROM {stage} AS s WHERE t.{key} = s.g9_key AND s.g9_action = 1 AND s.g9_op = 1 AND s.g9_exists{{F}}";
        }

        var insert = $"INSERT INTO {table.BaseName} ({key}{string.Concat(columns.Select(c => ", " + c.Db!.Quoted))}) " +
                     $"SELECT s.g9_key::{table.Key.DbType}{string.Concat(columns.Select(c => ", " + ValueExpression(c, "s")))} " +
                     $"FROM {stage} AS s WHERE s.g9_action = 1 AND s.g9_op = 1 AND NOT s.g9_exists{{F}} ORDER BY s.g9_seq";

        return new ApplyState
        {
            Connection = connection,
            Transaction = transaction,
            Table = table,
            Stage = stage,
            DeleteSet = delete.Replace("{F}", ArrayFilter, StringComparison.Ordinal),
            DeleteRow = delete.Replace("{F}", RowFilter, StringComparison.Ordinal),
            UpdateSet = update?.Replace("{F}", ArrayFilter, StringComparison.Ordinal),
            UpdateRow = update?.Replace("{F}", RowFilter, StringComparison.Ordinal),
            InsertSet = insert.Replace("{F}", ArrayFilter, StringComparison.Ordinal),
            InsertRow = insert.Replace("{F}", RowFilter, StringComparison.Ordinal),
            MaxRejections = maxRejections,
            PriorRejections = priorRejections
        };
    }

    private static string ValueExpression(PgBoundColumn column, string alias)
    {
        var staged = alias + "." + column.Db!.Quoted;
        if (column.Model.WriteSql is not null) return "(" + G9CSqlTemplate.BindValue(column.Model.WriteSql, staged) + ")";
        return string.Equals(column.StagingTypeName, column.Db.TypeName, StringComparison.Ordinal) ? staged : staged + "::" + column.Db.DbType;
    }

    private async Task ApplyIsolatedAsync(ApplyState state, List<ApplyRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var savepoint = "g9_apply_" + (state.SavepointCounter++).ToString(CultureInfo.InvariantCulture);
        await state.Transaction.SaveAsync(savepoint, ct).ConfigureAwait(false);
        try
        {
            await ExecuteApplyAsync(state, rows, ct).ConfigureAwait(false);
            await ExecuteAsync(state.Connection, state.Transaction, "SET CONSTRAINTS ALL IMMEDIATE; SET CONSTRAINTS ALL DEFERRED", ct).ConfigureAwait(false);
            await state.Transaction.ReleaseAsync(savepoint, ct).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (IsRowLevelFailure(exception))
        {
            await state.Transaction.RollbackAsync(savepoint, ct).ConfigureAwait(false);
            if (rows.Count == 1)
            {
                state.Failures[rows[0].Seq] = exception;
                if (state.Failures.Count + state.PriorRejections > state.MaxRejections) throw TooManyRejections(state.MaxRejections);
                await MarkFailureAsync(state, rows[0].Seq, exception, ct).ConfigureAwait(false);
                LogRowRejected(_logger, state.Table.Model.LogicalName, rows[0].Seq, exception.SqlState, exception.ConstraintName);
                return;
            }

            var middle = rows.Count / 2;
            await ApplyIsolatedAsync(state, rows.GetRange(0, middle), ct).ConfigureAwait(false);
            await ApplyIsolatedAsync(state, rows.GetRange(middle, rows.Count - middle), ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteApplyAsync(ApplyState state, List<ApplyRow> rows, CancellationToken ct)
    {
        if (!state.Table.Ordered)
        {
            var sql = new StringBuilder();
            if (rows.Any(r => r.Op == 2)) sql.Append(state.DeleteSet).Append(";\n");
            if (state.UpdateSet is not null && rows.Any(r => r.Op == 1 && r.Exists)) sql.Append(state.UpdateSet).Append(";\n");
            if (rows.Any(r => r.Op == 1 && !r.Exists)) sql.Append(state.InsertSet);
            if (sql.Length == 0) return;
            await ExecuteAsync(state.Connection, state.Transaction, sql.ToString(), ct,
                P("g9_seqs", NpgsqlDbType.Array | NpgsqlDbType.Bigint, rows.Select(r => r.Seq).ToArray())).ConfigureAwait(false);
            return;
        }

        foreach (var chunk in rows.Chunk(250))
        {
            var batch = new NpgsqlBatch(state.Connection, state.Transaction) { Timeout = _options.CommandTimeoutSeconds };
            await using (batch.ConfigureAwait(false))
            {
                foreach (var row in chunk)
                {
                    var sql = row.Op == 2 ? state.DeleteRow : row.Exists ? state.UpdateRow : state.InsertRow;
                    if (sql is null) continue;
                    var command = new NpgsqlBatchCommand(sql);
                    command.Parameters.Add(P("g9_seq", NpgsqlDbType.Bigint, row.Seq));
                    batch.BatchCommands.Add(command);
                }

                if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task MarkFailureAsync(ApplyState state, long seq, PostgresException exception, CancellationToken ct)
    {
        var code = exception.SqlState.StartsWith("P0", StringComparison.Ordinal) ? G9SyncErrorCodes.RejectValidation : G9SyncErrorCodes.RejectConstraint;
        await ExecuteAsync(state.Connection, state.Transaction,
            $"UPDATE {state.Stage} SET g9_action = 0, g9_code = @g9_code, g9_message = @g9_message, g9_sqlstate = @g9_state, " +
            "g9_constraint = @g9_constraint, g9_column = @g9_column, g9_details = @g9_details WHERE g9_seq = @g9_seq", ct,
            P("g9_code", NpgsqlDbType.Text, code), P("g9_message", NpgsqlDbType.Text, exception.MessageText),
            P("g9_state", NpgsqlDbType.Text, exception.SqlState), P("g9_constraint", NpgsqlDbType.Text, exception.ConstraintName),
            P("g9_column", NpgsqlDbType.Text, exception.ColumnName), P("g9_details", NpgsqlDbType.Text, exception.Detail),
            P("g9_seq", NpgsqlDbType.Bigint, seq)).ConfigureAwait(false);
    }

    private async Task RemarkFailuresAsync(ApplyState state, CancellationToken ct)
    {
        foreach (var (seq, exception) in state.Failures) await MarkFailureAsync(state, seq, exception, ct).ConfigureAwait(false);
    }

    private async Task<List<long>> FindWriteViolationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table, string stage,
        string authPredicate, List<(G9CResolvedSubscription Subscription, string Prefix)> bindings, CancellationToken ct)
    {
        var violations = new List<long>();
        await using var command = Command(connection, transaction,
            $"SELECT s.g9_seq FROM {stage} AS s JOIN {table.BaseName} AS t ON t.{table.Key.Quoted} = s.g9_key " +
            $"WHERE s.g9_action = 1 AND s.g9_op = 1 AND NOT coalesce({authPredicate}, false)");
        foreach (var (subscription, prefix) in bindings) BindSubscription(command.Parameters, subscription, prefix);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) violations.Add(reader.GetInt64(0));
        return violations;
    }

    private static bool IsRowLevelFailure(PostgresException exception)
    {
        var state = exception.SqlState;
        return state.StartsWith("22", StringComparison.Ordinal) // data exception
               || state.StartsWith("23", StringComparison.Ordinal) // integrity constraint violation
               || state.StartsWith("27", StringComparison.Ordinal) // triggered data change violation
               || state.StartsWith("2F", StringComparison.Ordinal) // SQL routine exception
               || state.StartsWith("39", StringComparison.Ordinal) // external routine invocation exception
               || state.StartsWith("P0", StringComparison.Ordinal) // PL/pgSQL raise
               || state is "42501" or "44000"; // insufficient privilege (row security), check option violation
    }

    // ================================================================ results

    private async Task CollectResultsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table, PushWork work, string stage,
        bool transformed, G9DtPushResult result, CancellationToken ct)
    {
        var key = table.Key.Quoted;
        var accepted = new G9DtAcceptedRows { TableId = table.TableId };
        var upserted = new List<string>();
        await using (var command = Command(connection, transaction,
                         $"SELECT s.g9_key::text, s.g9_op, coalesce(k.ver, 0) FROM {stage} AS s LEFT JOIN {table.TrackingName} AS k ON k.{key} = s.g9_key " +
                         "WHERE s.g9_action = 1 ORDER BY s.g9_seq"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var rowKey = reader.GetString(0);
                var deleted = reader.GetInt16(1) == 2;
                accepted.Keys.Add(rowKey);
                accepted.Deleted.Add(deleted);
                accepted.Versions.Add(reader.GetInt64(2));
                if (!deleted) upserted.Add(rowKey);
            }
        }

        if (accepted.Keys.Count > 0) result.Accepted.Add(accepted);

        await using (var command = Command(connection, transaction,
                         $"SELECT g9_key::text, g9_code, g9_message, g9_sqlstate, g9_constraint, g9_column, g9_details FROM {stage} " +
                         // Every excluded row with a code that is not a conflict is a rejection — including app codes from validators
                         // and resolvers. A code filter on 'G9S-REJ%' once dropped those rows from the result, leaving them dirty forever.
                         "WHERE g9_action = 0 AND g9_code IS NOT NULL AND g9_code NOT LIKE 'G9S-CON%' ORDER BY g9_seq"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Rejections.Add(new G9DtRowRejection
                {
                    TableId = table.TableId,
                    Key = reader.GetString(0),
                    Code = reader.GetString(1),
                    Message = reader.IsDBNull(2) ? null : reader.GetString(2),
                    SqlState = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Constraint = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Column = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Details = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
        }

        var canonicalKeys = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Command(connection, transaction,
                         $"SELECT g9_key::text, g9_code, coalesce(g9_conflict_mask, -1), g9_partial FROM {stage} WHERE g9_code LIKE 'G9S-CON%' ORDER BY g9_seq"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var rowKey = reader.GetString(0);
                result.Conflicts.Add(new G9DtRowConflict
                {
                    TableId = table.TableId, Key = rowKey, Code = reader.GetString(1), ServerChangedMask = reader.GetInt64(2), PartiallyApplied = reader.GetBoolean(3)
                });
                canonicalKeys.Add(rowKey);
            }
        }

        if (transformed) canonicalKeys.UnionWith(upserted);
        if (canonicalKeys.Count == 0) return;

        var frame = await BuildCanonicalFrameAsync(connection, transaction, table, work.Shape.Columns, canonicalKeys.ToArray(), ct).ConfigureAwait(false);
        if (frame is not null) result.CanonicalFrames.Add(frame);
    }

    private async Task<byte[]?> BuildCanonicalFrameAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table,
        IReadOnlyList<G9CSyncServerColumn> columns, string[] keys, CancellationToken ct)
    {
        var key = table.Key.Quoted;
        var types = columns.Select(c => c.Type).ToArray();
        var found = new HashSet<string>(StringComparer.Ordinal);
        using var writer = new G9CRowBatchWriter();
        await using (var command = Command(connection, transaction,
                         $"SELECT t.{key}, coalesce(k.ver, 0){string.Concat(columns.Select(c => ", " + ReadExpression(table, c, "t")))} " +
                         $"FROM {table.BaseName} AS t LEFT JOIN {table.TrackingName} AS k ON k.{key} = t.{key} WHERE t.{key} = ANY(@g9_keys)"))
        {
            command.Parameters.Add(KeyArrayParameter("g9_keys", table, keys));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!writer.InSection) writer.BeginSection(G9ERowBatchOp.Upsert, table.TableId, table.Model.KeyType, types);
                found.Add(BeginRow(writer, reader, table, reader.GetInt64(1)));
                for (var i = 0; i < types.Length; i++) WriteValue(writer, reader, 2 + i, types[i]);
                writer.EndRow();
            }
        }

        if (writer.InSection) writer.EndSection();

        var missing = keys.Where(k => !found.Contains(k)).ToArray();
        if (missing.Length > 0)
        {
            var tombstones = new List<(string Key, long Version)>();
            await using var command = Command(connection, transaction,
                $"SELECT {PgTypes.KeyText(table.Model.KeyType, "k." + key)}, k.ver FROM {table.TrackingName} AS k WHERE k.{key} = ANY(@g9_keys) AND k.deleted");
            command.Parameters.Add(KeyArrayParameter("g9_keys", table, missing));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) tombstones.Add((reader.GetString(0), reader.GetInt64(1)));
            await reader.DisposeAsync().ConfigureAwait(false);
            AppendKeySection(writer, G9ERowBatchOp.Tombstone, table, tombstones);
        }

        return writer.TotalRows == 0 ? null : writer.ToFrame();
    }

    private async Task<List<string>> ReadKeysAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    {
        var keys = new List<string>();
        await using var command = Command(connection, transaction, sql);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) keys.Add(reader.GetString(0));
        return keys;
    }

    private static G9CSyncException TooManyRejections(int max) =>
        new(G9SyncErrorCodes.RejectTooMany, $"More than {max} rows of this push were rejected; the whole push was rolled back (this signals a systematic problem, not bad rows).");

    [LoggerMessage(EventId = 22010, Level = LogLevel.Debug, Message = "Rejected upload row {Seq} of {Table}: {SqlState} {Constraint}")]
    private static partial void LogRowRejected(ILogger logger, string table, long seq, string sqlState, string? constraint);

    private sealed class PushWork
    {
        public required PgTable Table { get; init; }
        public required G9CPushTableShape Shape { get; init; }
        public List<(int Frame, int Section)> Sections { get; } = [];
    }

    private readonly record struct ApplyRow(long Seq, short Op, bool Exists);

    private sealed class ApplyState
    {
        public required NpgsqlConnection Connection { get; init; }
        public required NpgsqlTransaction Transaction { get; init; }
        public required PgTable Table { get; init; }
        public required string Stage { get; init; }
        public required string DeleteSet { get; init; }
        public required string DeleteRow { get; init; }
        public string? UpdateSet { get; init; }
        public string? UpdateRow { get; init; }
        public required string InsertSet { get; init; }
        public required string InsertRow { get; init; }
        public required int MaxRejections { get; init; }
        public required int PriorRejections { get; init; }
        public Dictionary<long, PostgresException> Failures { get; } = [];
        public int SavepointCounter { get; set; }
    }
}
