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
///     The upload apply. One transaction per push: idempotency lock → every table staged (COPY into temp staging tables, one
///     for its deletes and one for its upserts) and write-authorised against the state BEFORE the push → deletes, children
///     first (reverse model order) → upserts, parents first (model order) — each: validators → transformers → conflict policy
///     → delete handlers → apply with bisection isolation → results → post-write authorisation over every table at once.
///     Rejected rows are excluded, never fatal (D-10).
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

        // Claim the device for this principal on first use and verify it on every later one, in the statement that locks the row: the
        // receipt below is read by client id alone, and a client id is a value the caller supplies (ADR-0047).
        long lastBatch;
        byte[]? stored;
        string? owner;
        await using (var command = Command(connection, transaction,
                         $"INSERT INTO {_schema}.clients (client_id, user_id) VALUES (@g9_client, @g9_user) " +
                         $"ON CONFLICT (client_id) DO UPDATE SET user_id = coalesce(clients.user_id, EXCLUDED.user_id), last_seen = now() " +
                         "RETURNING user_id, last_push_batch, last_push_result"))
        {
            command.Parameters.Add(P("g9_client", NpgsqlDbType.Uuid, request.ClientId));
            command.Parameters.Add(P("g9_user", NpgsqlDbType.Text, request.Auth.UserId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            owner = reader.IsDBNull(0) ? null : reader.GetString(0);
            lastBatch = reader.GetInt64(1);
            stored = reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2);
        }

        if (!string.Equals(owner, request.Auth.UserId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new G9CSyncException(G9SyncErrorCodes.Unauthorized, "This device belongs to a different user.");
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

        // WAL mode: the guarded triggers track this transaction; tell the consumer to skip it (ADR-0028).
        if (IsWalMode) await EmitPushMarkerAsync(connection, transaction, request.ClientId, cancellationToken).ConfigureAwait(false);

        // Commands first, in device order (ADR-0025): the rows a device pushed after recording a command may depend on it.
        await ExecuteCommandsAsync(connection, transaction, request, result, cancellationToken).ConfigureAwait(false);

        // Post-write authorisation: every upserted row must be inside a writable scope right after its own table applied OR once
        // the whole push is applied (LES-0062). Its scope may run through a row of a LATER table - a new tree is in the farm only
        // through the placement planted with it - or a later row may take it away - a replanted tree loses its placement to the
        // new one; either alone must not refuse it, and the rows that moved are checked themselves. A row outside at both points
        // is rejected by applying the tables again with it excluded, so its dependants fail as usual.
        var forced = new Dictionary<int, HashSet<long>>();
        var baseline = (result.Accepted.Count, result.Rejections.Count, result.Conflicts.Count, result.CanonicalFrames.Count);
        for (var attempt = 0; ; attempt++)
        {
            var savepoint = "g9_push_" + attempt.ToString(CultureInfo.InvariantCulture);
            await transaction.SaveAsync(savepoint, cancellationToken).ConfigureAwait(false);
            // Deletes go child-first and before every upsert, as Dotmim applied them (LES-0063): a tree deleted with its placement
            // otherwise reached RESTRICT on FK_Placement_Tree_TreeId while the placement still stood, and a placement replaced in a
            // pot left the partial unique index holding two current rows for a moment. Every table is staged and authorised
            // first, so a delete is judged by the scope the row had BEFORE the push, not after its children left.
            var staged = new List<StagedTable>();
            foreach (var tableWork in work.OrderBy(w => w.Table.Model.Order))
            {
                foreach (var deletes in (bool[])[true, false])
                {
                    var table = await StageTableAsync(connection, transaction, request, tableWork, deletes, forced, cancellationToken).ConfigureAwait(false);
                    if (table is not null) staged.Add(table);
                }
            }

            var checks = new List<WriteCheck>();
            foreach (var table in staged.Where(t => t.Deletes).Reverse().Concat(staged.Where(t => !t.Deletes)))
            {
                await ApplyStagedAsync(connection, transaction, request, table, result, checks, cancellationToken).ConfigureAwait(false);
            }

            var violated = false;
            foreach (var check in checks)
            {
                var violations = await FindWriteViolationsAsync(connection, transaction, check, cancellationToken).ConfigureAwait(false);
                if (violations.Count == 0) continue;
                violated = true;
                if (!forced.TryGetValue(check.Table.TableId, out var seqs)) forced[check.Table.TableId] = seqs = [];
                seqs.UnionWith(violations);
            }

            if (!violated)
            {
                await transaction.ReleaseAsync(savepoint, cancellationToken).ConfigureAwait(false);
                break;
            }

            if (attempt >= 3) throw new G9CSyncException(G9SyncErrorCodes.ServerError, "Write authorisation did not converge for this push.");
            await transaction.RollbackAsync(savepoint, cancellationToken).ConfigureAwait(false);
            result.Accepted.RemoveRange(baseline.Item1, result.Accepted.Count - baseline.Item1);
            result.Rejections.RemoveRange(baseline.Item2, result.Rejections.Count - baseline.Item2);
            result.Conflicts.RemoveRange(baseline.Item3, result.Conflicts.Count - baseline.Item3);
            result.CanonicalFrames.RemoveRange(baseline.Item4, result.CanonicalFrames.Count - baseline.Item4);
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
        var decoded = 0L;
        for (var frameIndex = 0; frameIndex < request.Frames.Count; frameIndex++)
        {
            try
            {
                using var reader = G9CRowBatchReader.Open(request.Frames[frameIndex], request.MaxDecodedFrameBytes);
                decoded += reader.BodyBytes;
                if (decoded > request.MaxDecodedPushBytes)
                {
                    throw new G9CSyncException(G9SyncErrorCodes.RejectTooMany,
                        $"The push decodes to more than {request.MaxDecodedPushBytes} bytes; send smaller batches.");
                }

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

    /// <summary>One table's deletes or upserts, staged and write-authorised, waiting for its turn to apply.</summary>
    private sealed record StagedTable(PushWork Work, PgBoundColumn[] Columns, string Stage, bool Deletes, string? AuthPredicate,
        List<(G9CResolvedSubscription Subscription, string Prefix, PgScope Scope)> AuthBindings);

    /// <summary>Steps 1-2 for the deletes (or the upserts) of one table; null when the push carries none.</summary>
    private async Task<StagedTable?> StageTableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, G9CStorePushRequest request,
        PushWork work, bool deletes, Dictionary<int, HashSet<long>> forced, CancellationToken ct)
    {
        var table = work.Table;
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
        if (await CopyPushRowsAsync(connection, request, work, stage, columns, deletes, ct).ConfigureAwait(false) == 0)
        {
            await ExecuteAsync(connection, transaction, "DROP TABLE " + stage, ct).ConfigureAwait(false);
            return null;
        }

        await ExecuteAsync(connection, transaction,
            $"CREATE INDEX ON {stage} (g9_key); CREATE UNIQUE INDEX ON {stage} (g9_seq); ANALYZE {stage}; " +
            $"UPDATE {stage} AS s SET g9_exists = true FROM {table.BaseName} AS t WHERE t.{key} = s.g9_key", ct).ConfigureAwait(false);

        // 2. write authorisation for rows that already exist (update / delete must start inside a scope)
        var authBindings = new List<(G9CResolvedSubscription Subscription, string Prefix, PgScope Scope)>();
        var authPredicate = await BuildAuthPredicateAsync(table, request.Subscriptions, "t", authBindings, ct).ConfigureAwait(false);
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
            foreach (var (subscription, prefix, scope) in authBindings)
            {
                BindSubscription(command.Parameters, subscription, prefix);
                BindScopeParameters(command.Parameters, scope, prefix);
            }
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // rows an earlier attempt of this push found outside every writable subscription once all tables were applied
        if (forced.TryGetValue(table.TableId, out var outside))
        {
            await ExecuteAsync(connection, transaction,
                $"UPDATE {stage} SET g9_action = 0, g9_code = @g9_code, g9_message = @g9_message WHERE g9_action = 1 AND g9_seq = ANY(@g9_seqs)", ct,
                P("g9_code", NpgsqlDbType.Text, G9SyncErrorCodes.RejectPermission),
                P("g9_message", NpgsqlDbType.Text, "After the push the row would be outside every subscription the caller may write to."),
                P("g9_seqs", NpgsqlDbType.Array | NpgsqlDbType.Bigint, outside.ToArray())).ConfigureAwait(false);
        }

        return new StagedTable(work, columns, stage, deletes, authPredicate, authBindings);
    }

    /// <summary>Steps 3-7 for one staged set, in its turn.</summary>
    private async Task ApplyStagedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, G9CStorePushRequest request, StagedTable staged,
        G9DtPushResult result, List<WriteCheck> checks, CancellationToken ct)
    {
        var work = staged.Work;
        var table = work.Table;
        var model = table.Model;
        var columns = staged.Columns;
        var key = table.Key.Quoted;
        var stage = staged.Stage;

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
        // WAL mode: changes by other writers reach tracking asynchronously, so lock the rows and record their current state
        // before reading tracking - otherwise a just-committed server edit could be overwritten unseen (ADR-0028). This does
        // not wait for the consumer, so push availability never depends on it.
        if (IsWalMode) await LockAndRecordCurrentStateAsync(connection, transaction, table, stage, ct).ConfigureAwait(false);

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

        // 6. apply with isolation (post-write authorisation follows once every table is applied)
        var rows = new List<ApplyRow>();
        await using (var command = Command(connection, transaction, $"SELECT g9_seq, g9_op, g9_exists FROM {stage} WHERE g9_action = 1 ORDER BY g9_seq"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(new ApplyRow(reader.GetInt64(0), reader.GetInt16(1), reader.GetBoolean(2)));
        }

        var state = BuildApplyState(connection, transaction, table, columns, stage, request.MaxRejections, result.Rejections.Count);

        // A row writer (ADR-0025) replaces only the INSERT/UPDATE/DELETE. What the application writes may differ from what was
        // uploaded (audit stamps, resolved codes), so every written row goes back to the device as a canonical row.
        foreach (var candidate in request.Pipeline.RowWriters) await candidate.PrepareAsync(request.Services, ct).ConfigureAwait(false);
        state.Writer = request.Pipeline.RowWriters.FirstOrDefault(w => w.AppliesTo(model));
        if (state.Writer is not null)
        {
            transformed = true;
            state.WriteContext = new G9CRowWriteContext
            {
                Table = model, ClientId = request.ClientId, BatchId = request.BatchId, Caller = request.Caller, Auth = request.Auth,
                Connection = connection, Transaction = transaction, Services = request.Services, Ordered = table.Ordered
            };
        }

        await ApplyIsolatedAsync(state, rows, ct).ConfigureAwait(false);
        if (staged.AuthPredicate is not null && !staged.Deletes)
        {
            var check = new WriteCheck(table, stage, staged.AuthPredicate, staged.AuthBindings, null);
            var outside = await FindWriteViolationsAsync(connection, transaction, check, ct).ConfigureAwait(false);
            if (outside.Count > 0) checks.Add(check with { Candidates = outside.ToArray() });   // judged again at the end
        }

        // 7. results
        await CollectResultsAsync(connection, transaction, request, table, work, stage, transformed, result, ct).ConfigureAwait(false);
        if (result.Rejections.Count > request.MaxRejections) throw TooManyRejections(request.MaxRejections);
    }

    private async ValueTask<string?> BuildAuthPredicateAsync(PgTable table, IReadOnlyList<G9CResolvedSubscription> subscriptions, string alias,
        List<(G9CResolvedSubscription Subscription, string Prefix, PgScope Scope)> bindings, CancellationToken ct)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < subscriptions.Count; i++)
        {
            var subscription = subscriptions[i];
            var include = subscription.Definition.FindTable(table.Model);
            if (include is null || !include.AllowUpload || !seen.Add(subscription.Key)) continue;
            var prefix = "a" + i.ToString(CultureInfo.InvariantCulture) + "_";
            var scope = await ResolveScopeAsync(include, subscription, ct).ConfigureAwait(false);
            parts.Add("(" + BindScope(scope, alias, subscription.Definition, prefix) + ")");
            bindings.Add((subscription, prefix, scope));
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

    private static async Task<ulong> CopyPushRowsAsync(NpgsqlConnection connection, G9CStorePushRequest request, PushWork work, string stage,
        PgBoundColumn[] columns, bool deletes, CancellationToken ct)
    {
        var names = string.Concat(columns.Select(c => ", " + c.Db!.Quoted));
        var allBits = columns.Length >= 63 ? -1L : (1L << columns.Length) - 1;
        var importer = await connection.BeginBinaryImportAsync($"COPY {stage} ({StageMeta}{names}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false);
        await using (importer.ConfigureAwait(false))
        {
            foreach (var (frameIndex, sectionIndex) in work.Sections)
            {
                using var reader = G9CRowBatchReader.Open(request.Frames[frameIndex], request.MaxDecodedFrameBytes);
                for (var i = 0; i <= sectionIndex; i++) reader.NextSection();
                while (reader.ReadRow())
                {
                    if ((reader.PushOp == G9EPushRowOp.Delete) != deletes) continue;
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

            return await importer.CompleteAsync(ct).ConfigureAwait(false);
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
        if (table.Model.ConflictRules is { Rules.Count: > 0 } rules)
        {
            await EvaluateConflictRulesAsync(connection, transaction, request, table, columns, stage, rules, ct).ConfigureAwait(false);
            return;
        }

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
            PriorRejections = priorRejections,
            Columns = columns
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
        catch (Exception exception) when (RowFailure.TryFrom(exception, out var failure))
        {
            await state.Transaction.RollbackAsync(savepoint, ct).ConfigureAwait(false);
            if (rows.Count == 1)
            {
                state.Failures[rows[0].Seq] = failure;
                if (state.Failures.Count + state.PriorRejections > state.MaxRejections) throw TooManyRejections(state.MaxRejections);
                await MarkFailureAsync(state, rows[0].Seq, failure, ct).ConfigureAwait(false);
                LogRowRejected(_logger, state.Table.Model.LogicalName, rows[0].Seq, failure.SqlState ?? failure.Code, failure.Constraint);
                return;
            }

            var middle = rows.Count / 2;
            await ApplyIsolatedAsync(state, rows.GetRange(0, middle), ct).ConfigureAwait(false);
            await ApplyIsolatedAsync(state, rows.GetRange(middle, rows.Count - middle), ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteApplyAsync(ApplyState state, List<ApplyRow> rows, CancellationToken ct)
    {
        if (state.Writer is not null)
        {
            await WriteThroughWriterAsync(state, rows, ct).ConfigureAwait(false);
            return;
        }

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

    /// <summary>Reads the batch's staged rows and hands them to the row writer, with only the columns the policy decided to apply.</summary>
    private async Task WriteThroughWriterAsync(ApplyState state, List<ApplyRow> rows, CancellationToken ct)
    {
        var table = state.Table;
        var writes = new List<G9CRowWrite>(rows.Count);
        await using (var command = Command(state.Connection, state.Transaction,
                         $"SELECT g9_seq, g9_op, g9_exists, g9_key, g9_key::text, coalesce(g9_apply_mask, g9_dirty_mask)" +
                         $"{string.Concat(state.Columns.Select(c => ", " + c.Db!.Quoted))} FROM {state.Stage} WHERE g9_seq = ANY(@g9_seqs) ORDER BY g9_seq"))
        {
            command.Parameters.Add(P("g9_seqs", NpgsqlDbType.Array | NpgsqlDbType.Bigint, rows.Select(r => r.Seq).ToArray()));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var op = reader.GetInt16(1) == 2 ? G9ERowWriteOp.Delete : G9ERowWriteOp.Upsert;
                var exists = reader.GetBoolean(2);
                var mask = reader.GetInt64(5);
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (op == G9ERowWriteOp.Upsert)
                {
                    for (var i = 0; i < state.Columns.Length; i++)
                    {
                        if (exists && (mask & (1L << i)) == 0) continue;
                        values[state.Columns[i].Model.Name] = reader.IsDBNull(6 + i) ? null : reader.GetValue(6 + i);
                    }
                }

                writes.Add(new G9CRowWrite
                {
                    Seq = reader.GetInt64(0),
                    Op = op,
                    Exists = exists,
                    KeyValue = reader.GetValue(3),
                    Key = table.Model.KeyType == G9ESyncColumnType.Guid ? G9CSyncKey.FromGuid(reader.GetGuid(3)) : reader.GetString(4),
                    Values = values
                });
            }
        }

        await state.Writer!.WriteAsync(state.WriteContext!, writes, ct).ConfigureAwait(false);
    }

    private async Task MarkFailureAsync(ApplyState state, long seq, RowFailure failure, CancellationToken ct)
    {
        await ExecuteAsync(state.Connection, state.Transaction,
            $"UPDATE {state.Stage} SET g9_action = 0, g9_code = @g9_code, g9_message = @g9_message, g9_sqlstate = @g9_state, " +
            "g9_constraint = @g9_constraint, g9_column = @g9_column, g9_details = @g9_details WHERE g9_seq = @g9_seq", ct,
            P("g9_code", NpgsqlDbType.Text, failure.Code), P("g9_message", NpgsqlDbType.Text, failure.Message),
            P("g9_state", NpgsqlDbType.Text, failure.SqlState), P("g9_constraint", NpgsqlDbType.Text, failure.Constraint),
            P("g9_column", NpgsqlDbType.Text, failure.Column), P("g9_details", NpgsqlDbType.Text, failure.Details),
            P("g9_seq", NpgsqlDbType.Bigint, seq)).ConfigureAwait(false);
    }

    /// <summary>A failure attributable to the rows being applied: a database row-level error, or an application rejection.</summary>
    private sealed record RowFailure(string Code, string? Message, string? SqlState, string? Constraint, string? Column, string? Details)
    {
        /// <summary>
        ///     Recognises row-level failures anywhere in the exception chain: a writer's ORM wraps the database error (EF Core
        ///     throws DbUpdateException around the PostgresException), and application code may throw <see cref="G9CRowRejectedException" />.
        /// </summary>
        public static bool TryFrom(Exception exception, out RowFailure failure)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                switch (current)
                {
                    case G9CRowRejectedException rejected:
                        failure = new RowFailure(rejected.Code, rejected.Message, null, null, null, rejected.Details);
                        return true;
                    case PostgresException postgres when IsRowLevelFailure(postgres):
                        failure = new RowFailure(
                            postgres.SqlState.StartsWith("P0", StringComparison.Ordinal) ? G9SyncErrorCodes.RejectValidation : G9SyncErrorCodes.RejectConstraint,
                            postgres.MessageText, postgres.SqlState, postgres.ConstraintName, postgres.ColumnName, postgres.Detail);
                        return true;
                }
            }

            failure = null!;
            return false;
        }
    }

    /// <summary>The post-write check of one applied table; <see cref="Candidates" /> limits it to the rows outside after their step.</summary>
    private sealed record WriteCheck(PgTable Table, string Stage, string AuthPredicate,
        List<(G9CResolvedSubscription Subscription, string Prefix, PgScope Scope)> Bindings, long[]? Candidates);

    private async Task<List<long>> FindWriteViolationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, WriteCheck check, CancellationToken ct)
    {
        var violations = new List<long>();
        await using var command = Command(connection, transaction,
            $"SELECT s.g9_seq FROM {check.Stage} AS s JOIN {check.Table.BaseName} AS t ON t.{check.Table.Key.Quoted} = s.g9_key " +
            $"WHERE s.g9_action = 1 AND s.g9_op = 1 AND NOT coalesce({check.AuthPredicate}, false)" +
            (check.Candidates is null ? "" : " AND s.g9_seq = ANY(@g9_candidates)"));
        if (check.Candidates is not null) command.Parameters.Add(P("g9_candidates", NpgsqlDbType.Array | NpgsqlDbType.Bigint, check.Candidates));
        foreach (var (subscription, prefix, scope) in check.Bindings)
        {
            BindSubscription(command.Parameters, subscription, prefix);
            BindScopeParameters(command.Parameters, scope, prefix);
        }

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

    private async Task CollectResultsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, G9CStorePushRequest request, PgTable table, PushWork work, string stage,
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

        // In this transaction, not after it: an effect that exists describes rows that committed, and rows that committed owe an effect
        // (ADR-0052). A crash between here and delivery loses nothing, and a replayed push does not need to run anything again.
        if (accepted.Keys.Count > 0 && request.Pipeline.EffectHandlers.Any(h => h.AppliesTo(table.Model)))
        {
            var deletedKeys = new List<string>();
            for (var i = 0; i < accepted.Keys.Count; i++)
            {
                if (i < accepted.Deleted.Count && accepted.Deleted[i]) deletedKeys.Add(accepted.Keys[i]);
            }

            await ExecuteAsync(connection, transaction,
                $"INSERT INTO {_schema}.effects (effect_id, table_id, client_id, batch_id, user_id, upserted, deleted) " +
                "VALUES (@g9_effect, @g9_table, @g9_client, @g9_batch, @g9_user, @g9_upserted, @g9_deleted)", ct,
                P("g9_effect", NpgsqlDbType.Uuid, Guid.NewGuid()), P("g9_table", NpgsqlDbType.Integer, table.TableId),
                P("g9_client", NpgsqlDbType.Uuid, request.ClientId), P("g9_batch", NpgsqlDbType.Bigint, request.BatchId),
                P("g9_user", NpgsqlDbType.Text, request.Auth.UserId),
                P("g9_upserted", NpgsqlDbType.Array | NpgsqlDbType.Text, upserted.ToArray()),
                P("g9_deleted", NpgsqlDbType.Array | NpgsqlDbType.Text, deletedKeys.ToArray())).ConfigureAwait(false);
        }

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
        if (canonicalKeys.Count == 0 || table.Model.Direction == G9ESyncDirection.UploadOnly) return;   // upload-only rows never go back

        // The device applies a canonical row in the columns it HOLDS, which include read-only and computed columns the push shape
        // leaves out (LES-0035). A client too old to say which it holds gets the push shape only when the two cannot differ.
        IReadOnlyList<G9CSyncServerColumn> shape;
        if (request.HeldTables.TryGetValue(table.TableId, out var held)) shape = held.Columns;
        else if (!table.Model.Columns.Any(c => c.IsReadOnly || c.IsComputed)) shape = work.Shape.Columns;
        else return;   // the device keeps its values until the next pull delivers the row

        var frame = await BuildCanonicalFrameAsync(connection, transaction, table, shape, canonicalKeys.ToArray(), request.FrameVersion, ct).ConfigureAwait(false);
        if (frame is not null) result.CanonicalFrames.Add(frame);
    }

    private async Task<byte[]?> BuildCanonicalFrameAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table,
        IReadOnlyList<G9CSyncServerColumn> columns, string[] keys, int frameVersion, CancellationToken ct)
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

        return writer.TotalRows == 0 ? null : writer.ToFrame(version: frameVersion);
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
        public Dictionary<long, RowFailure> Failures { get; } = [];
        public int SavepointCounter { get; set; }
        public required PgBoundColumn[] Columns { get; init; }
        public IG9RowWriter? Writer { get; set; }
        public G9CRowWriteContext? WriteContext { get; set; }
    }
}
