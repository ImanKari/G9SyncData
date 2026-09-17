using G9SyncData.Server.Storage;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql;

/// <summary>
///     The durable effect outbox (ADR-0052). An effect is written in the same transaction as the rows that owe it, so it exists if and
///     only if those rows were committed, and a worker delivers it afterwards. Claiming skips locked rows and pushes the next attempt
///     forward, which is what lets several nodes share one queue and a crashed worker's claims return by themselves.
/// </summary>
public sealed partial class G9CPostgresSyncStore
{
    public async Task<IReadOnlyList<G9CPendingEffect>> ClaimEffectsAsync(int max, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        EnsureInitialized();
        var effects = new List<G9CPendingEffect>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            $"""
             UPDATE {_schema}.effects AS e
                SET attempts = e.attempts + 1, next_attempt_at = now() + @g9_retry
              WHERE e.effect_id IN (
                    SELECT effect_id FROM {_schema}.effects
                     WHERE NOT failed AND next_attempt_at <= now()
                     ORDER BY next_attempt_at
                     LIMIT @g9_max
                     FOR UPDATE SKIP LOCKED)
             RETURNING e.effect_id, e.table_id, e.client_id, e.batch_id, e.user_id, e.upserted, e.deleted, e.attempts, e.last_error
             """);
        command.Parameters.Add(P("g9_retry", NpgsqlDbType.Interval, retryDelay));
        command.Parameters.Add(P("g9_max", NpgsqlDbType.Integer, max));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            effects.Add(Read(reader));
        }

        return effects;
    }

    public async Task CompleteEffectAsync(Guid effectId, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, $"DELETE FROM {_schema}.effects WHERE effect_id = @g9_effect", cancellationToken,
            P("g9_effect", NpgsqlDbType.Uuid, effectId)).ConfigureAwait(false);
    }

    public async Task FailEffectAsync(Guid effectId, string error, int maxAttempts, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            $"UPDATE {_schema}.effects SET last_error = @g9_error, failed = (attempts >= @g9_max) WHERE effect_id = @g9_effect",
            cancellationToken, P("g9_error", NpgsqlDbType.Text, Truncate(error)), P("g9_max", NpgsqlDbType.Integer, maxAttempts),
            P("g9_effect", NpgsqlDbType.Uuid, effectId)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<G9CPendingEffect>> GetFailedEffectsAsync(int max, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        EnsureInitialized();
        var effects = new List<G9CPendingEffect>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = Command(connection, null,
            $"SELECT effect_id, table_id, client_id, batch_id, user_id, upserted, deleted, attempts, last_error FROM {_schema}.effects " +
            "WHERE failed ORDER BY created_at LIMIT @g9_max");
        command.Parameters.Add(P("g9_max", NpgsqlDbType.Integer, max));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            effects.Add(Read(reader));
        }

        return effects;
    }

    private static G9CPendingEffect Read(NpgsqlDataReader reader) => new()
    {
        EffectId = reader.GetGuid(0),
        TableId = reader.GetInt32(1),
        ClientId = reader.GetGuid(2),
        BatchId = reader.GetInt64(3),
        UserId = reader.IsDBNull(4) ? null : reader.GetString(4),
        UpsertedKeys = reader.GetFieldValue<string[]>(5),
        DeletedKeys = reader.GetFieldValue<string[]>(6),
        Attempt = reader.GetInt32(7),
        LastError = reader.IsDBNull(8) ? null : reader.GetString(8)
    };

    /// <summary>Keeps one runaway exception message from turning the outbox into a log store.</summary>
    private static string Truncate(string error) => error.Length <= 2000 ? error : error[..2000];
}
