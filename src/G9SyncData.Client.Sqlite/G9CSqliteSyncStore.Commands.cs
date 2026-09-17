using System.Globalization;
using G9SyncData.Client.Storage;
using G9SyncData.Client.Sqlite.Native;
using SQLitePCL;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     The command outbox (ADR-0025 command path).
///     <list type="bullet">
///         <item><b>Recorded atomically with its local effects.</b> The command row and the app's local writes share one SQLite
///         transaction; if the local apply throws, neither exists.</item>
///         <item><b>Local effects are not row changes.</b> While <c>g9_ctx.applying = 2</c> the capture triggers stay silent and the
///         command triggers record each touched row in <c>g9_command_rows</c> instead: the server applies the COMMAND, never the
///         rows.</item>
///         <item><b>Protected until settled.</b> A pull does not overwrite a row touched by a pending command; the command's result
///         carries the row's canonical state (or a tombstone), which replaces the optimistic one whatever the outcome.</item>
///     </list>
/// </summary>
public sealed partial class G9CSqliteSyncStore
{
    /// <summary>Completed commands are kept this long for the app to inspect, then deleted.</summary>
    private static readonly TimeSpan CommandHistory = TimeSpan.FromDays(30);

    public async Task<G9CLocalCommand> EnqueueCommandAsync(G9CCommandRequest request, Func<IG9LocalCommandWriter, CancellationToken, Task>? applyLocally,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        EnsureInitialized();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var command = RecordCommand(connection, transaction, request);
            if (applyLocally is not null)
            {
                BeginCommandEffects(connection, transaction, command.CommandId);
                await applyLocally(new LocalCommandWriter(connection, transaction), cancellationToken).ConfigureAwait(false);
                EndCommandEffects(connection, transaction);
            }

            transaction.Commit();
            return command;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Records a command on the APP's own SQLite connection - for example sqlite-net's <c>SQLiteConnection.Handle</c> - so the command
    ///     and the local effects the app writes with its own data access in <paramref name="applyLocally" /> commit or roll back together
    ///     (A-11). Writes made on that connection inside <paramref name="applyLocally" /> are the command's effects (not uploaded as row
    ///     changes, protected until the server's result settles them); writes made before or after it are ordinary row changes.
    /// </summary>
    /// <remarks>
    ///     <list type="bullet">
    ///         <item>Call it where your code owns the connection (inside sqlite-net's <c>RunInTransaction</c>, or its async
    ///         connection's lock). Inside a transaction the command is a SAVEPOINT of it - if the app's transaction rolls back, the
    ///         command was never recorded; outside one it is its own <c>BEGIN IMMEDIATE</c> transaction.</item>
    ///         <item>The connection must be open on this store's database file.</item>
    ///         <item>The connection's own busy timeout applies while it waits for a sync that is writing; a lock not obtained in time
    ///         is a <see cref="G9CSqliteException" /> with <c>SqliteErrorCode</c> 5 and nothing is recorded.</item>
    ///     </list>
    /// </remarks>
    public G9CLocalCommand EnqueueCommand(sqlite3 appConnection, G9CCommandRequest request, Action? applyLocally = null)
    {
        using var scope = BeginOnAppConnection(appConnection, request, applyLocally is not null);
        applyLocally?.Invoke();
        return scope.Complete();
    }

    /// <summary>The asynchronous form of <see cref="EnqueueCommand" />, for effects written with asynchronous code on the same connection.</summary>
    public async Task<G9CLocalCommand> EnqueueCommandAsync(sqlite3 appConnection, G9CCommandRequest request, Func<CancellationToken, Task>? applyLocally,
        CancellationToken cancellationToken = default)
    {
        using var scope = BeginOnAppConnection(appConnection, request, applyLocally is not null);
        if (applyLocally is not null) await applyLocally(cancellationToken).ConfigureAwait(false);
        return scope.Complete();
    }

    private AppConnectionCommand BeginOnAppConnection(sqlite3 appConnection, G9CCommandRequest request, bool withEffects)
    {
        ArgumentNullException.ThrowIfNull(appConnection);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        EnsureInitialized();
        var connection = SqliteConnection.Borrow(appConnection);
        EnsureSameDatabase(appConnection);

        // No _writeLock: SQLite's write lock serialises this with the store's own writers, and waiting for the store's lock while the
        // app's connection may already hold SQLite's would deadlock both until a busy timeout.
        var transaction = connection.BeginNestedTransaction("g9_command");
        try
        {
            var command = RecordCommand(connection, transaction, request);
            if (withEffects) BeginCommandEffects(connection, transaction, command.CommandId);
            return new AppConnectionCommand(connection, transaction, command, withEffects);
        }
        catch
        {
            transaction.Dispose();
            throw;
        }
    }

    /// <summary>A command recorded on a connection in the wrong file would be silently lost: refuse it.</summary>
    private void EnsureSameDatabase(sqlite3 appConnection)
    {
        var appFile = raw.sqlite3_db_filename(appConnection, "main").utf8_to_string();
        var storeFile = SqliteConnection.DataSourceOf(_connectionString);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.IsNullOrEmpty(appFile) || !string.Equals(Path.GetFullPath(appFile), Path.GetFullPath(storeFile), comparison))
        {
            throw new ArgumentException($"The connection is open on '{appFile}', not on the sync store's database '{storeFile}'.", nameof(appConnection));
        }
    }

    private static G9CLocalCommand RecordCommand(SqliteConnection connection, SqliteTransaction transaction, G9CCommandRequest request)
    {
        var sequence = long.Parse(Meta(connection, transaction, "next_command_seq") ?? "1", CultureInfo.InvariantCulture);
        SetMeta(connection, transaction, "next_command_seq", (sequence + 1).ToString(CultureInfo.InvariantCulture));

        var commandId = Guid.NewGuid();
        var created = NowMs();
        Exec(connection, transaction,
            "INSERT INTO g9_commands (command_id, seq, name, version, payload, created_utc, state) VALUES ($id, $seq, $name, $version, $payload, $created, 0)",
            ("$id", commandId.ToString("D")), ("$seq", sequence), ("$name", request.Name), ("$version", request.Version), ("$payload", request.Payload),
            ("$created", created));
        return new G9CLocalCommand
        {
            CommandId = commandId,
            Sequence = sequence,
            Name = request.Name,
            Version = request.Version,
            Payload = request.Payload,
            CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(created).UtcDateTime,
            State = G9ELocalCommandState.Pending
        };
    }

    /// <summary>From here until <see cref="EndCommandEffects" />, writes on this connection are the command's effects (command triggers).</summary>
    private static void BeginCommandEffects(SqliteConnection connection, SqliteTransaction transaction, Guid commandId) =>
        Exec(connection, transaction, "UPDATE g9_ctx SET applying = 2, command_id = $id WHERE id = 1", ("$id", commandId.ToString("D")));

    private static void EndCommandEffects(SqliteConnection connection, SqliteTransaction transaction) =>
        Exec(connection, transaction, "UPDATE g9_ctx SET applying = 0, command_id = NULL WHERE id = 1");

    public async Task<IReadOnlyList<G9CLocalCommand>> GetCommandsAsync(G9ELocalCommandState? state, int limit, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = Command(connection, null,
            "SELECT command_id, seq, name, version, payload, created_utc, state, code, message, result, completed_utc FROM g9_commands " +
            (state is null ? string.Empty : "WHERE state = $state ") + "ORDER BY seq DESC LIMIT $limit");
        if (state is not null) command.Parameters.AddWithValue("$state", (int)state.Value);
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        var commands = new List<G9CLocalCommand>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            commands.Add(new G9CLocalCommand
            {
                CommandId = Guid.Parse(reader.GetString(0)),
                Sequence = reader.GetInt64(1),
                Name = reader.GetString(2),
                Version = reader.GetInt32(3),
                Payload = reader.GetString(4),
                CreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)).UtcDateTime,
                State = (G9ELocalCommandState)reader.GetInt32(6),
                Code = reader.IsDBNull(7) ? null : reader.GetString(7),
                Message = reader.IsDBNull(8) ? null : reader.GetString(8),
                Result = reader.IsDBNull(9) ? null : reader.GetString(9),
                CompletedUtc = reader.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)).UtcDateTime
            });
        }

        return commands;
    }

    /// <summary>Pending commands for a push batch, with the rows each touched, as wire DTOs.</summary>
    private List<Protocol.G9DtCommand> ReadPendingCommands(SqliteConnection connection, SqliteTransaction transaction, string? inflightBatch, int limit)
    {
        var commands = new List<Protocol.G9DtCommand>();
        using (var command = Command(connection, transaction,
                   "SELECT command_id, seq, name, version, payload, created_utc FROM g9_commands WHERE state = 0" +
                   // A retried batch carries exactly its own commands; a new batch takes every pending one (including commands of a
                   // batch whose result was lost - the server recognises their ids).
                   (inflightBatch is null ? string.Empty : " AND batch_id = $inflight") + " ORDER BY seq LIMIT $limit"))
        {
            if (inflightBatch is not null) command.Parameters.AddWithValue("$inflight", long.Parse(inflightBatch, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                commands.Add(new Protocol.G9DtCommand
                {
                    CommandId = Guid.Parse(reader.GetString(0)),
                    Sequence = reader.GetInt64(1),
                    Name = reader.GetString(2),
                    Version = reader.GetInt32(3),
                    Payload = reader.GetString(4),
                    CreatedUtcMs = reader.GetInt64(5)
                });
            }
        }

        using var touched = Command(connection, transaction, "SELECT tid, pk FROM g9_command_rows WHERE command_id = $id");
        var id = touched.Parameters.Add("$id", SqliteType.Text);
        foreach (var item in commands)
        {
            id.Value = item.CommandId.ToString("D");
            using var reader = touched.ExecuteReader();
            while (reader.Read())
            {
                var local = _byLocal.Values.FirstOrDefault(t => t.Tid == reader.GetInt64(0));
                if (local?.Negotiated is null) continue;
                item.Touched.Add(new Protocol.G9DtRowRef { TableId = local.Negotiated.TableId, Key = reader.GetString(1) });
            }
        }

        return commands;
    }

    /// <summary>Records server outcomes and releases the rows those commands protected, before their canonical rows are applied.</summary>
    private static long CompleteCommands(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Protocol.G9DtCommandResult> results)
    {
        long completed = 0;
        var now = NowMs();
        foreach (var result in results)
        {
            var id = result.CommandId.ToString("D");
            var state = result.Status switch
            {
                Protocol.G9ECommandStatus.Ok => G9ELocalCommandState.Ok,
                Protocol.G9ECommandStatus.Superseded => G9ELocalCommandState.Superseded,
                _ => G9ELocalCommandState.Rejected
            };
            completed += Convert.ToInt64(Scalar(connection, transaction,
                "UPDATE g9_commands SET state = $state, code = $code, message = $message, result = $result, completed_utc = $now " +
                "WHERE command_id = $id AND state = 0 RETURNING 1",
                ("$state", (int)state), ("$code", result.Code), ("$message", result.Message), ("$result", result.Result), ("$now", now), ("$id", id)) ?? 0L,
                CultureInfo.InvariantCulture);
            Exec(connection, transaction, "DELETE FROM g9_command_rows WHERE command_id = $id", ("$id", id));
        }

        Exec(connection, transaction, "DELETE FROM g9_commands WHERE state <> 0 AND completed_utc < $cutoff",
            ("$cutoff", now - (long)CommandHistory.TotalMilliseconds));
        return completed;
    }

    /// <summary>A command being recorded on the app's connection; disposed without <see cref="Complete" />, it leaves no trace.</summary>
    private sealed class AppConnectionCommand(SqliteConnection connection, SqliteTransaction transaction, G9CLocalCommand command, bool withEffects)
        : IDisposable
    {
        public G9CLocalCommand Complete()
        {
            if (withEffects) EndCommandEffects(connection, transaction);
            transaction.Commit();
            return command;
        }

        public void Dispose()
        {
            transaction.Dispose();
            connection.Dispose();
        }
    }

    private sealed class LocalCommandWriter(SqliteConnection connection, SqliteTransaction transaction) : IG9LocalCommandWriter
    {
        public Task<int> ExecuteAsync(string sql, params object?[] args)
        {
            using var command = Bind(sql, args);
            return Task.FromResult(command.ExecuteNonQuery());
        }

        public Task<object?> ScalarAsync(string sql, params object?[] args)
        {
            using var command = Bind(sql, args);
            var value = command.ExecuteScalar();
            return Task.FromResult(value is DBNull ? null : value);
        }

        private SqliteCommand Bind(string sql, object?[] args)
        {
            var command = Command(connection, transaction, sql);
            for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), args[i] ?? DBNull.Value);
            return command;
        }
    }
}
