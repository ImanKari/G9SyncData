using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using G9SyncData.Server.PostgreSql.Internal;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

public sealed partial class G9CPostgresSyncStore
{
    public async IAsyncEnumerable<IReadOnlyList<int>> ListenForChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureInitialized();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        connection.Notification += (_, e) => channel.Writer.TryWrite(e.Payload);
        await ExecuteAsync(connection, null, "LISTEN " + PgNames.Quote(_options.NotificationChannel), cancellationToken).ConfigureAwait(false);

        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = PumpNotificationsAsync(connection, channel.Writer, pumpCancellation.Token);
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Coalesce bursts: a bulk write notifies once per statement.
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                var ids = new HashSet<int>();
                while (channel.Reader.TryRead(out var name))
                {
                    if (_byLogical.TryGetValue(name, out var table)) ids.Add(table.TableId);
                }

                if (ids.Count > 0) yield return ids.ToArray();
            }
        }
        finally
        {
            await pumpCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }
    }

    private static async Task PumpNotificationsAsync(NpgsqlConnection connection, ChannelWriter<string> writer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested) await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    public async Task<int> CleanupTombstonesAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        // Maintenance is the natural home of the key-set sweep too (ADR-0030); builders sweep as well, so this is not the only one.
        await SweepKeySetsIfDueAsync(cancellationToken).ConfigureAwait(false);
        await using (var purge = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(purge, null, $"DELETE FROM {_schema}.commands WHERE executed_at < now() - make_interval(secs => @g9_seconds)", cancellationToken,
                P("g9_seconds", NpgsqlDbType.Double, _options.CommandLogRetention.TotalSeconds)).ConfigureAwait(false);
        }

        var removed = 0;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in _tables.Values)
        {
            removed += await ScalarAsync<int>(connection, null, $"""
                WITH removed AS (
                    DELETE FROM {table.TrackingName} WHERE deleted AND changed_at < now() - make_interval(secs => @g9_seconds) RETURNING xid),
                newest AS (SELECT xid FROM removed ORDER BY xid DESC LIMIT 1),
                horizon AS (
                    UPDATE {_schema}.tables AS t
                    SET max_removed_xid = CASE WHEN t.max_removed_xid IS NULL OR newest.xid > t.max_removed_xid THEN newest.xid ELSE t.max_removed_xid END
                    FROM newest WHERE t.table_id = @g9_id RETURNING 1)
                SELECT count(*)::int FROM removed
                """, cancellationToken,
                P("g9_seconds", NpgsqlDbType.Double, retention.TotalSeconds), P("g9_id", NpgsqlDbType.Integer, table.TableId)).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>
    ///     The complete, idempotent DDL script for production review: base objects, capture objects for every table and
    ///     the table registrations. Apply it, then run the server with <see cref="G9CPostgresSyncOptions.AutoInstall" /> off.
    /// </summary>
    public async Task<string> GenerateInstallScriptAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await IntrospectAllAsync(connection, cancellationToken).ConfigureAwait(false);
        var script = new StringBuilder()
            .AppendLine("-- G9SyncData capture install script (idempotent). Review, then apply in one transaction.")
            .AppendLine("BEGIN;")
            .AppendLine(PgCaptureSql.Base(_schema));
        foreach (var table in tables)
        {
            var capture = PgCaptureSql.Capture(table, _schema, _options);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(capture)));
            script.AppendLine("-- " + table.Model.LogicalName).AppendLine(capture).AppendLine(PgCaptureSql.Register(table, _schema, hash));
        }

        return script.AppendLine("COMMIT;").ToString();
    }
}
