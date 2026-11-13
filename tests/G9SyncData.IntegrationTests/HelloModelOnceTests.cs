using System.Collections.Concurrent;
using System.Reflection;
using G9SyncData.Client.SignalR;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server;
using G9SyncData.Server.Auth;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests;

/// <summary>
///     Hello sends the client model once per connection and answers "unchanged" for what the client holds (1.5.21, AgriPad TR-0069): a
///     device used to send its whole model (~30 KB for AgriPad) with every Hello, get every negotiated table and definition back and save
///     them again - on every sync. What must not change: the negotiation, the pokes (the hub joins the groups from the negotiated tables,
///     also when the answer leaves them out), a reconnect (a new connection starts with the full model) and a server that lost the model.
/// </summary>
public sealed class HelloModelOnceTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task The_server_names_the_model_answers_unchanged_for_what_the_client_holds_and_asks_for_a_model_it_lost()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var full = new G9DtHelloRequest { ClientId = Guid.NewGuid(), Tables = TestSchema.Client.Tables.Select(t => t.ToWire()).ToList() };

        var first = await harness.Engine.HelloAsync(G9CSyncCaller.Anonymous, full);
        Assert.False(string.IsNullOrEmpty(first.ModelHash));
        Assert.False(string.IsNullOrEmpty(first.TablesHash));
        Assert.False(string.IsNullOrEmpty(first.DefinitionsHash));
        Assert.NotEmpty(first.Tables);
        Assert.NotEmpty(first.Definitions);
        Assert.False(first.TablesUnchanged || first.DefinitionsUnchanged || first.ModelRequired);

        // Named by its id: the same negotiation.
        var named = await harness.Engine.HelloAsync(G9CSyncCaller.Anonymous, new G9DtHelloRequest { ClientId = full.ClientId, ModelHash = first.ModelHash });
        Assert.False(named.ModelRequired);
        Assert.Equal(first.Tables.Select(t => (t.TableId, t.Name, t.Columns.Count)), named.Tables.Select(t => (t.TableId, t.Name, t.Columns.Count)));
        Assert.Equal(first.TablesHash, named.TablesHash);

        // What the client holds is not sent again - but the hub still learns the negotiated tables (its poke groups).
        IReadOnlyList<G9DtNegotiatedTable> negotiated = [];
        var unchanged = await harness.Engine.HelloAsync(G9CSyncCaller.Anonymous,
            new G9DtHelloRequest { ClientId = full.ClientId, ModelHash = first.ModelHash, KnownTablesHash = first.TablesHash, KnownDefinitionsHash = first.DefinitionsHash },
            tables => negotiated = tables);
        Assert.True(unchanged.TablesUnchanged);
        Assert.True(unchanged.DefinitionsUnchanged);
        Assert.Empty(unchanged.Tables);
        Assert.Empty(unchanged.UnknownTables);
        Assert.Empty(unchanged.Definitions);
        Assert.Equal(first.Tables.Select(t => t.Name), negotiated.Select(t => t.Name));

        // A stale id for one of them: that one is sent.
        var stale = await harness.Engine.HelloAsync(G9CSyncCaller.Anonymous,
            new G9DtHelloRequest { ClientId = full.ClientId, ModelHash = first.ModelHash, KnownTablesHash = "old", KnownDefinitionsHash = first.DefinitionsHash });
        Assert.False(stale.TablesUnchanged);
        Assert.NotEmpty(stale.Tables);
        Assert.True(stale.DefinitionsUnchanged);

        // A model this server never saw (it restarted): asked for, and nothing else in the answer.
        var lost = await harness.Engine.HelloAsync(G9CSyncCaller.Anonymous, new G9DtHelloRequest { ClientId = full.ClientId, ModelHash = "unknown-model" });
        Assert.True(lost.ModelRequired);
        Assert.Empty(lost.Tables);
        Assert.Empty(lost.Subscriptions);
        Assert.Null(lost.Error);

        // An older client (no ids at all) is answered exactly as before.
        var old = await harness.Engine.HelloAsync(G9CSyncCaller.Anonymous, full);
        Assert.False(old.TablesUnchanged || old.DefinitionsUnchanged);
        Assert.Equal(first.Tables.Count, old.Tables.Count);
    }

    [PostgresTheory]
    [InlineData(G9ESyncHubProtocol.Json)]
    [InlineData(G9ESyncHubProtocol.MessagePack)]
    public async Task Over_signalr_the_model_goes_once_per_connection_and_everything_still_syncs_and_pokes(G9ESyncHubProtocol protocol)
    {
        var hellos = new HelloRecorder();
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR,
            server: s => s.Services.AddSingleton<IG9SyncConnectGate>(hellos));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 20);
        await using var client = await harness.CreateClientAsync(protocol: protocol);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        await client.SyncOkAsync();
        await client.SyncOkAsync();
        await client.SyncOkAsync();
        var seen = hellos.Take();
        Assert.Equal(3, seen.Count);
        Assert.True(seen[0].Tables > 0 && seen[0].ModelHash is null, "the first Hello of a connection sends the model");
        Assert.All(seen.Skip(1), h => Assert.True(h.Tables == 0 && h.ModelHash is not null, "a later Hello on it names the model"));
        Assert.All(seen.Skip(1), h => Assert.True(h.KnownTablesHash is not null && h.KnownDefinitionsHash is not null));

        // Still a working device: down, up, and the saved negotiation untouched.
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'later' WHERE "SiteId" = $1""", site);
        var created = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'HM-1', 1, 0)", Key(created), Key(site));
        await client.SyncOkAsync();
        Assert.Equal(20L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note = 'later'"));
        Assert.Equal("HM-1", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.NotEqual("[]", await client.ScalarAsync("SELECT value FROM g9_meta WHERE key = 'tables_json'"));

        // The server loses every model (a restart): the next Hello is asked for it and resends it - the run does not notice.
        ForgetModels(harness.Engine);
        hellos.Take();
        await client.SyncOkAsync();
        seen = hellos.Take();
        Assert.Equal(2, seen.Count);
        Assert.Equal(0, seen[0].Tables);
        Assert.True(seen[1].Tables > 0);

        // A dropped link: the reconnect starts its connection with the full model (the rejoin), and a poke still arrives.
        var signalR = (G9CSignalRSyncTransport)client.Transport;
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signalR.Connection.Reconnected += _ =>
        {
            reconnected.TrySetResult();
            return Task.CompletedTask;
        };
        Assert.True(harness.Wire!.AbortAll() > 0);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(60));
        var poked = new TaskCompletionSource<G9DtChangesAvailable>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Transport.ChangesAvailable += changes =>
        {
            if (changes.Tables.Contains("farm.Pot", StringComparer.OrdinalIgnoreCase)) poked.TrySetResult(changes);
        };
        var inserted = 0;
        for (var attempt = 0; attempt < 15 && !poked.Task.IsCompleted; attempt++)
        {
            await harness.InsertPotAsync(Guid.NewGuid(), site, "AFTER-" + attempt);
            inserted++;
            await Task.WhenAny(poked.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        }

        Assert.True(poked.Task.IsCompleted, "no poke after the reconnect: the hub did not join the groups of an unchanged answer");
        var rejoin = hellos.Take();
        Assert.True(rejoin.Count >= 1 && rejoin[0].Tables > 0, "the rejoin of a new connection carries the full model");
        await client.SyncOkAsync();
        Assert.Equal(21 + inserted, await client.CountAsync("Pot"));
    }

    /// <summary>Clears the server's negotiated models, as a restart does (a test-only reach into the engine).</summary>
    private static void ForgetModels(G9CSyncServerEngine engine)
    {
        var field = typeof(G9CSyncServerEngine).GetField("_models", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((System.Collections.IDictionary)field.GetValue(engine)!).Clear();
    }

    /// <summary>Records what every Hello carried (a gate sees each request before the engine answers it).</summary>
    private sealed class HelloRecorder : IG9SyncConnectGate
    {
        private readonly ConcurrentQueue<(int Tables, string? ModelHash, string? KnownTablesHash, string? KnownDefinitionsHash)> _seen = new();

        public ValueTask<G9DtSyncError?> CheckAsync(G9CSyncCaller caller, G9DtHelloRequest request, CancellationToken cancellationToken)
        {
            _seen.Enqueue((request.Tables.Count, request.ModelHash, request.KnownTablesHash, request.KnownDefinitionsHash));
            return ValueTask.FromResult<G9DtSyncError?>(null);
        }

        public List<(int Tables, string? ModelHash, string? KnownTablesHash, string? KnownDefinitionsHash)> Take()
        {
            var taken = new List<(int, string?, string?, string?)>();
            while (_seen.TryDequeue(out var item)) taken.Add(item);
            return taken;
        }
    }
}
