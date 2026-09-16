using G9SyncData.Server.PostgreSql.Internal;
using G9SyncData.Server.Storage;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>The server-side change feed (plan D-15): the same snapshot visibility rule as a delta pull, without subscription scopes.</summary>
public sealed partial class G9CPostgresSyncStore
{
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
