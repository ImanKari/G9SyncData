using G9SyncData.Client.Runs;
using G9SyncData.Client.SignalR;
using G9SyncData.Client.Sqlite;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Model;
using G9SyncData.Server.SnapshotFiles;
using G9SyncData.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.SnapshotFiles;

/// <summary>
///     Snapshot files end to end (ADR-0072): a large first download arrives as a SQLite file the device copies, and the database it leaves
///     is the database the row path leaves - row for row, version for version, membership for membership.
/// </summary>
public sealed class SnapshotFileSyncTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "g9sync-snapshot-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A late handle; the temp folder is disposable.
        }
    }

    private Task<SyncHarness> HarnessAsync(Action<G9CSnapshotFileOptions>? files = null, SyncTransportKind transport = SyncTransportKind.InMemory,
        Action<G9CSyncModelBuilder>? model = null, Action<G9SyncData.Server.G9CSyncServerOptions>? configure = null) =>
        SyncHarness.CreateAsync(output, configure: configure, transport: transport, model: model, server: s => s.UseSnapshotFiles(o =>
        {
            o.Directory = _directory;
            o.MinRows = 100;
            o.SegmentBytes = 64 * 1024;
            files?.Invoke(o);
        }));

    private static async Task<SyncClient> SubscribedAsync(SyncHarness harness, Guid site, bool files, Action<G9CSqliteSyncStoreOptions>? store = null)
    {
        var client = await harness.CreateClientAsync(store: o =>
        {
            o.AcceptSnapshotFiles = files;
            o.SnapshotFileCheckpointBytes = 128 * 1024;
            store?.Invoke(o);
        });
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        return client;
    }

    [PostgresFact]
    public async Task A_large_first_download_arrives_as_a_file_and_leaves_the_database_the_row_path_leaves()
    {
        await using var harness = await HarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3000);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Weight" = 12.5, "PlantedAt" = '2026-03-01 08:30:00', "Note" = 'n' || "Code" WHERE "Code" LIKE '%7'""");

        await using var viaFile = await SubscribedAsync(harness, site, files: true);
        var fileRun = await viaFile.SyncOkAsync();
        await using var viaRows = await SubscribedAsync(harness, site, files: false);
        var rowRun = await viaRows.SyncOkAsync();

        var diagnostics = fileRun.Changes.Diagnostics;
        output.WriteLine("file: " + diagnostics);
        output.WriteLine("rows: " + rowRun.Changes.Diagnostics);
        Assert.Equal(1, diagnostics.SnapshotFiles);
        Assert.Equal(G9SyncData.Client.Storage.G9CApplyDiagnostics.FileModeCopy, diagnostics.FileTables["Pot"]);
        Assert.Equal(3000, diagnostics.FileRows);
        Assert.Equal(0, rowRun.Changes.Diagnostics.SnapshotFiles);
        Assert.Equal(3001, fileRun.Downloaded);   // the site row as a frame (below the threshold), the pots as a file

        await AssertSameDatabaseAsync(viaRows, viaFile);
        Assert.Empty(Directory.EnumerateFiles(viaFile.Store.SnapshotFileDirectory));
    }

    [PostgresFact]
    public async Task A_second_device_reuses_the_built_file_and_the_deltas_bring_it_up_to_date()
    {
        await using var harness = await HarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2000);
        await using var first = await SubscribedAsync(harness, site, files: true);
        await first.SyncOkAsync();
        var service = harness.Services.GetRequiredService<G9CSnapshotFileService>();
        Assert.Single(service.Files);

        // The server moves on: edits, deletes, inserts. The second device gets the same file plus the delta since it was built.
        var pots = await harness.PotIdsAsync(site);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 9 WHERE "Id" = ANY($1)""", pots.Take(15).ToArray());
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = ANY($1)""", pots.Skip(100).Take(7).ToArray());
        await harness.InsertPotsAsync(site, 11, prefix: "N");

        await using var second = await SubscribedAsync(harness, site, files: true);
        var run = await second.SyncOkAsync();
        Assert.Single(service.Files);
        Assert.Equal(1, run.Changes.Diagnostics.SnapshotFiles);

        await using var reference = await SubscribedAsync(harness, site, files: false);
        await reference.SyncOkAsync();
        await AssertSameDatabaseAsync(reference, second, compareVersions: false);
        Assert.Equal(2000 - 7 + 11, await second.CountAsync("Pot"));
        Assert.Equal(15L, await second.ScalarAsync("SELECT count(*) FROM Pot WHERE Status = 9"));
    }

    [PostgresFact]
    public async Task A_download_cut_mid_file_resumes_at_the_last_checkpoint_not_at_the_start()
    {
        await using var harness = await HarnessAsync(o => o.SegmentBytes = 16 * 1024);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 4000);

        await using var client = await SubscribedAsync(harness, site, files: true, o => o.SnapshotFileCheckpointBytes = 64 * 1024);
        client.InMemory.Faults.BreakStreamAfter(G9ETransportOperation.Pull, chunks: 25);
        await client.SyncOkAsync();

        // The second stream started after a checkpoint (every 4 segments), not at the first segment.
        var resumes = client.InMemory.PullContinuations.Where(c => c is not null).ToList();
        Assert.NotEmpty(resumes);
        Assert.StartsWith("seg:", resumes[0]!.AfterKey, StringComparison.Ordinal);
        Assert.True(int.Parse(resumes[0]!.AfterKey![(resumes[0]!.AfterKey!.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture) >= 4);

        await using var reference = await SubscribedAsync(harness, site, files: false);
        await reference.SyncOkAsync();
        await AssertSameDatabaseAsync(reference, client, compareVersions: false);
    }

    [PostgresFact]
    public async Task A_download_that_moves_on_between_drops_survives_more_drops_than_the_retry_limit()
    {
        await using var harness = await HarnessAsync(o => o.SegmentBytes = 16 * 1024);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 4000);

        // One retry allowed, three drops - each after six segments, i.e. after three checkpoints of 32 KiB. Counted per download, the
        // second drop failed the run; counted per drop without progress, each drop starts from a later checkpoint and the run finishes.
        await using var client = await harness.CreateClientAsync(o => o.MaxRetries = 1, store: o =>
        {
            o.AcceptSnapshotFiles = true;
            o.SnapshotFileCheckpointBytes = 32 * 1024;
        });
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        client.InMemory.Faults.BreakStreamAfter(G9ETransportOperation.Pull, chunks: 6, times: 3);
        await client.SyncOkAsync();

        var resumes = client.InMemory.PullContinuations.Where(c => c is not null).ToList();
        Assert.Equal(3, resumes.Count);

        await using var reference = await SubscribedAsync(harness, site, files: false);
        await reference.SyncOkAsync();
        await AssertSameDatabaseAsync(reference, client, compareVersions: false);
    }

    [PostgresFact]
    public async Task A_link_that_drops_before_every_checkpoint_still_runs_out_of_retries()
    {
        await using var harness = await HarnessAsync(o => o.SegmentBytes = 16 * 1024);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 4000);

        // A checkpoint every 1 MiB, a drop after every second segment: segments arrive, but the resume point never moves, so this is
        // not progress and the run fails after MaxRetries instead of retrying for ever.
        await using var client = await harness.CreateClientAsync(o => o.MaxRetries = 2, store: o =>
        {
            o.AcceptSnapshotFiles = true;
            o.SnapshotFileCheckpointBytes = 1024 * 1024;
        });
        // The sites first, in a run of their own: their rows would be durable progress inside the faulted stream.
        await client.Engine.SubscribeAsync("sites");
        await client.SyncOkAsync();
        var pullsBefore = client.InMemory.PullContinuations.Count;
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        client.InMemory.Faults.BreakStreamAfter(G9ETransportOperation.Pull, chunks: 2, times: 10);
        var run = await client.SyncAsync();

        Assert.False(run.IsSuccess);
        Assert.Equal(G9SyncErrorCodes.NetworkFailure, run.Error?.Code);
        Assert.Equal(3, client.InMemory.PullContinuations.Count - pullsBefore);
    }

    [PostgresFact]
    public async Task A_reconnect_that_fails_while_the_network_is_still_down_is_retried_not_the_end_of_the_run()
    {
        await using var harness = await HarnessAsync(o => o.SegmentBytes = 16 * 1024);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 4000);

        // The connection drops after six segments, and the next two reconnects fail - the network is still down. Three retries are
        // allowed; the reconnect used to run in the catch, outside the retry, so the first failed reconnect failed the run.
        DroppingTransport? dropping = null;
        await using var client = await harness.CreateClientAsync(o => o.MaxRetries = 3, store: o =>
        {
            o.AcceptSnapshotFiles = true;
            o.SnapshotFileCheckpointBytes = 32 * 1024;
        }, transportFactory: () => dropping = new DroppingTransport(new G9CInMemorySyncTransport(harness.Engine, harness.Notifier), dropAfter: 6));
        await client.Engine.SubscribeAsync("sites");
        await client.SyncOkAsync();
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        dropping!.Inner.Faults.FailBefore(G9ETransportOperation.Connect, times: 2);
        await client.SyncOkAsync();

        Assert.True(dropping.Inner.CallCount(G9ETransportOperation.Connect) >= 4, "the two failed reconnects and the one that worked");
        await using var reference = await SubscribedAsync(harness, site, files: false);
        await reference.SyncOkAsync();
        await AssertSameDatabaseAsync(reference, client, compareVersions: false);
    }

    [PostgresFact]
    public async Task A_second_site_merges_into_the_table_the_first_one_filled()
    {
        await using var harness = await HarnessAsync();
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 1500);
        await harness.InsertPotsAsync(south, 1200, prefix: "S");

        await using var viaFile = await SubscribedAsync(harness, north, files: true);
        await viaFile.SyncOkAsync();
        await viaFile.Engine.SubscribeAsync("site-pots", SitePots(south));
        var second = await viaFile.SyncOkAsync();
        Assert.Equal(G9SyncData.Client.Storage.G9CApplyDiagnostics.FileModeMerge, second.Changes.Diagnostics.FileTables["Pot"]);

        await using var viaRows = await SubscribedAsync(harness, north, files: false);
        await viaRows.SyncOkAsync();
        await viaRows.Engine.SubscribeAsync("site-pots", SitePots(south));
        await viaRows.SyncOkAsync();
        await AssertSameDatabaseAsync(viaRows, viaFile, compareVersions: false);
    }

    [PostgresFact]
    public async Task Rows_with_pending_local_changes_are_not_overwritten_by_the_file_and_still_upload()
    {
        await using var harness = await HarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1000);
        var pots = await harness.PotIdsAsync(site);
        var fresh = Guid.NewGuid();

        // Before the site arrives, the device holds two pending inserts: one of the site's own pots (edited) and a pot the server has
        // never seen. The pull runs without the push, so both are still pending when the file is copied in.
        await using var client = await harness.CreateClientAsync(store: o => o.AcceptSnapshotFiles = true);
        await client.Engine.SubscribeAsync("sites");
        await client.SyncOkAsync();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'MINE', 42, 0)", Key(pots[0]), Key(site));
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'NEW-1', 7, 0)", Key(fresh), Key(site));
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        var run = await client.SyncOkAsync(new G9CSyncRequest { Push = false });

        Assert.Equal(1, run.Changes.Diagnostics.SnapshotFiles);
        Assert.Equal(G9SyncData.Client.Storage.G9CApplyDiagnostics.FileModeMerge, run.Changes.Diagnostics.FileTables["Pot"]);
        Assert.Equal(1, run.Changes.SkippedDirty);
        Assert.Equal(1001, await client.CountAsync("Pot"));
        Assert.Equal(42L, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.Equal("MINE", await client.ScalarAsync("SELECT Code FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.Equal(2, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        // The protected row still belongs to the subscription (membership), and both pending rows upload on the next run.
        var site_pots = (long)(await client.ScalarAsync("SELECT slot FROM g9_subscriptions WHERE name = 'site-pots'"))!;
        Assert.NotEqual(0L, (long)(await client.ScalarAsync("SELECT subs FROM g9_rows WHERE pk = $p0", Key(pots[0])))! & (1L << (int)site_pots));
        await client.SyncOkAsync();
        Assert.Equal(7, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", fresh));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task A_corrupt_segment_costs_one_replan_as_rows_not_the_run()
    {
        await using var harness = await HarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 800);

        // A transport that flips one byte of the first segment it carries.
        await using var client = await harness.CreateClientAsync(transportFactory: () => new CorruptingTransport(new G9CInMemorySyncTransport(harness.Engine, harness.Notifier)));
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        var run = await client.SyncOkAsync();

        Assert.Equal(0, run.Changes.Diagnostics.SnapshotFiles);
        Assert.Equal(800, await client.CountAsync("Pot"));
        Assert.Empty(Directory.EnumerateFiles(client.Store.SnapshotFileDirectory));
    }

    [PostgresFact]
    public async Task A_server_without_snapshot_files_and_a_subscription_that_refuses_them_both_send_rows()
    {
        var site = Guid.NewGuid();
        await using (var plain = await SyncHarness.CreateAsync(output))
        {
            await plain.InsertSiteAsync(site, "North");
            await plain.InsertPotsAsync(site, 500);
            await using var client = await SubscribedAsync(plain, site, files: true);
            var run = await client.SyncOkAsync();
            Assert.Equal(0, run.Changes.Diagnostics.SnapshotFiles);
            Assert.Equal(500, await client.CountAsync("Pot"));
        }

        await using var refusing = await HarnessAsync(model: m =>
        {
            ConfigureServer(m);
            m.Subscription("never-pots", s => s
                .Parameter("SiteId", G9ESyncColumnType.Guid)
                .Include("farm.Pot", "{row}.\"SiteId\" = @SiteId", "NOT {row}.\"IsDeleted\"")
                .SnapshotFiles(G9ESnapshotFilePolicy.Never)
                .DependsOn("sites"));
        });
        await refusing.InsertSiteAsync(site, "North");
        await refusing.InsertPotsAsync(site, 500);
        await using var never = await refusing.CreateClientAsync();
        await never.Engine.SubscribeAsync("sites");
        await never.Engine.SubscribeAsync("never-pots", SitePots(site));
        var neverRun = await never.SyncOkAsync();
        Assert.Equal(0, neverRun.Changes.Diagnostics.SnapshotFiles);
        Assert.Equal(500, await never.CountAsync("Pot"));
    }

    [PostgresTheory]
    [InlineData(G9ESyncHubProtocol.MessagePack)]
    [InlineData(G9ESyncHubProtocol.Json)]
    public async Task A_file_streams_over_real_SignalR_on_both_hub_protocols(G9ESyncHubProtocol protocol)
    {
        await using var harness = await HarnessAsync(transport: SyncTransportKind.SignalR);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2500);

        await using var client = await harness.CreateClientAsync(protocol: protocol);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        var run = await client.SyncOkAsync();
        Assert.Equal(1, run.Changes.Diagnostics.SnapshotFiles);
        Assert.Equal(2500, await client.CountAsync("Pot"));
        output.WriteLine($"{protocol}: {harness.Wire} - {run.Changes.Diagnostics}");
    }

    [PostgresFact]
    public async Task A_pull_planned_on_one_instance_is_served_by_another_that_builds_the_file_itself()
    {
        // Two instances behind one load balancer share the token signing key (ADR-0064), not the file directory.
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await using var harness = await HarnessAsync(configure: o => o.TokenSigningKey = key);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1500);
        var otherDirectory = Path.Combine(_directory, "other");
        var other = await harness.StartServerAsync(server: s => s.UseSnapshotFiles(o =>
        {
            o.Directory = otherDirectory;
            o.MinRows = 100;
            o.SegmentBytes = 64 * 1024;
        }));

        await using var client = await SubscribedAsync(harness, site, files: true);
        var transport = client.InMemory;
        transport.CallStarting += (operation, _) =>
        {
            if (operation == G9ETransportOperation.Pull) transport.Engine = other.Engine;
        };

        var run = await client.SyncOkAsync();
        Assert.Equal(1, run.Changes.Diagnostics.SnapshotFiles);
        Assert.Equal(1500, await client.CountAsync("Pot"));
        Assert.Single(other.Services.GetRequiredService<G9CSnapshotFileService>().Files);
    }

    // ================================================================ helpers

    private async Task AssertSameDatabaseAsync(SyncClient expected, SyncClient actual, bool compareVersions = true)
    {
        foreach (var sql in new[]
                 {
                     "SELECT * FROM Pot ORDER BY Id",
                     "SELECT * FROM Site ORDER BY Id",
                     compareVersions
                         ? "SELECT r.tid, r.pk, r.server_ver, r.subs, r.dirty, r.deleted FROM g9_rows r ORDER BY r.tid, r.pk"
                         : "SELECT r.tid, r.pk, r.subs, r.dirty, r.deleted FROM g9_rows r WHERE r.deleted = 0 ORDER BY r.tid, r.pk",
                     "SELECT key, cursor IS NOT NULL, state FROM g9_subscriptions ORDER BY key"
                 })
        {
            var left = await DumpAsync(expected.DatabasePath, sql);
            var right = await DumpAsync(actual.DatabasePath, sql);
            Assert.True(left.Count == right.Count, $"{sql}: {left.Count} rows vs {right.Count}");
            for (var i = 0; i < left.Count; i++) Assert.True(left[i] == right[i], $"{sql}, row {i}:\n expected {left[i]}\n actual   {right[i]}");
        }

        output.WriteLine("Databases match.");
    }

    private static async Task<List<string>> DumpAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i) ? "NULL" : reader.GetFieldType(i).Name + ":" + Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
            }

            rows.Add(string.Join(" | ", values));
        }

        return rows;
    }

    /// <summary>Passes everything through, except that the first snapshot-file segment arrives with one byte flipped.</summary>
    /// <summary>Drops the connection once, after <paramref name="dropAfter" /> pull chunks, as a phone losing its network does.</summary>
    private sealed class DroppingTransport(G9CInMemorySyncTransport inner, int dropAfter) : G9SyncData.Client.Transport.IG9SyncTransport
    {
        private int _dropped;

        public G9CInMemorySyncTransport Inner => inner;

        public bool IsConnected => inner.IsConnected;

        public event Action<G9DtChangesAvailable>? ChangesAvailable
        {
            add => inner.ChangesAvailable += value;
            remove => inner.ChangesAvailable -= value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);

        public Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken) => inner.HelloAsync(request, cancellationToken);

        public Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken) =>
            inner.PushAsync(request, chunks, cancellationToken);

        public Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken) => inner.PlanPullAsync(request, cancellationToken);

        public async IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var delivered = 0;
            await foreach (var chunk in inner.PullAsync(token, continuation, cancellationToken))
            {
                if (chunk.File is not null && ++delivered > dropAfter && Interlocked.Exchange(ref _dropped, 1) == 0) inner.Disconnect();
                yield return chunk;
            }
        }

        public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken) =>
            inner.ReconcileAsync(request, cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken) =>
            inner.FetchRowsAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class CorruptingTransport(G9CInMemorySyncTransport inner) : G9SyncData.Client.Transport.IG9SyncTransport
    {
        private int _corrupted;

        public bool IsConnected => inner.IsConnected;

        public event Action<G9DtChangesAvailable>? ChangesAvailable
        {
            add => inner.ChangesAvailable += value;
            remove => inner.ChangesAvailable -= value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);

        public Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken) => inner.HelloAsync(request, cancellationToken);

        public Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken) =>
            inner.PushAsync(request, chunks, cancellationToken);

        public Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken) => inner.PlanPullAsync(request, cancellationToken);

        public async IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var chunk in inner.PullAsync(token, continuation, cancellationToken))
            {
                if (chunk.File is not null && chunk.Payload is { Length: > 8 } payload && Interlocked.Exchange(ref _corrupted, 1) == 0)
                {
                    var copy = (byte[])payload.Clone();
                    copy[copy.Length / 2] ^= 0x5A;
                    chunk.Payload = copy;
                }

                yield return chunk;
            }
        }

        public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken) =>
            inner.ReconcileAsync(request, cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken) =>
            inner.FetchRowsAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
