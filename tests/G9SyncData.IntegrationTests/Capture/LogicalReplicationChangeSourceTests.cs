using System.Diagnostics;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Capture;
using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace G9SyncData.IntegrationTests.Capture;

/// <summary>
///     The WAL capture backend against a real PostgreSQL with <c>wal_level=logical</c> (ADR-0021).
///     <para>
///         These tests exist because every interesting property of logical replication is invisible in a unit test:
///         that a slot resumes exactly where it was confirmed, that an unconfirmed batch is redelivered, that a delete
///         arrives with only its key, and that a table outside the model costs nothing. Each of those is a production
///         failure mode of the engine being replaced.
///     </para>
/// </summary>
[Collection("postgres")]
public sealed class LogicalReplicationChangeSourceTests : IAsyncLifetime
{
    private const string Publication = "g9wal_test";
    private const string Slot = "g9wal_test";

    private string _database = string.Empty;
    private string _connectionString = string.Empty;

    public async Task InitializeAsync()
    {
        if (PgTestServer.UnavailableReason is not null) return;

        _database = "g9wal_" + Guid.NewGuid().ToString("N")[..16];

        await using (var admin = new NpgsqlConnection(PgTestServer.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_database} TEMPLATE template0", admin);
            await create.ExecuteNonQueryAsync();
        }

        _connectionString = new NpgsqlConnectionStringBuilder(PgTestServer.AdminConnectionString)
        {
            Database = _database,
            Pooling = false
        }.ConnectionString;

        // One statement per command: a logical slot cannot be created in a transaction that has written (LES-0013),
        // and Npgsql batches multiple statements into one implicit transaction exactly like psql -c does.
        await ExecuteAsync("""CREATE TABLE "Pot" ("Id" uuid PRIMARY KEY, "Code" text NOT NULL, "RidgeId" uuid NULL)""");
        await ExecuteAsync("""CREATE TABLE "Untracked" ("Id" uuid PRIMARY KEY, "Value" text NULL)""");
        await ExecuteAsync($"""CREATE PUBLICATION {Publication} FOR TABLE "Pot", "Untracked" """);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(_database)) return;

        try
        {
            await ExecuteAsync($"SELECT pg_drop_replication_slot('{Slot}')");
        }
        catch (PostgresException)
        {
            // The slot may never have been created, or the test dropped it. Either way the database goes next, and a
            // leaked slot would retain WAL - which is why this is best-effort but never skipped.
        }

        await using var admin = new NpgsqlConnection(PgTestServer.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    [PostgresLogicalFact]
    public async Task Insert_update_and_delete_arrive_in_commit_order_with_their_keys()
    {
        var id = Guid.NewGuid();
        await using var source = CreateSource();

        var batches = await CollectAsync(source, expectedBatches: 3, async () =>
        {
            await ExecuteAsync($"""INSERT INTO "Pot" VALUES ('{id}', 'A-1', NULL)""");
            await ExecuteAsync($"""UPDATE "Pot" SET "Code" = 'A-2' WHERE "Id" = '{id}'""");
            await ExecuteAsync($"""DELETE FROM "Pot" WHERE "Id" = '{id}'""");
        });

        Assert.Equal(3, batches.Count);
        Assert.Equal([G9EChangeKind.Insert, G9EChangeKind.Update, G9EChangeKind.Delete],
            batches.Select(b => b.Rows.Single().Kind));
        Assert.All(batches, b => Assert.Equal("public.Pot", b.Rows.Single().TableLogicalName));
        Assert.All(batches, b => Assert.Equal(id.ToString(), b.Rows.Single().Key));

        // A DELETE carries only the primary key under the default replica identity - which is all the engine needs,
        // and the reason REPLICA IDENTITY FULL is deliberately NOT required (ADR-0021).
        Assert.Equal(G9EChangeKind.Delete, batches[^1].Rows.Single().Kind);

        // Positions are strictly increasing: they are the ordering authority (ADR-0022).
        Assert.True(string.CompareOrdinal(batches[0].Position.Token, batches[1].Position.Token) < 0);
        Assert.True(batches[0].CommitTimestampUtc <= batches[2].CommitTimestampUtc);
    }

    [PostgresLogicalFact]
    public async Task Several_changes_to_one_row_in_one_transaction_collapse_to_the_end_state()
    {
        var id = Guid.NewGuid();
        await using var source = CreateSource();

        var batches = await CollectAsync(source, expectedBatches: 1, async () =>
        {
            await ExecuteAsync($"""
                                BEGIN;
                                INSERT INTO "Pot" VALUES ('{id}', 'A-1', NULL);
                                UPDATE "Pot" SET "Code" = 'A-2' WHERE "Id" = '{id}';
                                UPDATE "Pot" SET "Code" = 'A-3' WHERE "Id" = '{id}';
                                COMMIT;
                                """);
        });

        // One transaction, one batch, one row: a device only ever needs the committed end state.
        var row = Assert.Single(Assert.Single(batches).Rows);
        Assert.Equal(id.ToString(), row.Key);
        Assert.Equal(G9EChangeKind.Update, row.Kind);
    }

    [PostgresLogicalFact]
    public async Task A_published_table_outside_the_sync_model_is_ignored()
    {
        var tracked = Guid.NewGuid();
        await using var source = CreateSource();

        var batches = await CollectAsync(source, expectedBatches: 1, async () =>
        {
            await ExecuteAsync($"""INSERT INTO "Untracked" VALUES ('{Guid.NewGuid()}', 'noise')""");
            await ExecuteAsync($"""INSERT INTO "Pot" VALUES ('{tracked}', 'A-1', NULL)""");
        });

        var row = Assert.Single(Assert.Single(batches).Rows);
        Assert.Equal("public.Pot", row.TableLogicalName);
        Assert.Equal(tracked.ToString(), row.Key);
    }

    [PostgresLogicalFact]
    public async Task A_confirmed_position_is_not_redelivered_but_an_unconfirmed_one_is()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        // Session 1: read two batches, confirm ONLY the first.
        G9CChangePosition confirmed;
        await using (var source = CreateSource())
        {
            var confirmedFirst = false;
            var batches = await CollectAsync(source, expectedBatches: 2, async () =>
            {
                await ExecuteAsync($"""INSERT INTO "Pot" VALUES ('{first}', 'A-1', NULL)""");
                await ExecuteAsync($"""INSERT INTO "Pot" VALUES ('{second}', 'B-1', NULL)""");
            },
            onBatch: async batch =>
            {
                // Confirm ONLY the first batch, from inside the live stream - exactly how the engine acknowledges
                // after it has durably applied one commit's worth of changes.
                if (confirmedFirst) return;
                confirmedFirst = true;
                await source.ConfirmAsync(batch.Position, CancellationToken.None);
            });

            confirmed = batches[0].Position;
            Assert.False(confirmed.IsStart);
        }

        // Session 2: the slot resumes after what was CONFIRMED, so the second insert must come again.
        await using (var source = CreateSource())
        {
            var batches = await CollectAsync(source, expectedBatches: 1, () => Task.CompletedTask);
            var keys = batches.SelectMany(b => b.Rows).Select(r => r.Key).ToList();

            Assert.Contains(second.ToString(), keys);
            Assert.DoesNotContain(first.ToString(), keys);
        }
    }

    [PostgresLogicalFact]
    public async Task A_missing_publication_fails_with_a_message_that_says_what_to_create()
    {
        var source = new G9CLogicalReplicationChangeSource(
            BuildModel(),
            new G9CLogicalReplicationOptions
            {
                ConnectionString = _connectionString,
                PublicationName = "does_not_exist",
                SlotName = Slot
            },
            NullLogger<G9CLogicalReplicationChangeSource>.Instance);

        await using (source)
        {
            var error = await Assert.ThrowsAsync<G9CSyncException>(
                () => source.EnsureInfrastructureAsync(CancellationToken.None));

            Assert.Contains("does_not_exist", error.Message, StringComparison.Ordinal);
            Assert.Contains("FOR ALL TABLES", error.Message, StringComparison.Ordinal);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private G9CLogicalReplicationChangeSource CreateSource()
        => new(BuildModel(),
            new G9CLogicalReplicationOptions
            {
                ConnectionString = _connectionString,
                PublicationName = Publication,
                SlotName = Slot,
                CreateSlotIfMissing = true
            },
            NullLogger<G9CLogicalReplicationChangeSource>.Instance);

    private static G9CSyncServerModel BuildModel()
    {
        var builder = new G9CSyncModelBuilder();
        builder.Table("public", "Pot", t => t
            .Key("Id")
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("RidgeId", G9ESyncColumnType.Guid));
        builder.Subscription("all", s => s.Include("public.Pot", "true"));
        return builder.Build();
    }

    /// <summary>
    ///     Starts the source, runs <paramref name="act" />, and collects until <paramref name="expectedBatches" />
    ///     batches have arrived or the deadline passes. The deadline is what turns "the stream stalled" into a failed
    ///     test instead of a hung one.
    /// </summary>
    private static async Task<List<G9CChangeBatch>> CollectAsync(IG9ChangeSource source, int expectedBatches, Func<Task> act,
        Func<G9CChangeBatch, Task>? onBatch = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var batches = new List<G9CChangeBatch>();
        var started = new TaskCompletionSource();

        // Create the slot BEFORE any write. A change committed before its slot exists is never decoded, and relying on a delay
        // for this made three tests fail under the load of a full parallel run (the slot was created inside the reader task).
        if (source is G9CLogicalReplicationChangeSource wal) await wal.EnsureInfrastructureAsync(cts.Token);

        var reader = Task.Run(async () =>
        {
            await foreach (var batch in source.ReadAsync(G9CChangePosition.Start, cts.Token))
            {
                started.TrySetResult();
                batches.Add(batch);
                if (onBatch is not null) await onBatch(batch);
                if (batches.Count >= expectedBatches) return;
            }
        }, cts.Token);

        // The slot already exists, so writes from here on are retained whether or not streaming has begun.
        await act();

        var stopwatch = Stopwatch.StartNew();
        while (batches.Count < expectedBatches && stopwatch.Elapsed < TimeSpan.FromSeconds(20) && !reader.IsCompleted)
        {
            await Task.Delay(50, cts.Token);
        }

        await cts.CancelAsync();
        try { await reader; } catch (OperationCanceledException) { /* expected: the stream is long-lived */ }

        return batches;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>A fact that is skipped unless the test server actually has <c>wal_level=logical</c>.</summary>
public sealed class PostgresLogicalFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> Reason = new(Probe);

    public PostgresLogicalFactAttribute()
    {
        if (Reason.Value is { } reason) Skip = reason;
    }

    private static string? Probe()
    {
        if (PgTestServer.UnavailableReason is { } unavailable)
            return "PostgreSQL test server unavailable (set G9SYNC_TEST_PG): " + unavailable;

        try
        {
            using var connection = new NpgsqlConnection(PgTestServer.AdminConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand("SELECT current_setting('wal_level')", connection);
            var walLevel = command.ExecuteScalar() as string;
            return string.Equals(walLevel, "logical", StringComparison.Ordinal)
                ? null
                : $"wal_level is '{walLevel}', not 'logical' - see AiGuides/20-Infrastructure-PostgreSQL.md";
        }
        catch (Exception ex)
        {
            return "Could not read wal_level: " + ex.Message;
        }
    }
}
