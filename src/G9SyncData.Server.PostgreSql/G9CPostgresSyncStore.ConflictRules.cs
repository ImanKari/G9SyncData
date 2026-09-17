using System.Diagnostics.Metrics;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
using G9SyncData.Server.PostgreSql.Internal;
using G9SyncData.Server.Storage;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     Declarative conflict rules (A-05). The staging table already knows, per uploaded row, whether the server changed it since the
///     device's base version (<c>g9_conflict_mask</c>, exact per column because tables with rules track column versions). Rules run
///     in C# over those facts, in order, first match wins; rows no rule matches get the table's policy. Decisions are then written back
///     to the staging table set-based, one statement per action.
/// </summary>
public sealed partial class G9CPostgresSyncStore
{
    private static readonly Counter<long> ConflictDecisions =
        Diagnostics.G9CSyncServerTelemetry.Meter.CreateCounter<long>("g9sync.server.conflict.decisions", "{row}",
            "Conflicting uploads by table, deciding rule (\"policy\" when none matched) and action");

    private async Task EvaluateConflictRulesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, G9CStorePushRequest request, PgTable table,
        PgBoundColumn[] columns, string stage, G9CConflictRules rules, CancellationToken ct)
    {
        var key = table.Key.Quoted;
        var names = columns.Select(c => c.Model.Name).ToArray();
        var cases = new List<(G9CConflictCase Case, bool Exists, long ConflictMask)>();

        var server = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        await using (var command = Command(connection, transaction,
                         $"SELECT {PgTypes.KeyText(table.Model.KeyType, "t." + key)}{string.Concat(columns.Select(c => ", " + ReadExpression(table, c.Model, "t")))} " +
                         $"FROM {table.BaseName} AS t WHERE t.{key} IN (SELECT g9_key FROM {stage} WHERE g9_conflict_mask IS NOT NULL AND g9_action = 1)"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Length; i++) values[names[i]] = reader.IsDBNull(1 + i) ? null : reader.GetValue(1 + i);
                server[reader.GetString(0)] = values;
            }
        }

        await using (var command = Command(connection, transaction,
                         $"SELECT g9_key::text, g9_op, g9_exists, g9_base_ver, coalesce(g9_server_ver, 0), g9_server_deleted, g9_dirty_mask, g9_conflict_mask, " +
                         $"g9_hlc, g9_server_changed_ms{string.Concat(columns.Select(c => ", " + c.Db!.Quoted))} " +
                         $"FROM {stage} WHERE g9_conflict_mask IS NOT NULL AND g9_action = 1 ORDER BY g9_seq"))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var rowKey = reader.GetString(0);
                var dirty = reader.GetInt64(6);
                var conflict = reader.GetInt64(7);
                var serverDeleted = reader.GetBoolean(5);
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < columns.Length; i++) values[names[i]] = reader.IsDBNull(10 + i) ? null : reader.GetValue(10 + i);
                var hlc = reader.GetInt64(8);

                cases.Add((new G9CConflictCase
                {
                    Table = table.Model,
                    Key = rowKey,
                    ClientDeletes = reader.GetInt16(1) == 2,
                    ServerDeleted = serverDeleted,
                    BaseVersion = reader.GetInt64(3),
                    ServerVersion = reader.GetInt64(4),
                    ClientChanged = ColumnsOf(dirty, names),
                    ServerChanged = conflict == -1 || serverDeleted ? null : ColumnsOf(conflict, names),
                    ClientEditedUtc = hlc > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(hlc).UtcDateTime : null,
                    ServerChangedUtc = reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)).UtcDateTime,
                    ClientValues = values,
                    ServerValues = server.GetValueOrDefault(rowKey),
                    ClientId = request.ClientId,
                    Auth = request.Auth,
                    Connection = connection,
                    Transaction = transaction,
                    Services = request.Services
                }, reader.GetBoolean(2), conflict));
            }
        }

        if (cases.Count == 0) return;

        var takeServer = new List<string>();
        var takeClient = new List<string>();
        var merge = new List<string>();
        var applyColumns = new Dictionary<long, List<string>>();
        var rejections = new List<G9CRowRejection>();
        foreach (var (conflictCase, exists, _) in cases)
        {
            var (rule, outcome) = await DecideAsync(rules, conflictCase, table.Model.ConflictPolicy, ct).ConfigureAwait(false);
            ConflictDecisions.Add(1, new KeyValuePair<string, object?>("table", table.Model.LogicalName), new KeyValuePair<string, object?>("rule", rule),
                new KeyValuePair<string, object?>("action", outcome.Action.ToString()));

            switch (outcome.Action)
            {
                case G9EConflictAction.TakeClient:
                    takeClient.Add(conflictCase.Key);
                    break;
                case G9EConflictAction.MergeColumns:
                    merge.Add(conflictCase.Key);
                    break;
                case G9EConflictAction.ApplyColumns when !conflictCase.ClientDeletes && !conflictCase.ServerDeleted && exists:
                    var mask = 0L;
                    for (var i = 0; i < names.Length; i++)
                    {
                        if (outcome.Columns.Contains(names[i], StringComparer.OrdinalIgnoreCase)) mask |= 1L << i;
                    }

                    if (!applyColumns.TryGetValue(mask, out var group)) applyColumns[mask] = group = [];
                    group.Add(conflictCase.Key);
                    break;
                case G9EConflictAction.Reject:
                    rejections.Add(new G9CRowRejection(conflictCase.Key, outcome.Code ?? G9SyncErrorCodes.ConflictResolverRejected, outcome.Message));
                    break;
                default:
                    // TakeServer, and ApplyColumns where no column can apply (a delete on either side).
                    takeServer.Add(conflictCase.Key);
                    break;
            }
        }

        if (rejections.Count > 0) await RejectRowsAsync(connection, transaction, stage, table, rejections, ct).ConfigureAwait(false);

        if (takeClient.Count > 0)
        {
            await ExecuteAsync(connection, transaction, $"UPDATE {stage} SET g9_conflict_mask = NULL WHERE g9_key::text = ANY(@g9_keys) AND g9_action = 1", ct,
                P("g9_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, takeClient.ToArray())).ConfigureAwait(false);
        }

        if (merge.Count > 0)
        {
            await ExecuteAsync(connection, transaction, $"""
                UPDATE {stage} SET g9_action = 0,
                    g9_code = CASE WHEN g9_server_deleted THEN '{G9SyncErrorCodes.ConflictDeleted}' ELSE '{G9SyncErrorCodes.ConflictServerWins}' END
                WHERE g9_key::text = ANY(@g9_keys) AND g9_action = 1 AND (g9_conflict_mask = -1 OR g9_op = 2 OR NOT g9_exists);
                UPDATE {stage}
                SET g9_apply_mask = g9_dirty_mask & ~g9_conflict_mask,
                    g9_partial = (g9_dirty_mask & g9_conflict_mask) <> 0 AND (g9_dirty_mask & ~g9_conflict_mask) <> 0,
                    g9_code = CASE WHEN (g9_dirty_mask & g9_conflict_mask) <> 0 THEN '{G9SyncErrorCodes.ConflictColumns}' ELSE NULL END
                WHERE g9_key::text = ANY(@g9_keys) AND g9_action = 1;
                UPDATE {stage} SET g9_action = 0, g9_code = coalesce(g9_code, '{G9SyncErrorCodes.ConflictColumns}')
                WHERE g9_key::text = ANY(@g9_keys) AND g9_action = 1 AND g9_apply_mask = 0
                """, ct, P("g9_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, merge.ToArray())).ConfigureAwait(false);
        }

        foreach (var (mask, keys) in applyColumns)
        {
            await ExecuteAsync(connection, transaction, $"""
                UPDATE {stage}
                SET g9_apply_mask = g9_dirty_mask & @g9_mask,
                    g9_partial = (g9_dirty_mask & @g9_mask) <> 0 AND (g9_dirty_mask & ~@g9_mask) <> 0,
                    g9_code = CASE WHEN (g9_dirty_mask & ~@g9_mask) <> 0 THEN '{G9SyncErrorCodes.ConflictColumns}' ELSE NULL END
                WHERE g9_key::text = ANY(@g9_keys) AND g9_action = 1;
                UPDATE {stage} SET g9_action = 0, g9_code = coalesce(g9_code, '{G9SyncErrorCodes.ConflictColumns}')
                WHERE g9_key::text = ANY(@g9_keys) AND g9_action = 1 AND g9_apply_mask = 0
                """, ct, P("g9_mask", NpgsqlDbType.Bigint, mask), P("g9_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, keys.ToArray())).ConfigureAwait(false);
        }

        if (takeServer.Count > 0)
        {
            await ExecuteAsync(connection, transaction,
                $"UPDATE {stage} SET g9_action = 0, g9_code = CASE WHEN g9_server_deleted THEN '{G9SyncErrorCodes.ConflictDeleted}' ELSE '{G9SyncErrorCodes.ConflictServerWins}' END " +
                "WHERE g9_key::text = ANY(@g9_keys) AND g9_action = 1", ct,
                P("g9_keys", NpgsqlDbType.Array | NpgsqlDbType.Text, takeServer.ToArray())).ConfigureAwait(false);
        }
    }

    /// <summary>The first matching rule's outcome, or the table's policy expressed as an outcome.</summary>
    private static async ValueTask<(string Rule, G9CConflictOutcome Outcome)> DecideAsync(G9CConflictRules rules, G9CConflictCase conflictCase,
        G9EConflictPolicy policy, CancellationToken ct)
    {
        foreach (var rule in rules.Rules)
        {
            if (await rule.Condition(conflictCase, ct).ConfigureAwait(false)) return (rule.Name, await rule.Decide(conflictCase, ct).ConfigureAwait(false));
        }

        return ("policy", policy switch
        {
            G9EConflictPolicy.ClientWins => G9CConflictOutcome.TakeClient,
            G9EConflictPolicy.ColumnMerge => G9CConflictOutcome.MergeColumns,
            G9EConflictPolicy.LastWriterWins => conflictCase.ClientIsNewer ? G9CConflictOutcome.TakeClient : G9CConflictOutcome.TakeServer,
            _ => G9CConflictOutcome.TakeServer
        });
    }

    private static HashSet<string> ColumnsOf(long mask, string[] names)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Length; i++)
        {
            if (mask == -1 || (mask & (1L << i)) != 0) set.Add(names[i]);
        }

        return set;
    }
}
