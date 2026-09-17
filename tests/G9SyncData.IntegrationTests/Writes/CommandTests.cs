using System.Text.Json.Serialization;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.IntegrationTests.Scopes;
using G9SyncData.Protocol;
using G9SyncData.Server;
using G9SyncData.Server.EfCore;
using G9SyncData.Server.Pipeline;
using G9SyncData.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SQLitePCL;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Writes;

/// <summary>
///     Commands (ADR-0025 command path): intents executed by the server's own handler, once, in order, with the device's local effects
///     protected until the server's canonical state replaces them.
/// </summary>
public sealed class CommandTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_swap_that_row_writes_cannot_express_succeeds_as_a_command_and_both_sides_agree()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);   // P00001, P00002

        // As row edits, the swap is two updates against a partial unique index: whichever lands first collides (E-09).
        await client.ExecAsync("UPDATE Pot SET Code = 'P00002' WHERE Id = $p0", Key(pots[0]));
        await client.ExecAsync("UPDATE Pot SET Code = 'P00001' WHERE Id = $p1", null, Key(pots[1]));
        var rows = await client.SyncAsync();
        Assert.True(rows.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(rows));
        Assert.Contains(await client.Engine.GetRejectionsAsync(), r => r.SqlState == "23505");
        await client.Engine.RetryRejectedAsync();
        await client.ExecAsync("UPDATE Pot SET Code = 'P00001' WHERE Id = $p0", Key(pots[0]));
        await client.ExecAsync("UPDATE Pot SET Code = 'P00002' WHERE Id = $p0", Key(pots[1]));
        await client.SyncAsync();

        Assert.Equal("P00001", await client.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.Equal("P00002", await client.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pots[1])));

        // As a command, the server's handler decides the order.
        var command = await client.Engine.ExecuteCommandAsync("swap-codes", new SwapCodes(pots[0], pots[1]), CommandJson.Default.SwapCodes,
            async (local, _) =>
            {
                var codeA = (string)(await local.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pots[0])))!;
                var codeB = (string)(await local.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pots[1])))!;
                await local.ExecuteAsync("UPDATE Pot SET Code = $p1 WHERE Id = $p0", Key(pots[0]), codeB);
                await local.ExecuteAsync("UPDATE Pot SET Code = $p1 WHERE Id = $p0", Key(pots[1]), codeA);
            });
        Assert.Equal("P00002", await client.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pots[0])));   // applied locally at once
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);                               // ...but not as row changes

        await client.SyncOkAsync();

        Assert.Equal("P00002", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
        Assert.Equal("P00001", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", pots[1]));
        Assert.Equal("P00002", await client.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.Equal("P00001", await client.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pots[1])));
        Assert.Equal(G9ELocalCommandState.Ok, (await client.Engine.GetCommandsAsync()).Single(c => c.CommandId == command.CommandId).State);
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).PendingCommands);
    }

    [PostgresFact]
    public async Task A_rejected_command_keeps_nothing_on_the_server_and_restores_the_device()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];
        var before = await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot);

        await client.Engine.ExecuteCommandAsync("set-status", new SetStatus(pot, 9), CommandJson.Default.SetStatus,
            async (local, _) => await local.ExecuteAsync("UPDATE Pot SET Status = 9, Note = 'optimistic' WHERE Id = $p0", Key(pot)));
        Assert.Equal(9L, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pot)));

        await client.SyncOkAsync();

        Assert.Equal(before, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Null(await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pot));   // the handler's partial write was rolled back
        Assert.Equal((long)before, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Null(await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
        var recorded = Assert.Single(await client.Engine.GetCommandsAsync());
        Assert.Equal(G9ELocalCommandState.Rejected, recorded.State);
        Assert.Equal("APP-STATUS-RANGE", recorded.Code);
    }

    [PostgresFact]
    public async Task A_command_whose_result_was_lost_is_executed_once()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];
        var before = await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot);

        await client.Engine.ExecuteCommandAsync("bump-status", new SetStatus(pot, 1), CommandJson.Default.SetStatus);
        client.InMemory.Faults.FailAfter(G9ETransportOperation.Push);   // the server commits, the device never hears
        await client.SyncOkAsync();

        Assert.Equal(2, client.InMemory.CallCount(G9ETransportOperation.Push));
        Assert.Equal(1, Recorder(harness).Executions("bump-status"));
        Assert.Equal(before + 1, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal((long)(before + 1), await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pot)));
    }

    [PostgresFact]
    public async Task A_command_resent_in_a_new_batch_after_its_result_was_lost_returns_the_stored_outcome_without_running_again()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site, o => o.MaxRetries = 0);
        var pot = (await harness.PotIdsAsync(site))[0];
        var before = await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot);

        await client.Engine.ExecuteCommandAsync("bump-status", new SetStatus(pot, 1), CommandJson.Default.SetStatus);
        client.InMemory.Faults.FailAfter(G9ETransportOperation.Push);   // the server commits, the device never hears, and does not retry
        Assert.Equal(G9ESyncOutcome.Failed, (await client.SyncAsync()).Outcome);
        Assert.Equal(1, Recorder(harness).Executions("bump-status"));

        // The device loses its in-flight batch record, so the command travels again in a NEW batch the server has never seen.
        await client.ExecAsync("DELETE FROM g9_meta WHERE key IN ('inflight_batch_id', 'inflight_max_seq')");
        client.InMemory.Faults.Clear();
        await client.SyncOkAsync();

        Assert.Equal(1, Recorder(harness).Executions("bump-status"));
        Assert.Equal(before + 1, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(G9ELocalCommandState.Ok, Assert.Single(await client.Engine.GetCommandsAsync()).State);
    }

    [PostgresFact]
    public async Task A_pending_command_survives_an_app_restart_and_runs_on_the_next_sync()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        var first = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];
        await first.Engine.ExecuteCommandAsync("set-note", new SetNote(pot, "after restart"), CommandJson.Default.SetNote,
            async (local, _) => await local.ExecuteAsync("UPDATE Pot SET Note = 'after restart' WHERE Id = $p0", Key(pot)));

        // The app is killed before it syncs.
        await first.Engine.DisposeAsync();
        await first.Store.DisposeAsync();
        SyncHarness.ClearPoolOf(first.DatabasePath);
        await using var second = await harness.CreateClientAsync(prepareDatabase: path =>
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                if (File.Exists(first.DatabasePath + suffix)) File.Copy(first.DatabasePath + suffix, path + suffix, overwrite: true);
            }

            return Task.CompletedTask;
        });
        await first.DisposeAsync();

        Assert.Equal(1, (await second.Engine.GetPendingSummaryAsync()).PendingCommands);
        await second.SyncOkAsync();
        Assert.Equal("after restart:confirmed", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal("after restart:confirmed", await second.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
    }

    [PostgresFact]
    public async Task A_pull_does_not_overwrite_a_row_its_pending_command_touched_and_the_result_settles_it()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];

        await client.Engine.ExecuteCommandAsync("set-note", new SetNote(pot, "mine"), CommandJson.Default.SetNote,
            async (local, _) => await local.ExecuteAsync("UPDATE Pot SET Note = 'mine' WHERE Id = $p0", Key(pot)));
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'server edit', "Status" = 3 WHERE "Id" = $1""", pot);

        var pull = await client.SyncOkAsync(new G9CSyncRequest { Push = false });
        Assert.True(pull.Changes.SkippedDirty >= 1);
        Assert.Equal("mine", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));

        await client.SyncOkAsync();
        Assert.Equal("mine:confirmed", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal(3L, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pot)));   // the server's other edit arrived too
    }

    [PostgresFact]
    public async Task An_unknown_command_is_rejected_with_a_stable_code_and_the_device_is_restored()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pot = (await harness.PotIdsAsync(site))[0];

        await client.Engine.ExecuteCommandAsync("no-such-command", "{}", async (local, _) => await local.ExecuteAsync("UPDATE Pot SET Note = 'x' WHERE Id = $p0", Key(pot)));
        await client.SyncOkAsync();

        var recorded = Assert.Single(await client.Engine.GetCommandsAsync());
        Assert.Equal(G9ELocalCommandState.Rejected, recorded.State);
        Assert.Equal(G9SyncErrorCodes.CommandUnknown, recorded.Code);
        Assert.Null(await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));
    }

    [PostgresFact]
    public async Task A_row_a_command_creates_outside_the_device_scope_is_not_kept_on_the_device()
    {
        await using var harness = await CreateHarnessAsync();
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await using var client = await harness.CreateSubscribedClientAsync(north);
        var created = Guid.NewGuid();

        await client.Engine.ExecuteCommandAsync("create-pot", new CreatePot(created, south, "ELSEWHERE"), CommandJson.Default.CreatePot,
            async (local, _) => await local.ExecuteAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'ELSEWHERE', 0, 0)",
                Key(created), Key(south)));
        Assert.True(await client.ExistsAsync("Pot", created));

        await client.SyncOkAsync();

        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.False(await client.ExistsAsync("Pot", created), "The device may not keep a row outside its subscriptions, even one it created.");
    }

    // ================================================================ commands on the app's own connection (A-11)

    [PostgresFact]
    public async Task A_command_recorded_on_the_apps_own_connection_commits_with_the_apps_other_writes()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        // The app's ORM connection (sqlite-net holds exactly this: a sqlite3 handle), inside the app's own transaction.
        using (var app = AppConnection.Open(client.DatabasePath))
        {
            app.Exec("BEGIN");
            app.Exec("UPDATE Pot SET Note = 'plain edit' WHERE Id = $p0", Key(pots[1]));   // an ordinary row change
            var command = client.Store.EnqueueCommand(app.Handle,
                new G9CCommandRequest { Name = "set-note", Payload = """{"PotId":"%ID%","Note":"mine"}""".Replace("%ID%", Key(pots[0]), StringComparison.Ordinal) },
                () => app.Exec("UPDATE Pot SET Note = 'mine' WHERE Id = $p0", Key(pots[0])));
            Assert.Equal(G9ELocalCommandState.Pending, command.State);

            // Nothing is visible to the store's own connections until the APP commits.
            Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).PendingCommands);
            app.Exec("COMMIT");
        }

        var pending = await client.Engine.GetPendingSummaryAsync();
        Assert.Equal((1, 1), (pending.PendingCommands, pending.TotalRows));   // the command's effect is not a row change; the plain edit is

        await client.SyncOkAsync();

        Assert.Equal("mine:confirmed", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
        Assert.Equal("plain edit", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[1]));
        Assert.Equal("mine:confirmed", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[0])));
    }

    [PostgresFact]
    public async Task A_command_on_the_apps_connection_is_undone_by_the_apps_rollback_and_by_its_own_failure()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);
        var request = new G9CCommandRequest { Name = "set-note", Payload = "{}" };

        using (var app = AppConnection.Open(client.DatabasePath))
        {
            // 1. The app rolls its transaction back: the command never existed.
            app.Exec("BEGIN");
            client.Store.EnqueueCommand(app.Handle, request, () => app.Exec("UPDATE Pot SET Note = 'rolled back' WHERE Id = $p0", Key(pots[0])));
            app.Exec("ROLLBACK");

            // 2. The effects throw inside the app's transaction: the command and its effects are undone, the app's transaction survives.
            app.Exec("BEGIN");
            app.Exec("UPDATE Pot SET Note = 'kept' WHERE Id = $p0", Key(pots[1]));
            Assert.Throws<InvalidOperationException>(() => client.Store.EnqueueCommand(app.Handle, request, () =>
            {
                app.Exec("UPDATE Pot SET Note = 'half done' WHERE Id = $p0", Key(pots[0]));
                throw new InvalidOperationException("validation failed");
            }));
            app.Exec("COMMIT");

            // 3. Outside any transaction the command is its own transaction; a failure leaves nothing.
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.Store.EnqueueCommandAsync(app.Handle, request, _ =>
            {
                app.Exec("UPDATE Pot SET Note = 'half done' WHERE Id = $p0", Key(pots[0]));
                throw new InvalidOperationException("validation failed");
            }));
            Assert.NotEqual(0, raw.sqlite3_get_autocommit(app.Handle));   // no transaction left open on the app's connection
        }

        Assert.Empty(await client.Engine.GetCommandsAsync());
        Assert.Null(await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.Equal("kept", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[1])));
        Assert.Equal(0L, await client.ScalarAsync("SELECT applying FROM g9_ctx WHERE id = 1"));
        var pending = await client.Engine.GetPendingSummaryAsync();
        Assert.Equal((0, 1), (pending.PendingCommands, pending.TotalRows));   // only the app's own edit, as an ordinary change
    }

    [PostgresFact]
    public async Task A_connection_open_on_another_database_is_refused()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var elsewhere = Path.Combine(Path.GetTempPath(), "g9sync-elsewhere-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var app = AppConnection.Open(elsewhere);
            var error = Assert.Throws<ArgumentException>(() => client.Store.EnqueueCommand(app.Handle, new G9CCommandRequest { Name = "set-note", Payload = "{}" }));
            Assert.Contains("not on the sync store's database", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(elsewhere);
        }

        Assert.Empty(await client.Engine.GetCommandsAsync());
    }

    /// <summary>An app-owned SQLite connection used through SQLitePCL.raw only, as sqlite-net uses it.</summary>
    private sealed class AppConnection : IDisposable
    {
        private AppConnection(sqlite3 handle) => Handle = handle;

        public sqlite3 Handle { get; }

        public static AppConnection Open(string path)
        {
            Assert.Equal(raw.SQLITE_OK, raw.sqlite3_open_v2(path, out var db, raw.SQLITE_OPEN_READWRITE | raw.SQLITE_OPEN_CREATE, null));
            raw.sqlite3_busy_timeout(db, 30_000);
            return new AppConnection(db);
        }

        public void Exec(string sql, params string[] args)
        {
            using var statement = Prepare(sql, args);
            var rc = raw.sqlite3_step(statement);
            Assert.True(rc == raw.SQLITE_DONE, raw.sqlite3_errmsg(Handle).utf8_to_string());
        }

        public void Dispose()
        {
            raw.sqlite3_close_v2(Handle);
            Handle.Dispose();
        }

        private sqlite3_stmt Prepare(string sql, string[] args)
        {
            Assert.True(raw.sqlite3_prepare_v2(Handle, sql, out var statement) == raw.SQLITE_OK, raw.sqlite3_errmsg(Handle).utf8_to_string());
            for (var i = 0; i < args.Length; i++) raw.sqlite3_bind_text(statement, i + 1, args[i]);
            return statement;
        }
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateHarnessAsync() => SyncHarness.CreateAsync(output, server: builder =>
    {
        builder.Services.AddDbContext<FarmDbContext>(options => options.UseNpgsql(EfScopeFixture.NeverOpened));
        builder.Services.AddSingleton<CommandRecorder>();

        builder.AddCommand<SwapCodes>("swap-codes", CommandJson.Default.SwapCodes, async (context, command, ct) =>
        {
            // The application's own DbContext, joined to the push transaction: the handler decides the order the index needs.
            await using var bound = await context.OpenDbContextAsync<FarmDbContext>(ct);
            var pots = await bound.Db.Pots.IgnoreQueryFilters().Where(p => p.Id == command.A || p.Id == command.B).ToListAsync(ct);
            var a = pots.Single(p => p.Id == command.A);
            var b = pots.Single(p => p.Id == command.B);
            var (codeA, codeB) = (a.Code, b.Code);
            a.Code = "~swap~" + command.A.ToString("N");
            await bound.Db.SaveChangesAsync(ct);
            b.Code = codeA;
            await bound.Db.SaveChangesAsync(ct);
            a.Code = codeB;
            await bound.Db.SaveChangesAsync(ct);
            return G9CCommandResult.Ok();
        });

        builder.AddCommand<SetStatus>("set-status", CommandJson.Default.SetStatus, async (context, command, ct) =>
        {
            // Write first, then refuse: the rollback of the command's savepoint must remove the partial write.
            await ExecAsync(context, """UPDATE farm."Pot" SET "Note" = 'half-done' WHERE "Id" = $1""", ct, command.PotId);
            if (command.Status > 4) return G9CCommandResult.Rejected("APP-STATUS-RANGE", "Status must be between 0 and 4.");
            await ExecAsync(context, """UPDATE farm."Pot" SET "Status" = $2, "Note" = NULL WHERE "Id" = $1""", ct, command.PotId, command.Status);
            return G9CCommandResult.Ok();
        });

        builder.AddCommand<SetStatus>("bump-status", CommandJson.Default.SetStatus, async (context, command, ct) =>
        {
            context.Services.GetRequiredService<CommandRecorder>().Record("bump-status");
            await ExecAsync(context, """UPDATE farm."Pot" SET "Status" = "Status" + $2 WHERE "Id" = $1""", ct, command.PotId, command.Status);
            return G9CCommandResult.Ok();
        });

        builder.AddCommand<SetNote>("set-note", CommandJson.Default.SetNote, async (context, command, ct) =>
        {
            await ExecAsync(context, """UPDATE farm."Pot" SET "Note" = $2 WHERE "Id" = $1""", ct, command.PotId, command.Note + ":confirmed");
            return G9CCommandResult.Ok();
        });

        builder.AddCommand<CreatePot>("create-pot", CommandJson.Default.CreatePot, async (context, command, ct) =>
        {
            await ExecAsync(context, """INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status") VALUES ($1, $2, $3, 0)""", ct, command.PotId, command.SiteId, command.Code);
            return G9CCommandResult.Ok();
        });
    });

    private static async Task ExecAsync(G9CCommandContext context, string sql, CancellationToken ct, params object[] args)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)context.Connection, (NpgsqlTransaction)context.Transaction);
        foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg });
        await command.ExecuteNonQueryAsync(ct);
    }

    private static CommandRecorder Recorder(SyncHarness harness) => harness.Services.GetRequiredService<CommandRecorder>();

    private sealed class CommandRecorder
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public void Record(string name)
        {
            lock (_counts) _counts[name] = _counts.GetValueOrDefault(name) + 1;
        }

        public int Executions(string name)
        {
            lock (_counts) return _counts.GetValueOrDefault(name);
        }
    }
}

internal sealed record SwapCodes(Guid A, Guid B);

internal sealed record SetStatus(Guid PotId, int Status);

internal sealed record SetNote(Guid PotId, string Note);

internal sealed record CreatePot(Guid PotId, Guid SiteId, string Code);

[JsonSerializable(typeof(SwapCodes))]
[JsonSerializable(typeof(SetStatus))]
[JsonSerializable(typeof(SetNote))]
[JsonSerializable(typeof(CreatePot))]
internal sealed partial class CommandJson : JsonSerializerContext;
