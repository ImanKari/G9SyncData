using System.Globalization;
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
///     Commands (ADR-0025 command path), executed inside the push transaction before the pushed rows.
///     <list type="bullet">
///         <item><b>Exactly once per command id.</b> <c>g9sync.commands</c> stores each outcome with the push; a command id seen again
///         (a retried batch, or a device that lost the result and resends in a new batch) returns the stored outcome without running.</item>
///         <item><b>Isolated.</b> Each command runs under its own savepoint. A rejection — returned, thrown as
///         <see cref="G9CRowRejectedException" />, or a row-level database error — rolls the savepoint back; later commands and the
///         pushed rows still apply.</item>
///         <item><b>Settled.</b> The device applied the command locally without marking rows dirty. The result carries canonical
///         rows for every row the device touched and every row the handler changed — or a tombstone when the row does not exist or
///         is not visible to the device's subscriptions — so the device converges to server state whatever the outcome.</item>
///     </list>
/// </summary>
public sealed partial class G9CPostgresSyncStore
{
    private async Task ExecuteCommandsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, G9CStorePushRequest request,
        G9DtPushResult result, CancellationToken ct)
    {
        if (request.Commands.Count == 0) return;

        var stored = new Dictionary<Guid, G9DtCommandResult>();
        await using (var command = Command(connection, transaction,
                         $"SELECT command_id, status, code, message, result FROM {_schema}.commands WHERE client_id = @g9_client AND command_id = ANY(@g9_ids)"))
        {
            command.Parameters.Add(P("g9_client", NpgsqlDbType.Uuid, request.ClientId));
            command.Parameters.Add(P("g9_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, request.Commands.Select(c => c.CommandId).ToArray()));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                stored[reader.GetGuid(0)] = new G9DtCommandResult
                {
                    CommandId = reader.GetGuid(0),
                    Status = (G9ECommandStatus)reader.GetInt16(1),
                    Code = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Message = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Result = reader.IsDBNull(4) ? null : reader.GetString(4)
                };
            }
        }

        var touched = new Dictionary<int, HashSet<string>>();
        var executed = 0;
        foreach (var command in request.Commands)
        {
            foreach (var row in command.Touched)
            {
                if (!touched.TryGetValue(row.TableId, out var keys)) touched[row.TableId] = keys = new HashSet<string>(StringComparer.Ordinal);
                keys.Add(row.Key);
            }

            if (stored.TryGetValue(command.CommandId, out var previous))
            {
                result.CommandResults.Add(previous);
                continue;
            }

            var outcome = await ExecuteCommandAsync(connection, transaction, request, command, executed++, ct).ConfigureAwait(false);
            var dto = new G9DtCommandResult
            {
                CommandId = command.CommandId, Status = outcome.Status, Code = outcome.Code, Message = outcome.Message, Result = outcome.Result
            };
            await ExecuteAsync(connection, transaction,
                $"INSERT INTO {_schema}.commands (client_id, command_id, sequence, name, version, status, code, message, result) " +
                "VALUES (@g9_client, @g9_id, @g9_seq, @g9_name, @g9_version, @g9_status, @g9_code, @g9_message, @g9_result)", ct,
                P("g9_client", NpgsqlDbType.Uuid, request.ClientId), P("g9_id", NpgsqlDbType.Uuid, command.CommandId),
                P("g9_seq", NpgsqlDbType.Bigint, command.Sequence), P("g9_name", NpgsqlDbType.Text, command.Name),
                P("g9_version", NpgsqlDbType.Integer, command.Version), P("g9_status", NpgsqlDbType.Smallint, (short)outcome.Status),
                P("g9_code", NpgsqlDbType.Text, outcome.Code), P("g9_message", NpgsqlDbType.Text, outcome.Message),
                P("g9_result", NpgsqlDbType.Text, outcome.Result)).ConfigureAwait(false);
            result.CommandResults.Add(dto);
            LogCommandExecuted(_logger, command.Name, command.Version, command.CommandId, outcome.Status, outcome.Code);
        }

        // Rows the handlers changed carry this transaction's id in tracking - and nothing else does yet, because the pushed rows
        // are applied after the commands.
        var changed = new Dictionary<int, List<string>>();
        if (executed > 0)
        {
            foreach (var table in _tables.Values.Where(t => request.HeldTables.ContainsKey(t.TableId)))
            {
                var keys = await ReadKeysAsync(connection, transaction,
                    $"SELECT {PgTypes.KeyText(table.Model.KeyType, "k." + table.Key.Quoted)} FROM {table.TrackingName} AS k WHERE k.xid = pg_current_xact_id()", ct)
                    .ConfigureAwait(false);
                if (keys.Count > 0) changed[table.TableId] = keys;
            }
        }

        foreach (var (tableId, shape) in request.HeldTables)
        {
            if (_byId.TryGetValue(tableId, out var held) && held.Model.Direction == G9ESyncDirection.UploadOnly) continue;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (touched.TryGetValue(tableId, out var touchedKeys)) keys.UnionWith(touchedKeys);
            if (changed.TryGetValue(tableId, out var changedKeys)) keys.UnionWith(changedKeys);
            if (keys.Count == 0) continue;

            var table = Get(shape.Table);
            var normalized = keys.Select(k => G9CSyncKey.TryNormalize(table.Model.KeyType, k, out var canonical) ? canonical : null).OfType<string>().ToArray();
            var frame = await BuildVisibleCanonicalFrameAsync(connection, transaction, table, shape.Columns, normalized, request.Subscriptions, request.FrameVersion, ct).ConfigureAwait(false);
            if (frame is not null) result.CanonicalFrames.Add(frame);
        }
    }

    private async Task<G9CCommandResult> ExecuteCommandAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, G9CStorePushRequest request,
        G9DtCommand command, int index, CancellationToken ct)
    {
        var handler = request.Pipeline.CommandHandlers.FirstOrDefault(h => string.Equals(h.Name, command.Name, StringComparison.Ordinal) && h.Supports(command.Version));
        if (handler is null)
        {
            return G9CCommandResult.Rejected(G9SyncErrorCodes.CommandUnknown, $"No handler is registered for command {command.Name} v{command.Version}.");
        }

        var context = new G9CCommandContext
        {
            CommandId = command.CommandId,
            Name = command.Name,
            Version = command.Version,
            Sequence = command.Sequence,
            CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(command.CreatedUtcMs).UtcDateTime,
            ClientId = request.ClientId,
            BatchId = request.BatchId,
            Caller = request.Caller,
            Auth = request.Auth,
            Connection = connection,
            Transaction = transaction,
            Services = request.Services
        };

        var savepoint = "g9_cmd_" + index.ToString(CultureInfo.InvariantCulture);
        await transaction.SaveAsync(savepoint, ct).ConfigureAwait(false);
        G9CCommandResult outcome;
        try
        {
            outcome = await handler.ExecuteAsync(context, command.Payload, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (RowFailure.TryFrom(exception, out var failure))
        {
            outcome = G9CCommandResult.Rejected(failure.Code,
                failure.SqlState is null ? failure.Message : $"{failure.Message} (SQLSTATE {failure.SqlState}{(failure.Constraint is null ? string.Empty : ", " + failure.Constraint)})");
        }

        if (outcome.Status == G9ECommandStatus.Ok) await transaction.ReleaseAsync(savepoint, ct).ConfigureAwait(false);
        else await transaction.RollbackAsync(savepoint, ct).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    ///     Canonical rows for <paramref name="keys" /> as the device may see them: an upsert when the row exists and is visible to one of
    ///     its subscriptions, otherwise a tombstone (the device must not keep a row it cannot see, nor one that does not exist).
    /// </summary>
    private async Task<byte[]?> BuildVisibleCanonicalFrameAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, PgTable table,
        IReadOnlyList<G9CSyncServerColumn> columns, string[] keys, IReadOnlyList<G9CResolvedSubscription> subscriptions, int frameVersion, CancellationToken ct)
    {
        if (keys.Length == 0) return null;
        var key = table.Key.Quoted;
        var parts = new List<string>();
        var bindings = new List<(G9CResolvedSubscription Subscription, string Prefix, PgScope Scope)>();
        for (var i = 0; i < subscriptions.Count; i++)
        {
            var subscription = subscriptions[i];
            var include = subscription.Definition.FindTable(table.Model);
            if (include is null) continue;
            var prefix = "v" + i.ToString(CultureInfo.InvariantCulture) + "_";
            var scope = await ResolveScopeAsync(include, subscription, ct).ConfigureAwait(false);
            var when = include.WhenSql is null ? "true" : Bind(include.WhenSql, "t", subscription.Definition, prefix);
            parts.Add($"(coalesce(({BindScope(scope, "t", subscription.Definition, prefix)}), false) AND coalesce(({when}), false))");
            bindings.Add((subscription, prefix, scope));
        }

        var visible = parts.Count == 0 ? "false" : string.Join(" OR ", parts);
        var types = columns.Select(c => c.Type).ToArray();
        var sent = new HashSet<string>(StringComparer.Ordinal);
        var tombstones = new List<(string Key, long Version)>();
        using var writer = new G9CRowBatchWriter();
        await using (var command = Command(connection, transaction,
                         $"SELECT t.{key}, coalesce(k.ver, 0), ({visible}){string.Concat(columns.Select(c => ", " + ReadExpression(table, c, "t")))} " +
                         $"FROM {table.BaseName} AS t LEFT JOIN {table.TrackingName} AS k ON k.{key} = t.{key} WHERE t.{key} = ANY(@g9_keys)"))
        {
            foreach (var (subscription, prefix, scope) in bindings)
            {
                BindSubscription(command.Parameters, subscription, prefix);
                BindScopeParameters(command.Parameters, scope, prefix);
            }

            command.Parameters.Add(KeyArrayParameter("g9_keys", table, keys));
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var version = reader.GetInt64(1);
                if (!reader.GetBoolean(2))
                {
                    var hidden = ReadKey(reader, table);
                    sent.Add(hidden);
                    tombstones.Add((hidden, version));
                    continue;
                }

                if (!writer.InSection) writer.BeginSection(G9ERowBatchOp.Upsert, table.TableId, table.Model.KeyType, types);
                sent.Add(BeginRow(writer, reader, table, version));
                for (var i = 0; i < types.Length; i++) WriteValue(writer, reader, 3 + i, types[i]);
                writer.EndRow();
            }
        }

        if (writer.InSection) writer.EndSection();
        tombstones.AddRange(keys.Where(k => !sent.Contains(k)).Select(k => (k, 0L)));
        AppendKeySection(writer, G9ERowBatchOp.Tombstone, table, tombstones);
        return writer.TotalRows == 0 ? null : writer.ToFrame(version: frameVersion);
    }

    [LoggerMessage(EventId = 22020, Level = LogLevel.Debug, Message = "Command {Name} v{Version} ({CommandId}) executed: {Status} {Code}")]
    private static partial void LogCommandExecuted(ILogger logger, string name, int version, Guid commandId, G9ECommandStatus status, string? code);
}
