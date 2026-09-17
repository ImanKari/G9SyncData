using System.Globalization;
using G9SyncData.Server.Capture;
using G9SyncData.Server.Model;
using Microsoft.Extensions.Logging;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.PgOutput;
using Npgsql.Replication.PgOutput.Messages;
using NpgsqlTypes;

namespace G9SyncData.Server.PostgreSql.Capture;

/// <summary>
///     Reads committed row changes from the PostgreSQL write-ahead log through a logical replication slot
///     (<c>pgoutput</c>) — the default capture backend (ADR-0021).
///     <para>
///         It yields keys, never column values (ADR-0027): the engine resolves what a client may see with the same
///         visibility query it uses everywhere else, so authorisation has exactly one implementation.
///     </para>
///     <para>
///         ⚠ Operational contract: a slot retains WAL until its consumer confirms. <see cref="ConfirmAsync" /> is what
///         releases it, and a consumer that reads without confirming will eventually invalidate its own slot (or fill
///         the disk, on an instance with no <c>max_slot_wal_keep_size</c>). See
///         <c>AiGuides/20-Infrastructure-PostgreSQL.md</c>.
///     </para>
/// </summary>
public sealed partial class G9CLogicalReplicationChangeSource : IG9ChangeSource
{
    private readonly G9CLogicalReplicationOptions _options;
    private readonly ILogger<G9CLogicalReplicationChangeSource> _logger;

    /// <summary>Key column name per <c>schema.table</c>, taken from the sync model.</summary>
    private readonly Dictionary<string, string> _keyColumnByTable;

    /// <summary>Relation id → (logical name, key ordinal). Populated from the relation messages the stream sends before first use of a table.</summary>
    private readonly Dictionary<uint, G9CRelationInfo> _relations = [];

    /// <summary>Explicit status updates are sent at most this often; Npgsql's own feedback timer covers idle periods.</summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(100);

    private LogicalReplicationConnection? _connection;
    private NpgsqlLogSequenceNumber _lastConfirmed;
    private long _lastStatusSentTimestamp;
    private bool _statusUnsent;

    public G9CLogicalReplicationChangeSource(G9CSyncServerModel model, G9CLogicalReplicationOptions options,
        ILogger<G9CLogicalReplicationChangeSource> logger)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _options = options;
        _logger = logger;
        _keyColumnByTable = model.Tables.ToDictionary(t => t.LogicalName, t => t.KeyColumn, StringComparer.OrdinalIgnoreCase);
    }

    public string Name => "wal";

    /// <summary>
    ///     Ensures the publication and the slot exist. Creating the SLOT is deliberately separated from any other
    ///     statement: PostgreSQL refuses to create a logical slot inside a transaction that has already written
    ///     (engineering log LES-0013).
    /// </summary>
    public async Task EnsureInfrastructureAsync(CancellationToken cancellationToken)
    {
        await using var admin = new NpgsqlConnection(_options.ConnectionString);
        await admin.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = new NpgsqlCommand("SELECT 1 FROM pg_publication WHERE pubname = @name", admin))
        {
            command.Parameters.AddWithValue("name", _options.PublicationName);
            var exists = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
            if (!exists)
            {
                throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                    $"Publication '{_options.PublicationName}' does not exist. Create it with an explicit table list "
                    + "(never FOR ALL TABLES) - see AiGuides/20-Infrastructure-PostgreSQL.md section 3.");
            }
        }

        await using (var command = new NpgsqlCommand(
                         "SELECT wal_status FROM pg_replication_slots WHERE slot_name = @name", admin))
        {
            command.Parameters.AddWithValue("name", _options.SlotName);
            var status = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;

            if (status is null)
            {
                if (!_options.CreateSlotIfMissing)
                {
                    throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                        $"Replication slot '{_options.SlotName}' does not exist and CreateSlotIfMissing is false.");
                }

                await using var replication = new LogicalReplicationConnection(_options.ConnectionString);
                await replication.Open(cancellationToken).ConfigureAwait(false);
                await replication.CreatePgOutputReplicationSlot(_options.SlotName, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                LogSlotCreated(_logger, _options.SlotName);
            }
            else if (!string.Equals(status, "reserved", StringComparison.Ordinal)
                     && !string.Equals(status, "extended", StringComparison.Ordinal))
            {
                // 'unreserved' or 'lost': the WAL this slot needed is gone, so resuming would silently skip changes.
                // Surfacing it is the whole point - the caller re-seeds rather than trusting an incomplete stream.
                throw new G9CSyncException(G9SyncErrorCodes.ResyncHorizon,
                    $"Replication slot '{_options.SlotName}' is '{status}': the WAL it needed has been removed. "
                    + "Drop the slot and re-seed - see AiGuides/20-Infrastructure-PostgreSQL.md section 9.");
            }
        }
    }

    public async IAsyncEnumerable<G9CChangeBatch> ReadAsync(G9CChangePosition from,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureInfrastructureAsync(cancellationToken).ConfigureAwait(false);

        var connection = new LogicalReplicationConnection(_options.ConnectionString);
        await connection.Open(cancellationToken).ConfigureAwait(false);
        _connection = connection;

        var slot = new PgOutputReplicationSlot(_options.SlotName);
        // messages: true delivers pg_logical_emit_message records - the push-origin marker and the sync points
        // (ADR-0028). Without it a push would wait for a sync point that is never reported.
        var options = new PgOutputReplicationOptions(_options.PublicationName, PgOutputProtocolVersion.V1, messages: true);

        // The slot itself remembers where it stopped, so a start position is only honoured when the caller has one
        // (a re-seed that wants to resume from a known LSN rather than from the slot's confirmed position).
        var startAt = from.IsStart ? default(NpgsqlLogSequenceNumber?) : ParsePosition(from);

        var rows = new List<G9CChangedRow>();
        var seen = new Dictionary<(string Table, string Key), int>();
        uint transactionId = 0;
        Guid? origin = null;

        await foreach (var message in connection
                           .StartReplication(slot, options, cancellationToken, startAt)
                           .ConfigureAwait(false))
        {
            G9CChangeBatch? batch = null;

            switch (message)
            {
                case RelationMessage relation:
                    CacheRelation(relation);
                    break;

                case BeginMessage begin:
                    rows.Clear();
                    seen.Clear();
                    transactionId = begin.TransactionXid;
                    origin = null;
                    break;

                case LogicalDecodingMessage decoded:
                {
                    // The payload is a stream that must be consumed before the next message is read (Npgsql recycles
                    // message instances), so it is read here even for prefixes this source ignores.
                    var payload = await ReadPayloadAsync(decoded, cancellationToken).ConfigureAwait(false);
                    var transactional = (decoded.Flags & 1) != 0;   // pgoutput: flag bit 1 = transactional message
                    if (transactional)
                    {
                        if (string.Equals(decoded.Prefix, G9CChangeMessages.PushPrefix, StringComparison.Ordinal))
                        {
                            origin = Guid.TryParse(payload, out var client) ? client : Guid.Empty;
                        }
                    }
                    else if (string.Equals(decoded.Prefix, G9CChangeMessages.SyncPointPrefix, StringComparison.Ordinal)
                             && Guid.TryParse(payload, out var nonce))
                    {
                        // Non-transactional messages are decoded in WAL order, so every transaction that committed
                        // before the marker was written has already been yielded above it.
                        batch = new G9CChangeBatch
                        {
                            Rows = [],
                            Position = G9CChangePosition.Start,
                            CommitTimestampUtc = DateTime.UtcNow,
                            SyncPoint = nonce
                        };
                    }

                    break;
                }

                case InsertMessage insert:
                    await AddAsync(insert.Relation.RelationId, G9EChangeKind.Insert, insert.NewRow, rows, seen, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case UpdateMessage update:
                    await AddAsync(update.Relation.RelationId, G9EChangeKind.Update, update.NewRow, rows, seen, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case KeyDeleteMessage keyDelete:
                    await AddAsync(keyDelete.Relation.RelationId, G9EChangeKind.Delete, keyDelete.Key, rows, seen, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case FullDeleteMessage fullDelete:
                    await AddAsync(fullDelete.Relation.RelationId, G9EChangeKind.Delete, fullDelete.OldRow, rows, seen, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case CommitMessage commit:
                    batch = new G9CChangeBatch
                    {
                        Rows = rows.ToArray(),
                        Position = new G9CChangePosition(commit.TransactionEndLsn.ToString()),
                        CommitTimestampUtc = commit.TransactionCommitTimestamp.ToUniversalTime(),
                        TransactionId = transactionId,
                        OriginClientId = origin
                    };
                    rows.Clear();
                    seen.Clear();
                    origin = null;
                    break;
            }

            // ⛔ Do NOT acknowledge here. SetReplicationStatus tells PostgreSQL "everything up to this point is durably
            // handled, recycle its WAL" - and at this point the engine has not handled anything yet. Acknowledging on
            // read would make a crash between reading and applying a SILENT DATA LOSS, which is the exact class of
            // defect this engine exists to remove. Only ConfirmAsync, called by the engine after it has durably
            // processed a batch, may advance the position. Until it does, the slot correctly retains WAL and an
            // unconfirmed batch is redelivered on reconnect (proved by
            // A_confirmed_position_is_not_redelivered_but_an_unconfirmed_one_is).
            //
            // Npgsql answers the server's keepalive requests on its own, so the connection stays healthy meanwhile.

            // An empty transaction (a commit that touched no synced table) still advances the stream: yielding it would
            // wake the engine for nothing, but its position must still reach ConfirmAsync eventually, which the caller
            // does by confirming the next non-empty batch. Keeping the slot moving is handled by SetReplicationStatus.
            if (batch is not null && (batch.IsSyncPoint || batch.Rows.Count > 0 || batch.OriginClientId.HasValue))
            {
                yield return batch;
            }
        }
    }

    public async ValueTask ConfirmAsync(G9CChangePosition position, CancellationToken cancellationToken)
    {
        if (position.IsStart || _connection is null) return;

        var lsn = ParsePosition(position);
        if (lsn <= _lastConfirmed) return;

        _lastConfirmed = lsn;

        // Recording the position is cheap and is what Npgsql's periodic feedback reports, so it is always done. The
        // explicit send is rate-limited: under heavy write load one round trip per transaction would be wasted work,
        // and a position sent late only means redelivery after a crash, never loss.
        _connection.SetReplicationStatus(lsn);
        if (_lastStatusSentTimestamp == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(_lastStatusSentTimestamp) >= StatusInterval)
        {
            await _connection.SendStatusUpdate(cancellationToken).ConfigureAwait(false);
            _lastStatusSentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _statusUnsent = false;
        }
        else
        {
            _statusUnsent = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is null) return;

        if (_statusUnsent)
        {
            try
            {
                // Best effort: a confirmed position that never reaches the server is redelivered next time, harmlessly.
                await _connection.SendStatusUpdate(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or IOException or ObjectDisposedException)
            {
                // the connection is already gone
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        _connection = null;
    }

    private static async ValueTask<string> ReadPayloadAsync(LogicalDecodingMessage message, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(message.Data, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 256, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlLogSequenceNumber ParsePosition(G9CChangePosition position)
    {
        if (NpgsqlLogSequenceNumber.TryParse(position.Token, out var lsn)) return lsn;
        throw new G9CSyncException(G9SyncErrorCodes.ServerError,
            $"'{position.Token}' is not a PostgreSQL log sequence number.");
    }

    private void CacheRelation(RelationMessage relation)
    {
        var logicalName = relation.Namespace + "." + relation.RelationName;
        if (!_keyColumnByTable.TryGetValue(logicalName, out var keyColumn))
        {
            // The publication carries a table the sync model does not know. Cache the miss so its rows are skipped
            // cheaply rather than re-resolved for every change.
            _relations[relation.RelationId] = new G9CRelationInfo(logicalName, -1);
            return;
        }

        var ordinal = -1;
        for (var i = 0; i < relation.Columns.Count; i++)
        {
            if (string.Equals(relation.Columns[i].ColumnName, keyColumn, StringComparison.OrdinalIgnoreCase))
            {
                ordinal = i;
                break;
            }
        }

        if (ordinal < 0)
        {
            throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid,
                $"Table '{logicalName}' is published but its key column '{keyColumn}' is not in the replicated column "
                + "list. Check the publication's column list and the sync model.");
        }

        _relations[relation.RelationId] = new G9CRelationInfo(logicalName, ordinal);
    }

    private async ValueTask AddAsync(uint relationId, G9EChangeKind kind, ReplicationTuple tuple,
        List<G9CChangedRow> rows, Dictionary<(string, string), int> seen, CancellationToken cancellationToken)
    {
        if (!_relations.TryGetValue(relationId, out var relation) || relation.KeyOrdinal < 0)
        {
            await DrainAsync(tuple).ConfigureAwait(false);
            return;
        }

        var key = await ReadKeyAsync(tuple, relation.KeyOrdinal, cancellationToken).ConfigureAwait(false);
        if (key is null) return;

        // Last change for a key wins within one transaction: a device only needs the end state.
        if (seen.TryGetValue((relation.LogicalName, key), out var index))
        {
            rows[index] = rows[index] with { Kind = kind };
            return;
        }

        seen[(relation.LogicalName, key)] = rows.Count;
        rows.Add(new G9CChangedRow(relation.LogicalName, kind, key));
    }

    private static async ValueTask<string?> ReadKeyAsync(ReplicationTuple tuple, int keyOrdinal,
        CancellationToken cancellationToken)
    {
        var ordinal = 0;
        string? key = null;

        // The tuple is a forward-only stream: every value must be visited in order, even the ones we ignore.
        await foreach (var value in tuple.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (ordinal++ != keyOrdinal) continue;
            if (value.IsDBNull) return null;

            using var reader = value.GetTextReader();
            key = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        return key;
    }

    private static async ValueTask DrainAsync(ReplicationTuple tuple)
    {
        await foreach (var _ in tuple.ConfigureAwait(false))
        {
            // Values of a table we do not sync: advance the stream without materialising anything.
        }
    }

    private readonly record struct G9CRelationInfo(string LogicalName, int KeyOrdinal);

    [LoggerMessage(EventId = 7301, Level = LogLevel.Information,
        Message = "G9SyncData created logical replication slot '{SlotName}'.")]
    private static partial void LogSlotCreated(ILogger logger, string slotName);
}

/// <summary>Options for <see cref="G9CLogicalReplicationChangeSource" />.</summary>
public sealed class G9CLogicalReplicationOptions
{
    /// <summary>A connection string the role may open a REPLICATION connection with.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>The publication naming the synced tables. Must exist; never <c>FOR ALL TABLES</c>.</summary>
    public string PublicationName { get; set; } = "g9sync";

    /// <summary>The slot this deployment owns. One slot per deployment.</summary>
    public string SlotName { get; set; } = "g9sync";

    /// <summary>Create the slot when it is missing (true) or fail (false, for environments where Ops owns slot creation).</summary>
    public bool CreateSlotIfMissing { get; set; } = true;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid, "G9CLogicalReplicationOptions.ConnectionString is required.");
        if (string.IsNullOrWhiteSpace(PublicationName))
            throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid, "G9CLogicalReplicationOptions.PublicationName is required.");
        if (string.IsNullOrWhiteSpace(SlotName))
            throw new G9CSyncException(G9SyncErrorCodes.ModelInvalid, "G9CLogicalReplicationOptions.SlotName is required.");
    }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"slot={SlotName} publication={PublicationName}");
}
