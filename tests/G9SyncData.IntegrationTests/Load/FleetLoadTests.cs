using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Model;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Load;

/// <summary>
///     A whole fleet at once, against one server and one database: every device's first sync together, then every device editing
///     its own rows, inserting rows and fighting over one shared row while the office keeps writing — all pushed at the same moment —
///     then everybody pulling. The answer has to be exact: every device ends with the server's rows, no device edit is lost, the
///     shared row has exactly one winner and every loser KEPT its change as a refusal (no silent loss under load).
/// </summary>
/// <remarks>
///     <para>
///         Size: <c>G9SYNC_LOAD_DEVICES</c> (default <see cref="DefaultDevices" />, small enough for every run of the suite; 1000 is the
///         acceptance size, run on demand: <c>$env:G9SYNC_LOAD_DEVICES = 1000; dotnet test --filter "FullyQualifiedName~Load"</c>).
///         The server keeps its production admission limits, so a large fleet is refused as busy and must get through by retrying —
///         exactly what a real fleet does after a deploy.
///     </para>
///     <para>
///         Every device is a real engine with its own SQLite file. The in-memory transport removes the network, not the work: JSON
///         round trips, the PostgreSQL push and pull paths, admission and the client stores all run for real.
///     </para>
/// </remarks>
public sealed class FleetLoadTests(ITestOutputHelper output)
{
    private const int DefaultDevices = 40;

    private static int Devices =>
        int.TryParse(Environment.GetEnvironmentVariable("G9SYNC_LOAD_DEVICES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 1
            ? n
            : DefaultDevices;

    [PostgresFact]
    public Task A_fleet_syncs_edits_and_converges_in_memory() => RunFleetAsync(SyncTransportKind.InMemory, Devices);

    [PostgresFact]
    public Task A_fleet_syncs_edits_and_converges_over_real_SignalR() => RunFleetAsync(SyncTransportKind.SignalR, Math.Max(2, Devices / 2));

    private async Task RunFleetAsync(SyncTransportKind transport, int devices)
    {
        var total = Stopwatch.StartNew();
        await using var harness = await SyncHarness.CreateAsync(output, transport: transport,
            model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ColumnMerge)),
            configure: o => o.PageRows = 500);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "Fleet");
        await harness.InsertPotsAsync(site, devices + 1);
        var pots = await harness.PotIdsAsync(site);
        var shared = pots[0];

        // ---- the fleet
        var clients = new SyncClient[devices];
        await ForEachAsync(Enumerable.Range(0, devices), 16, async i =>
        {
            clients[i] = await harness.CreateClientAsync(configure: o => o.RetryBaseDelay = TimeSpan.FromMilliseconds(50),
                store: s => s.KeepLostConflicts = true);
            await clients[i].Engine.SubscribeAsync("sites");
            await clients[i].Engine.SubscribeAsync("site-pots", SitePots(site));
        });
        output.WriteLine($"{devices} devices created in {total.ElapsedMilliseconds} ms");

        // ---- 1. every first sync at once
        var phase = Stopwatch.StartNew();
        var first = await SyncAllAsync(clients, "first sync");
        output.WriteLine($"first syncs: {phase.ElapsedMilliseconds} ms, {first} runs retried after a refusal");
        foreach (var client in clients) Assert.Equal((long)devices + 1, await client.CountAsync("Pot"));

        // ---- 2. everybody edits: an own pot, a new pot, and the shared pot - while the office writes another column
        for (var i = 0; i < devices; i++)
        {
            var client = clients[i];
            await client.ExecAsync("UPDATE Pot SET Note = $p0 WHERE Id = $p1", "device-" + i, Key(pots[i + 1]));
            await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, 0, 0)",
                Key(Guid.NewGuid()), Key(site), "D" + i.ToString("D5", CultureInfo.InvariantCulture));
            await client.ExecAsync("UPDATE Pot SET Status = $p0 WHERE Id = $p1", 1000 + i, Key(shared));
        }

        using var office = new CancellationTokenSource();
        var officeWrites = 0;
        var officeLoop = Task.Run(async () =>
        {
            var random = new Random(7);
            while (!office.IsCancellationRequested)
            {
                // Weight only: a column no device touches, so a column merge must keep both sides.
                await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Weight" = $1 WHERE "Id" = $2""", (decimal)random.Next(1, 1000), pots[random.Next(1, pots.Count)]);
                Interlocked.Increment(ref officeWrites);
            }
        });

        // ---- 3. every push at the same moment, then the office stops and everybody pulls
        phase.Restart();
        var pushes = await SyncAllAsync(clients, "push", allowRejections: true);
        await office.CancelAsync();
        await officeLoop;
        output.WriteLine($"pushes: {phase.ElapsedMilliseconds} ms, {pushes} retried runs, {officeWrites} office writes meanwhile");

        phase.Restart();
        var pulls = await SyncAllAsync(clients, "final pull", allowRejections: true);
        output.WriteLine($"final pulls: {phase.ElapsedMilliseconds} ms, {pulls} retried runs");

        // ---- the answer
        // Every device edit landed: its note, its new pot.
        for (var i = 0; i < devices; i++)
        {
            Assert.Equal("device-" + i, await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[i + 1]));
        }

        Assert.Equal((long)devices, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "SiteId" = $1 AND "Code" LIKE 'D%'""", site));

        // The shared pot: exactly one device won, every other one kept its value as a refusal - none was lost in silence.
        var winner = await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", shared) - 1000;
        Assert.InRange(winner, 0, devices - 1);
        var losers = 0;
        for (var i = 0; i < devices; i++)
        {
            var rejections = await clients[i].Engine.GetRejectionsAsync();
            var kept = rejections.SingleOrDefault(r => r.Key == Key(shared));
            Assert.True(rejections.All(r => r.Key == Key(shared)), $"device {i}: unexpected refusals {string.Join(", ", rejections.Select(r => r.Code + " " + r.Key))}");
            if (i == winner)
            {
                Assert.Null(kept);
                continue;
            }

            if (kept is null)
            {
                var local = await clients[i].ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(shared));
                var meta = await clients[i].ScalarAsync("SELECT dirty || '/' || rejected || '/' || server_ver FROM g9_rows WHERE pk = $p0", Key(shared));
                output.WriteLine($"LOST? device {i}: local Status {local}, meta {meta}, server Status {winner + 1000}");
                continue;
            }
            Assert.StartsWith("G9S-CON", kept.Code, StringComparison.Ordinal);
            Assert.Equal(1000L + i, await clients[i].ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(shared)));
            losers++;
        }

        output.WriteLine($"losers kept: {losers} of {devices - 1}");
        Assert.Equal(devices - 1, losers);

        // Every device holds exactly the server's rows (the shared pot's Status aside: losers keep their own until they decide).
        var server = await ServerFingerprintAsync(harness, site);
        for (var i = 0; i < devices; i++)
        {
            var device = await DeviceFingerprintAsync(clients[i]);
            Assert.True(server == device, $"device {i} differs from the server:\n{Diff(server, device)}");
        }

        output.WriteLine($"{transport}, {devices} devices: converged in {total.ElapsedMilliseconds} ms");
    }

    /// <summary>
    ///     Syncs every client at once until each has a finished run; a run refused as busy (or failed on any transient cause after its
    ///     own retries) is run again, as an app would. Returns how many extra runs that took. Anything else fails the test.
    /// </summary>
    private async Task<int> SyncAllAsync(SyncClient[] clients, string what, bool allowRejections = false)
    {
        var again = 0;
        var errors = new ConcurrentBag<string>();
        await Task.WhenAll(clients.Select((client, i) => Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var result = await client.SyncAsync();
                if (result.Outcome == G9ESyncOutcome.Succeeded) return;
                if (allowRejections && result.Outcome == G9ESyncOutcome.CompletedWithRejections && result.Problems.Count == 0) return;
                if (result.Error is { Retryable: true })
                {
                    Interlocked.Increment(ref again);
                    await Task.Delay(Random.Shared.Next(50, 400));
                    continue;
                }

                errors.Add($"device {i}, {what}: {SyncClient.Describe(result)}");
                return;
            }

            errors.Add($"device {i}, {what}: no finished run after 30 attempts");
        })));
        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Take(20)));
        return again;
    }

    private static async Task ForEachAsync<T>(IEnumerable<T> items, int parallelism, Func<T, Task> body) =>
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, async (item, _) => await body(item));

    private static async Task<string> ServerFingerprintAsync(SyncHarness harness, Guid site)
    {
        var lines = new List<string>();
        await using var command = harness.DataSource.CreateCommand("""
            SELECT "Id", "Code", "Status", "Weight", "Note", "IsDeleted" FROM farm."Pot" WHERE "SiteId" = $1 AND NOT "IsDeleted" ORDER BY "Id"
            """);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = site });
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(Line(reader.GetGuid(0).ToString("D"), reader.GetString(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return string.Join('\n', lines);
    }

    private static async Task<string> DeviceFingerprintAsync(SyncClient client)
    {
        var lines = new List<string>();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + client.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Code, Status, Weight, Note FROM Pot ORDER BY Id";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(Line(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : decimal.Parse(reader.GetValue(3).ToString()!, CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return string.Join('\n', lines);
    }

    // The shared pot's Status is left out: a loser keeps its own value until it decides (asserted separately above).
    private static string Line(string id, string code, int status, decimal? weight, string? note) =>
        $"{id}|{code}|{(code == "P00001" ? "*" : status.ToString(CultureInfo.InvariantCulture))}|{weight?.ToString("0.####", CultureInfo.InvariantCulture)}|{note}";

    private static string Diff(string expected, string actual)
    {
        var e = expected.Split('\n').ToHashSet();
        var a = actual.Split('\n').ToHashSet();
        return "missing on the device: " + string.Join("; ", e.Except(a).Take(5)) + "\nextra on the device: " + string.Join("; ", a.Except(e).Take(5));
    }
}
