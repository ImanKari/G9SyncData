using System.Diagnostics;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Sqlite;
using G9SyncData.Client.Storage;
using G9SyncData.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Revocation;

/// <summary>
///     <c>ReleaseUnclaimedRowsAsync</c>: a device that synced another way before adopts its own rows on the first sync; the ones no
///     subscription claims (another site's, the office's deletes it never heard of) stay with <c>subs = 0, server_ver = 0</c> for ever,
///     and an app that treats "present" as "allowed" shows them. The release removes exactly those rows, locally, and never uploads
///     anything - and every other state a row can be in is left alone (one case each).
/// </summary>
public sealed class UnclaimedRowsTests(ITestOutputHelper output)
{
    private sealed record Device(SyncHarness Harness, SyncClient Client, Guid North, Guid[] NorthPots, Guid Foreign, Guid[] ForeignPots) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }

    /// <summary>
    ///     The server has North (3 pots). The device's database already holds North's pots AND a site the server never gave it, with
    ///     <paramref name="foreignPots" /> pots: both adopted on the first sync, only North's claimed by a subscription.
    /// </summary>
    private async Task<Device> AdoptedDeviceAsync(int foreignPots = 9, Action<G9CSqliteSyncStoreOptions>? store = null)
    {
        var harness = await SyncHarness.CreateAsync(output);
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 3);
        var northPots = (await harness.PotIdsAsync(north)).ToArray();
        var foreign = Guid.NewGuid();
        var foreignIds = Enumerable.Range(0, foreignPots).Select(_ => Guid.NewGuid()).ToArray();

        var client = await harness.CreateClientAsync(store: store, prepareDatabase: async path =>
        {
            await using var connection = new SqliteConnection("Data Source=" + path);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Site (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, IsActive INTEGER NOT NULL);
                CREATE TABLE Pot (Id TEXT NOT NULL PRIMARY KEY, SiteId TEXT NOT NULL, Code TEXT NOT NULL, Status INTEGER NOT NULL,
                                  Weight REAL NULL, PlantedAt TEXT NULL, Note TEXT NULL, IsDeleted INTEGER NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
            await using var transaction = connection.BeginTransaction();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Site VALUES ($id, $name, 1)";
            foreach (var (id, name) in new[] { (north, "North"), (foreign, "Foreign") })
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", Key(id));
                command.Parameters.AddWithValue("$name", name);
                await command.ExecuteNonQueryAsync();
            }

            command.CommandText = "INSERT INTO Pot VALUES ($id, $site, $code, 0, NULL, NULL, NULL, 0)";
            var all = northPots.Select((id, i) => (id, north, i)).Concat(foreignIds.Select((id, i) => (id, foreign, i)));
            foreach (var (id, site, i) in all)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", Key(id));
                command.Parameters.AddWithValue("$site", Key(site));
                command.Parameters.AddWithValue("$code", "P" + (i + 1).ToString("00000", System.Globalization.CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        });

        var adopt = new G9CSubscriptionOptions { AdoptExistingRows = true };
        await client.Engine.SubscribeAsync("sites", options: adopt);
        await client.Engine.SubscribeAsync("site-pots", SitePots(north), adopt);
        await client.SyncOkAsync();
        return new Device(harness, client, north, northPots, foreign, foreignIds);
    }

    private static Task<object?> TidAsync(SyncClient client, string table) =>
        client.ScalarAsync("SELECT tid FROM g9_tables WHERE local_name = $p0", table);

    [PostgresFact]
    public async Task Only_adopted_rows_no_subscription_claimed_are_released_and_every_other_state_is_left_alone()
    {
        await using var device = await AdoptedDeviceAsync();
        var client = device.Client;
        var tid = await TidAsync(client, "Pot");
        var p = device.ForeignPots.Select(Key).ToArray();
        Assert.Equal(0L, await client.ScalarAsync("SELECT count(*) FROM g9_rows WHERE tid = $p0 AND pk = $p1 AND (subs <> 0 OR server_ver <> 0)", tid, p[0]));

        // Every state a row can be in besides "adopted and unclaimed":
        await client.ExecAsync("UPDATE g9_rows SET server_ver = 7 WHERE tid = $p0 AND pk = $p1", tid, p[1]);                  // uploaded, unclaimed
        await client.ExecAsync("UPDATE g9_rows SET dirty = 1, dirty_mask = -1 WHERE tid = $p0 AND pk = $p1", tid, p[2]);       // a pending change
        await client.ExecAsync("UPDATE g9_rows SET rejected = 1 WHERE tid = $p0 AND pk = $p1", tid, p[3]);                    // refused by the server
        await client.ExecAsync("INSERT INTO g9_revoked (tid, pk, disposition, reason, rule, at_utc) VALUES ($p0, $p1, 1, 0, NULL, 0)", tid, p[4]);
        await client.ExecAsync("INSERT INTO g9_ephemeral_rows (sub_key, tid, pk) VALUES ('eph', $p0, $p1)", tid, p[5]);
        await client.ExecAsync("INSERT INTO g9_commands (command_id, seq, name, version, payload, created_utc, state) VALUES ('c1', 1, 'move', 1, '{}', 0, 0); " +
                               "INSERT INTO g9_command_rows (command_id, tid, pk) VALUES ('c1', $p0, $p1)", tid, p[6]);
        var feedBefore = (long)(await client.ScalarAsync("SELECT seq FROM g9_clock WHERE id = 1"))!;

        var report = await client.Engine.ReleaseUnclaimedRowsAsync("Pot");

        // p[0], p[7], p[8]: adopted, never claimed, nothing pending - gone, and nothing else is.
        Assert.Equal(3L, report.RevokedDeleted);
        Assert.Equal(0L, report.RevokedQuarantined);
        foreach (var gone in new[] { 0, 7, 8 }) Assert.False(await client.ExistsAsync("Pot", device.ForeignPots[gone]), $"p{gone}");
        foreach (var kept in new[] { 1, 2, 3, 4, 5, 6 }) Assert.True(await client.ExistsAsync("Pot", device.ForeignPots[kept]), $"p{kept}");
        foreach (var held in device.NorthPots) Assert.True(await client.ExistsAsync("Pot", held));
        var released = new[] { p[0], p[7], p[8] }.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(released, report.ChangedKeys["Pot"].Order(StringComparer.Ordinal));

        // Tombstones in the change feed, so a projection learns the rows left; and nothing to upload - least of all a delete.
        var feed = await client.Engine.ReadChangeFeedAsync("Pot", feedBefore);
        Assert.Equal(released, feed.Where(e => e.Deleted).Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.All(feed, e => Assert.False(e.LocalPending));
        Assert.Equal(1, (await client.Engine.GetPendingSummaryAsync()).TotalRows); // p[2]'s own pending change, untouched

        // Idempotent: a second pass (after a crash, or on the next start) finds nothing.
        Assert.Equal(0L, (await client.Engine.ReleaseUnclaimedRowsAsync("Pot")).RevokedDeleted);

        // The site table the same way: the foreign site goes, North (held) stays.
        var sites = await client.Engine.ReleaseUnclaimedRowsAsync("Site");
        Assert.Equal(1L, sites.RevokedDeleted);
        Assert.False(await client.ExistsAsync("Site", device.Foreign));
        Assert.True(await client.ExistsAsync("Site", device.North));
    }

    [PostgresFact]
    public async Task A_pending_change_is_quarantined_only_when_asked_for_and_never_uploaded()
    {
        await using var device = await AdoptedDeviceAsync(foreignPots: 3);
        var client = device.Client;
        var tid = await TidAsync(client, "Pot");
        var pending = Key(device.ForeignPots[0]);
        await client.ExecAsync("UPDATE Pot SET Note = 'measured offline' WHERE Id = $p0", pending); // capture marks it dirty

        Assert.Equal(1L, await client.ScalarAsync("SELECT dirty FROM g9_rows WHERE tid = $p0 AND pk = $p1", tid, pending));
        var only = await client.Engine.ReleaseUnclaimedRowsAsync("Pot", [pending]);
        Assert.Equal(0L, only.RevokedDeleted + only.RevokedQuarantined);
        Assert.True(await client.ExistsAsync("Pot", device.ForeignPots[0]));

        var asked = await client.Engine.ReleaseUnclaimedRowsAsync("Pot", [pending], includePending: true);

        Assert.Equal(1L, asked.RevokedQuarantined);
        Assert.False(await client.ExistsAsync("Pot", device.ForeignPots[0]));
        var held = Assert.Single(await client.Engine.GetQuarantinedAsync());
        Assert.Equal(pending, held.Key);
        Assert.Equal(G9ERevocationReason.Unclaimed, held.Reason);
        Assert.True(held.HadPendingChanges);
        Assert.Equal("measured offline", held.Values["Note"]);
        // Only the given key: the other unclaimed rows are still there.
        Assert.True(await client.ExistsAsync("Pot", device.ForeignPots[1]));

        await client.SyncOkAsync();
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
        Assert.Equal(0L, await device.Harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", device.ForeignPots[0]));
    }

    /// <summary>
    ///     L02: the set path (default policy) leaves exactly the state the row-by-row walk leaves - the app table, every <c>g9_rows</c>
    ///     column including each tombstone's <c>local_seq</c>, the clock - on the same rows; then its time on 200,000 rows.
    /// </summary>
    [PostgresFact]
    public async Task The_set_path_leaves_the_state_the_row_by_row_walk_leaves_and_releases_200000_rows_in_seconds()
    {
        const int rows = 20_000;
        // A rule that never matches forces the row-by-row walk and decides as the default policy does.
        var walk = G9CRevocationPolicy.Create(b => b.When("never", _ => false).Delete());
        await using var bySet = await AdoptedDeviceAsync(rows);
        await using var byRow = await AdoptedDeviceAsync(rows, store: o => o.Revocation = walk);

        async Task<(string State, TimeSpan Took)> ReleaseAsync(Device device)
        {
            // Same keys on both devices are not possible (two databases of fresh ids), so the state is compared by position.
            var clock = Stopwatch.StartNew();
            var report = await device.Client.Engine.ReleaseUnclaimedRowsAsync("Pot");
            clock.Stop();
            Assert.Equal(rows, report.RevokedDeleted);
            var tid = await TidAsync(device.Client, "Pot");
            var order = device.ForeignPots.Select(Key).Order(StringComparer.Ordinal).Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
            var lines = new List<string>();
            await using (var connection = new SqliteConnection("Data Source=" + device.Client.DatabasePath))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT pk, subs, server_ver, dirty, dirty_mask, rejected, deleted, local_seq - (SELECT min(local_seq) FROM g9_rows WHERE tid = $t AND deleted = 1) " +
                                      "FROM g9_rows WHERE tid = $t AND deleted = 1";
                command.Parameters.AddWithValue("$t", tid);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var position = order.TryGetValue(reader.GetString(0), out var at) ? at : -1;
                    lines.Add($"{position}:{reader.GetInt64(1)}:{reader.GetInt64(2)}:{reader.GetInt64(3)}:{reader.GetInt64(4)}:{reader.GetInt64(5)}:{reader.GetInt64(6)}:{reader.GetInt64(7)}");
                }
            }

            lines.Sort(StringComparer.Ordinal);
            var left = await device.Client.CountAsync("Pot");
            return ($"{left}|{string.Join(";", lines)}", clock.Elapsed);
        }

        var (setState, setTook) = await ReleaseAsync(bySet);
        var (rowState, rowTook) = await ReleaseAsync(byRow);
        output.WriteLine($"{rows:N0} rows: set {setTook.TotalMilliseconds:N0} ms, row by row {rowTook.TotalMilliseconds:N0} ms");
        Assert.Equal(rowState, setState);

        await using var large = await AdoptedDeviceAsync(200_000);
        var clock = Stopwatch.StartNew();
        var released = await large.Client.Engine.ReleaseUnclaimedRowsAsync("Pot");
        clock.Stop();
        output.WriteLine($"200,000 rows: set {clock.Elapsed.TotalMilliseconds:N0} ms");
        Assert.Equal(200_000L, released.RevokedDeleted);
        Assert.Equal(3L, await large.Client.CountAsync("Pot"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"200,000 rows took {clock.Elapsed}");
    }
}
