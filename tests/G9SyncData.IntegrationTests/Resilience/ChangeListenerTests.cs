using G9SyncData.IntegrationTests.Infrastructure;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Resilience;

/// <summary>
///     The server's change listener holds one database connection for as long as the server runs, and pokes devices from it. Losing
///     that connection must never end the pokes: a connection the database kills is replaced, and so is one that died without anyone
///     saying so (LES-0076).
/// </summary>
public sealed class ChangeListenerTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Pokes_resume_after_the_database_kills_the_listeners_connection()
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        var (client, site, pokes) = await PokedClientAsync(harness);
        await using var _ = client;
        await ExpectPokeAsync(harness, site, pokes, "before the kill");

        var killed = await harness.ScalarServerAsync<long>(
            "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = current_database() AND query ILIKE 'LISTEN%'");
        Assert.True(killed > 0, "No listener connection was found to kill.");

        await ExpectPokeAsync(harness, site, pokes, "after the database killed the listener's connection");
    }

    [PostgresFact]
    public async Task Pokes_resume_after_the_listeners_connection_goes_half_open()
    {
        var target = new NpgsqlConnectionStringBuilder(PgTestServer.AdminConnectionString);
        await using var proxy = new FreezingTcpProxy(target.Host!, target.Port);
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR,
            connection: c => (c.Host, c.Port) = ("127.0.0.1", proxy.Port),
            postgres: o => (o.ListenerHeartbeat, o.ListenerHeartbeatTimeout) = (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        var (client, site, pokes) = await PokedClientAsync(harness);
        await using var _ = client;
        await ExpectPokeAsync(harness, site, pokes, "before the partition");

        // The partition: every connection open now - the listener's among them - stays connected and carries nothing, ever.
        Assert.True(proxy.FreezeOpenConnections() > 0);
        harness.DataSource.Clear();   // the pool's idle connections are frozen too; the writes below need fresh ones

        await ExpectPokeAsync(harness, site, pokes, "after the listener's connection went half-open", TimeSpan.FromSeconds(30));
    }

    private static async Task<(SyncClient Client, Guid Site, Counter Pokes)> PokedClientAsync(SyncHarness harness)
    {
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        var pokes = new Counter();
        client.Transport.ChangesAvailable += _ => Interlocked.Increment(ref pokes.Value);
        return (client, site, pokes);
    }

    /// <summary>Writes a pot every half second until a poke arrives; a write made while the listener was away pokes nobody (pokes are hints).</summary>
    private async Task ExpectPokeAsync(SyncHarness harness, Guid site, Counter pokes, string when, TimeSpan? within = null)
    {
        var before = Volatile.Read(ref pokes.Value);
        var deadline = DateTime.UtcNow + (within ?? TimeSpan.FromSeconds(15));
        var writes = 0;
        while (DateTime.UtcNow < deadline)
        {
            await harness.InsertPotAsync(Guid.NewGuid(), site, "W-" + Guid.NewGuid().ToString("N")[..12]);
            writes++;
            await Task.Delay(500);
            if (Volatile.Read(ref pokes.Value) > before)
            {
                output.WriteLine($"poked {when} after {writes} write(s)");
                return;
            }
        }

        Assert.Fail($"No poke {when}: the listener never recovered.");
    }

    private sealed class Counter
    {
        public int Value;
    }
}
