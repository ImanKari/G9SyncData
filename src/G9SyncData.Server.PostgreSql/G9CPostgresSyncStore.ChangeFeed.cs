using G9SyncData.Server.PostgreSql.Internal;
using G9SyncData.Server.Storage;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>The server-side change feed (plan D-15): the same snapshot visibility rule as a delta pull, without subscription scopes.</summary>
public sealed partial class G9CPostgresSyncStore
{
    /// <summary>
    ///     ONE query for every probe of a pull plan (TR-0069): a UNION of <c>EXISTS</c> probes, each the delta's own predicate on the
    ///     table's tracking rows (its <c>xid</c> index), deduplicated by table and snapshot - subscriptions synced together share their
    ///     cursor, so a plan of many subscriptions asks a few probes. A probe can only say MORE than the delta reads (no scope), never less.
    /// </summary>
    public async Task<IReadOnlyList<bool>> HasChangesAsync(IReadOnlyList<G9CStoreChangeProbe> probes, string newSnapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(newSnapshot);
        EnsureInitialized();
        var answers = new bool[probes.Count];
        if (probes.Count == 0) return answers;

        var distinct = new Dictionary<(int TableId, string Snapshot), int>();
        var slotOf = new int[probes.Count];
        var asked = new List<(PgTable Table, string Snapshot)>();
        for (var i = 0; i < probes.Count; i++)
        {
            var table = Get(probes[i].Table);
            var identity = (table.TableId, probes[i].PreviousSnapshot);
            if (!distinct.TryGetValue(identity, out var slot))
            {
                slot = asked.Count;
                distinct[identity] = slot;
                asked.Add((table, probes[i].PreviousSnapshot));
            }

            slotOf[i] = slot;
        }

        var sql = new System.Text.StringBuilder();
        for (var j = 0; j < asked.Count; j++)
        {
            if (j > 0) sql.Append(" UNION ALL ");
            sql.Append($"SELECT {j} WHERE EXISTS (SELECT 1 FROM {asked[j].Table.TrackingName} AS k ")
                .Append($"WHERE k.xid >= pg_snapshot_xmin(@g9_p{j}::pg_snapshot) ")
                .Append($"AND NOT pg_visible_in_snapshot(k.xid, @g9_p{j}::pg_snapshot) ")
                .Append("AND pg_visible_in_snapshot(k.xid, @g9_new::pg_snapshot))");
        }

        var changed = new bool[asked.Count];
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = Command(connection, null, sql.ToString());
            for (var j = 0; j < asked.Count; j++) command.Parameters.Add(P($"g9_p{j}", NpgsqlDbType.Text, asked[j].Snapshot));
            command.Parameters.Add(P("g9_new", NpgsqlDbType.Text, newSnapshot));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) changed[reader.GetInt32(0)] = true;
        }
        catch (Npgsql.PostgresException exception) when (exception.SqlState is Npgsql.PostgresErrorCodes.InvalidTextRepresentation or Npgsql.PostgresErrorCodes.InvalidParameterValue)
        {
            // An unreadable snapshot: plan everything, as before (the part's own read reports it).
            Array.Fill(changed, true);
        }

        for (var i = 0; i < probes.Count; i++) answers[i] = changed[slotOf[i]];
        return answers;
    }

    public async Task<IReadOnlyList<G9CStoreChange>> ReadChangesAsync(G9CStoreChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var table = Get(request.Table);
        var key = table.Key.Quoted;
        var sql = $"SELECT {PgTypes.KeyText(table.Model.KeyType, "k." + key)}, k.ver, k.deleted, k.last_mask, k.origin_client, k.changed_at " +
                  $"FROM {table.TrackingName} AS k " +
                  "WHERE k.xid >= pg_snapshot_xmin(@g9_prev::pg_snapshot) " +
                  "AND NOT pg_visible_in_snapshot(k.xid, @g9_prev::pg_snapshot) " +
                  "AND pg_visible_in_snapshot(k.xid, @g9_new::pg_snapshot)" +
                  (request.AfterKey is null ? string.Empty : $" AND k.{key} > @g9_after") +
                  $" ORDER BY k.{key} LIMIT @g9_limit";

        var changes = new List<G9CStoreChange>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null, sql);
        command.Parameters.Add(P("g9_prev", NpgsqlDbType.Text, request.PreviousSnapshot));
        command.Parameters.Add(P("g9_new", NpgsqlDbType.Text, request.NewSnapshot));
        if (request.AfterKey is not null) command.Parameters.Add(KeyParameter("g9_after", table, request.AfterKey));
        command.Parameters.Add(P("g9_limit", NpgsqlDbType.Integer, request.Limit));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(new G9CStoreChange(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetBoolean(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
        }

        return changes;
    }
}
