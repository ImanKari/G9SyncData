using G9SyncData.Client;
using G9SyncData.Client.Model;
using G9SyncData.Client.Runs;
using G9SyncData.Client.SignalR;
using G9SyncData.Client.Sqlite;
using G9SyncData.Client.Transport;
using G9SyncData.Server;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Model;
using G9SyncData.Server.Storage;
using G9SyncData.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using G9SyncData.Server.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace G9SyncData.IntegrationTests.Infrastructure;

public enum SyncTransportKind
{
    /// <summary>In-process calls with JSON round trips and fault injection.</summary>
    InMemory,

    /// <summary>Real SignalR over Kestrel on a loopback port, with the change listener and pokes running.</summary>
    SignalR
}

/// <summary>One throwaway PostgreSQL database with the server engine on top, plus any number of SQLite clients.</summary>
internal sealed class SyncHarness : IAsyncDisposable
{
    private readonly string _database;
    private readonly IAsyncDisposable _host;
    private readonly string? _hubUrl;

    /// <summary>Bytes on the real server's sockets (<see cref="SyncTransportKind.SignalR" /> only).</summary>
    public WireCounter? Wire { get; private init; }

    /// <summary>The SignalR hub URL when the harness runs a real server (<see cref="SyncTransportKind.SignalR" />); otherwise null.</summary>
    public string? HubUrl => _hubUrl;
    private readonly ITestOutputHelper _output;
    private readonly Action<G9CSyncServerOptions>? _configure;
    private readonly Action<G9CPostgresSyncOptions>? _postgres;
    private readonly List<SyncClient> _clients = [];
    private readonly List<ServiceProvider> _additionalServers = [];
    private readonly List<IHostedService> _startedConsumers = [];
    private readonly List<HarnessServer> _servers = [];
    private readonly List<string> _backups = [];

    private SyncHarness(string database, NpgsqlDataSource dataSource, IServiceProvider services, IAsyncDisposable host, string? hubUrl,
        ITestOutputHelper output, Action<G9CSyncServerOptions>? configure, bool usesWal, Action<G9CPostgresSyncOptions>? postgres)
    {
        _postgres = postgres;
        UsesWal = usesWal;
        _database = database;
        _host = host;
        _hubUrl = hubUrl;
        _output = output;
        _configure = configure;
        DataSource = dataSource;
        Services = services;
        Engine = services.GetRequiredService<G9CSyncServerEngine>();
        Store = services.GetRequiredService<IG9SyncServerStore>();
        Notifier = services.GetRequiredService<G9CSyncChangeNotifier>();
    }

    public NpgsqlDataSource DataSource { get; }
    public IServiceProvider Services { get; }
    public G9CSyncServerEngine Engine { get; }
    public IG9SyncServerStore Store { get; }
    public G9CSyncChangeNotifier Notifier { get; }

    /// <summary>True when this harness captures through the WAL consumer (ADR-0028) rather than triggers.</summary>
    public bool UsesWal { get; }

    /// <param name="output">Test output for library logs.</param>
    /// <param name="configure">Server options.</param>
    /// <param name="transport">In-memory or real SignalR.</param>
    /// <param name="model">Server model; default <see cref="TestSchema.ConfigureServer(G9CSyncModelBuilder)" />.</param>
    /// <param name="server">Extra server registrations (auth provider, pipeline hooks, gates).</param>
    /// <param name="seedBeforeCapture">Runs after the schema exists and before capture is installed: rows it writes have no tracking history.</param>
    /// <param name="walCapture">Forces a capture mode for this test; null follows <c>G9SYNC_TEST_CAPTURE</c>.</param>
    /// <param name="postgres">PostgreSQL store options, applied after the harness's own.</param>
    public static async Task<SyncHarness> CreateAsync(ITestOutputHelper output, Action<G9CSyncServerOptions>? configure = null,
        SyncTransportKind transport = SyncTransportKind.InMemory, Action<G9CSyncModelBuilder>? model = null, Action<G9CSyncServerBuilder>? server = null,
        Func<NpgsqlDataSource, Task>? seedBeforeCapture = null, bool? walCapture = null, Action<G9CPostgresSyncOptions>? postgres = null)
    {
        var useWal = walCapture ?? PgTestServer.UseWal;
        var database = "g9t_" + Guid.NewGuid().ToString("N")[..20];
        await using (var admin = new NpgsqlConnection(PgTestServer.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database} TEMPLATE template0", admin);
            await create.ExecuteNonQueryAsync();
        }

        var dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(PgTestServer.AdminConnectionString) { Database = database }.ConnectionString);
        SyncHarness? harness = null;
        try
        {
            await using (var schema = dataSource.CreateCommand(TestSchema.Sql))
            {
                await schema.ExecuteNonQueryAsync();
            }

            if (seedBeforeCapture is not null) await seedBeforeCapture(dataSource);

            if (transport == SyncTransportKind.InMemory)
            {
                var services = new ServiceCollection();
                services.AddLogging(logging => logging.AddProvider(new XunitLoggerProvider(output)).SetMinimumLevel(LogLevel.Information));
                var serverBuilder = AddServer(services, dataSource, database, configure, notifications: false, model, useWal, postgres);
                server?.Invoke(serverBuilder);
                var provider = services.BuildServiceProvider();
                harness = new SyncHarness(database, dataSource, provider, provider, hubUrl: null, output, configure, useWal, postgres);

                // No host runs here, so the WAL consumer is started by hand - and only the consumer: the other hosted
                // service's cleanup loop would change v1 behaviour the in-memory tests rely on.
                harness._startedConsumers.AddRange(provider.GetServices<IHostedService>().Where(h => h.GetType().Name == "G9CWalCaptureService"));
                foreach (var consumer in harness._startedConsumers) await consumer.StartAsync(CancellationToken.None);
            }
            else
            {
                var builder = WebApplication.CreateSlimBuilder();
                var wire = new WireCounter();
                builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.Use(wire.Middleware)));
                builder.Logging.ClearProviders().AddProvider(new XunitLoggerProvider(output)).SetMinimumLevel(LogLevel.Information);
                var serverBuilder = AddServer(builder.Services, dataSource, database, configure, notifications: true, model, useWal, postgres)
                    .UseSignalR(o => o.PokeCoalescence = TimeSpan.FromMilliseconds(100));
                server?.Invoke(serverBuilder);

                var app = builder.Build();
                app.UseWebSockets();
                app.MapG9SyncHub();
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
                harness = new SyncHarness(database, dataSource, app.Services, new WebHostLifetime(app), address.TrimEnd('/') + "/g9sync", output, configure, useWal, postgres)
                {
                    Wire = wire
                };
            }

            await harness.Engine.EnsureInitializedAsync();

            // In WAL mode nothing may be written before the slot exists and the consumer is streaming: a change committed
            // before the slot is created is never decoded. A confirmed sync point proves both.
            await harness.WaitForCaptureAsync();
            return harness;
        }
        catch
        {
            // A harness that fails to start must not leave its database behind.
            if (harness is not null) await harness.DisposeAsync();
            else await DropDatabaseAsync(database, dataSource);
            throw;
        }
    }

    private static G9CSyncServerBuilder AddServer(IServiceCollection services, NpgsqlDataSource dataSource, string database,
        Action<G9CSyncServerOptions>? configure, bool notifications, Action<G9CSyncModelBuilder>? model, bool useWal,
        Action<G9CPostgresSyncOptions>? postgres) =>
        services.AddG9SyncDataServer(model ?? TestSchema.ConfigureServer, options =>
            {
                options.InitializeOnStartup = false;
                options.EnableChangeNotifications = notifications;
                options.PageRows = 50;
                configure?.Invoke(options);
            })
            .UsePostgreSql(dataSource, options =>
            {
                if (useWal)
                {
                    options.CaptureMode = G9EPostgresCaptureMode.WriteAheadLog;
                    options.Wal.SlotName = database;          // slots are cluster-wide: one per test database
                    options.Wal.PublicationName = database;
                    options.Wal.ReplicationConnectionString =
                        new NpgsqlConnectionStringBuilder(PgTestServer.AdminConnectionString) { Database = database, Pooling = false }.ConnectionString;
                    options.Wal.RetryDelay = TimeSpan.FromMilliseconds(200);
                }

                postgres?.Invoke(options);
            });

    /// <summary>
    ///     WAL mode: returns once the consumer has recorded every transaction committed so far. Trigger mode: returns at once,
    ///     because triggers record changes inside the writer's own transaction. Called after every harness write, so tests
    ///     read the same "committed means tracked" guarantee in both modes.
    /// </summary>
    public Task WaitForCaptureAsync() =>
        UsesWal
            ? Services.GetRequiredService<G9CPostgresSyncStore>().WaitForCaptureAsync(TimeSpan.FromSeconds(30))
            : Task.CompletedTask;

    /// <summary>Stops the WAL consumer (in-memory harness only): changes by other writers then stay unrecorded.</summary>
    public async Task StopCaptureAsync()
    {
        foreach (var consumer in _startedConsumers) await consumer.StopAsync(CancellationToken.None);
    }

    /// <summary>Restarts the WAL consumer stopped by <see cref="StopCaptureAsync" />.</summary>
    public async Task StartCaptureAsync()
    {
        foreach (var consumer in _startedConsumers) await consumer.StartAsync(CancellationToken.None);
    }

    /// <summary>The slot name this harness's consumer uses (its database name).</summary>
    public string SlotName => _database;

    /// <summary>The tracking version of a pot, or null when the pot has no tracking row.</summary>
    public async Task<long?> PotTrackingVersionAsync(Guid pot)
    {
        var tracking = "g9sync." + G9SyncData.Server.PostgreSql.Internal.PgNames.Quote(G9SyncData.Server.PostgreSql.Internal.PgNames.TrackingBare("farm", "Pot"));
        await using var command = DataSource.CreateCommand($"SELECT ver FROM {tracking} WHERE \"Id\" = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = pot });
        return await command.ExecuteScalarAsync() is long version ? version : null;
    }

    /// <summary>A second server engine on the same database — e.g. the next deployment with a changed model.</summary>
    public async Task<G9CSyncServerEngine> CreateAdditionalServerAsync(Action<G9CSyncModelBuilder> model, Action<G9CSyncServerBuilder>? server = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new XunitLoggerProvider(_output)).SetMinimumLevel(LogLevel.Information));
        var serverBuilder = AddServer(services, DataSource, _database, _configure, notifications: false, model, UsesWal, _postgres);
        server?.Invoke(serverBuilder);
        var provider = services.BuildServiceProvider();
        _additionalServers.Add(provider);
        var engine = provider.GetRequiredService<G9CSyncServerEngine>();
        await engine.EnsureInitializedAsync();
        return engine;
    }

    /// <summary>
    ///     "Backs up" this database as it is now: a <c>CREATE DATABASE … TEMPLATE</c> copy, which is what a restore produces as far as
    ///     sync is concerned — the same rows, tracking and install row, under a different database. Capture pauses meanwhile (a
    ///     template must have no other sessions). The copy is dropped with the harness.
    /// </summary>
    public async Task<string> BackupAsync()
    {
        var backup = _database + "_b" + (_backups.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await StopCaptureAsync();
        foreach (var server in _servers.Where(server => server.Database == _database)) await server.StopCaptureAsync();
        try
        {
            DataSource.Clear();
            await using var admin = new NpgsqlConnection(PgTestServer.AdminConnectionString);
            await admin.OpenAsync();
            for (var attempt = 0; ; attempt++)
            {
                await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid()", admin))
                {
                    terminate.Parameters.Add(new NpgsqlParameter { Value = _database });
                    await terminate.ExecuteNonQueryAsync();
                }

                try
                {
                    await using var create = new NpgsqlCommand($"CREATE DATABASE {backup} TEMPLATE {_database}", admin);
                    await create.ExecuteNonQueryAsync();
                    break;
                }
                catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ObjectInUse && attempt < 50)
                {
                    await Task.Delay(100);
                }
            }

            _backups.Add(backup);
            return backup;
        }
        finally
        {
            await StartCaptureAsync();
            foreach (var server in _servers.Where(server => server.Database == _database)) await server.StartCaptureAsync();
        }
    }

    /// <summary>
    ///     Starts a server engine — with its own WAL consumer in WAL mode — on <paramref name="database" /> (default: this harness's):
    ///     a restored backup, or this database after its capture was reinstalled. Stop this harness's own capture first when both
    ///     would share a slot.
    /// </summary>
    public async Task<HarnessServer> StartServerAsync(string? database = null, Action<G9CSyncModelBuilder>? model = null, Action<G9CSyncServerBuilder>? server = null,
        Action<G9CSyncServerOptions>? configure = null)
    {
        database ??= _database;
        var dataSource = database == _database
            ? DataSource
            : NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(PgTestServer.AdminConnectionString) { Database = database }.ConnectionString);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new XunitLoggerProvider(_output)).SetMinimumLevel(LogLevel.Information));
        var serverBuilder = AddServer(services, dataSource, database, options =>
        {
            _configure?.Invoke(options);
            configure?.Invoke(options);
        }, notifications: false, model, UsesWal, _postgres);
        server?.Invoke(serverBuilder);
        var provider = services.BuildServiceProvider();
        var started = new HarnessServer(database, dataSource, database != _database, provider,
            provider.GetServices<IHostedService>().Where(h => h.GetType().Name == "G9CWalCaptureService").ToList(), UsesWal);
        _servers.Add(started);
        await started.StartCaptureAsync();
        await started.Engine.EnsureInitializedAsync();
        await started.WaitForCaptureAsync();
        return started;
    }

    /// <param name="configure">Engine options.</param>
    /// <param name="caller">Identity the in-memory transport presents.</param>
    /// <param name="prepareDatabase">Writes the SQLite file before the store opens it (simulates data from an earlier sync system).</param>
    /// <param name="store">Store options (e.g. a revocation policy).</param>
    /// <param name="clientModel">The device model; default <see cref="TestSchema.Client" />.</param>
    /// <param name="transportFactory">A transport of the test's own (e.g. routing calls across server instances); default in-memory or SignalR.</param>
    /// <param name="protocol">The hub protocol a SignalR client speaks.</param>
    public async Task<SyncClient> CreateClientAsync(Action<G9CSyncEngineOptions>? configure = null, G9CSyncCaller? caller = null,
        Func<string, Task>? prepareDatabase = null, Action<G9CSqliteSyncStoreOptions>? store = null, Func<IG9SyncTransport>? transportFactory = null,
        G9CSyncClientModel? clientModel = null, G9ESyncHubProtocol protocol = G9ESyncHubProtocol.MessagePack)
    {
        var directory = Path.Combine(Path.GetTempPath(), "g9sync-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        if (prepareDatabase is not null)
        {
            await prepareDatabase(path);
            ClearPoolOf(path);
        }

        var loggers = Services.GetRequiredService<ILoggerFactory>();
        var storeOptions = new G9CSqliteSyncStoreOptions { DatabasePath = path };
        store?.Invoke(storeOptions);
        var sqliteStore = new G9CSqliteSyncStore(storeOptions, loggers.CreateLogger<G9CSqliteSyncStore>());
        IG9SyncTransport transport = transportFactory?.Invoke() ?? (_hubUrl is null
            ? new G9CInMemorySyncTransport(Engine, Notifier, caller)
            : new G9CSignalRSyncTransport(new G9CSignalRSyncTransportOptions { Url = _hubUrl, Protocol = protocol }, loggers.CreateLogger<G9CSignalRSyncTransport>()));
        var options = new G9CSyncEngineOptions
        {
            AppVersion = "integration-tests",
            RetryBaseDelay = TimeSpan.FromMilliseconds(20),
            PullOnChangesAvailable = false
        };
        configure?.Invoke(options);
        var engine = new G9CSyncEngine(sqliteStore, transport, clientModel ?? TestSchema.Client, options, loggers.CreateLogger<G9CSyncEngine>());
        await engine.InitializeAsync();

        var client = new SyncClient(path, sqliteStore, transport, engine);
        _clients.Add(client);
        return client;
    }

    /// <summary>
    ///     Releases pooled connections of ONE database file. Tests run in parallel: <c>SqliteConnection.ClearAllPools()</c> would dispose
    ///     connections other tests' stores are using at that moment (LES-0030).
    /// </summary>
    public static void ClearPoolOf(string path)
    {
        // The store's connection string (G9CSqliteSyncStoreOptions.BuildConnectionString) and the test client's own.
        foreach (var connectionString in new[] { "Data Source=" + path + ";Pooling=True", "Data Source=" + path })
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }
    }

    /// <summary>A client subscribed to <c>sites</c> and one site's pots, after a successful first sync.</summary>
    public async Task<SyncClient> CreateSubscribedClientAsync(Guid site, Action<G9CSyncEngineOptions>? configure = null)
    {
        var client = await CreateClientAsync(configure);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", TestSchema.SitePots(site));
        await client.SyncOkAsync();
        return client;
    }

    // ================================================================ server data

    public async Task<int> ExecServerAsync(string sql, params object?[] args)
    {
        int affected;
        await using (var command = DataSource.CreateCommand(sql))
        {
            foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
            affected = await command.ExecuteNonQueryAsync();
        }

        await WaitForCaptureAsync();
        return affected;
    }

    public async Task<T?> ScalarServerAsync<T>(string sql, params object?[] args)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public Task InsertSiteAsync(Guid id, string name, bool active = true) =>
        ExecServerAsync("""INSERT INTO farm."Site" ("Id", "Name", "IsActive") VALUES ($1, $2, $3)""", id, name, active);

    /// <summary>Inserts pots with codes P00001, P00002, ...</summary>
    public Task InsertPotsAsync(Guid siteId, int count, string prefix = "P") =>
        ExecServerAsync("""
            INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status")
            SELECT gen_random_uuid(), $1, $2 || lpad(g::text, 5, '0'), g % 5 FROM generate_series(1, $3) AS g
            """, siteId, prefix, count);

    public Task InsertPotAsync(Guid id, Guid siteId, string code, bool isDeleted = false) =>
        ExecServerAsync("""INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status", "IsDeleted") VALUES ($1, $2, $3, 0, $4)""", id, siteId, code, isDeleted);

    /// <summary>Ids of a site's generated pots, in code order.</summary>
    public async Task<List<Guid>> PotIdsAsync(Guid siteId)
    {
        var ids = new List<Guid>();
        await using var command = DataSource.CreateCommand("""SELECT "Id" FROM farm."Pot" WHERE "SiteId" = $1 AND "Code" LIKE 'P%' ORDER BY "Code" """);
        command.Parameters.Add(new NpgsqlParameter { Value = siteId });
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>The database is dropped even when a client or the host fails to dispose, so a failing test leaves nothing behind.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var client in _clients) await client.DisposeAsync();
            foreach (var consumer in _startedConsumers) await consumer.StopAsync(CancellationToken.None);
            foreach (var server in _servers) await server.DisposeAsync();
            await _host.DisposeAsync();
            foreach (var provider in _additionalServers) await provider.DisposeAsync();
        }
        finally
        {
            // A logical slot belongs to its database and blocks DROP DATABASE; a leaked one would also retain WAL forever.
            foreach (var backup in _backups)
            {
                if (UsesWal) await DropSlotAsync(backup);
                await DropDatabaseAsync(backup, null);
            }

            if (UsesWal) await DropSlotAsync(_database);
            await DropDatabaseAsync(_database, DataSource);
        }
    }

    private static async Task DropSlotAsync(string slot)
    {
        await using var admin = new NpgsqlConnection(PgTestServer.AdminConnectionString);
        await admin.OpenAsync();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using (var terminate = new NpgsqlCommand(
                             "SELECT pg_terminate_backend(active_pid) FROM pg_replication_slots WHERE slot_name = $1 AND active", admin))
            {
                terminate.Parameters.Add(new NpgsqlParameter { Value = slot });
                await terminate.ExecuteNonQueryAsync();
            }

            try
            {
                await using var drop = new NpgsqlCommand("SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name = $1", admin);
                drop.Parameters.Add(new NpgsqlParameter { Value = slot });
                await drop.ExecuteNonQueryAsync();
                return;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ObjectInUse)
            {
                await Task.Delay(100);   // the walsender has not exited yet
            }
        }
    }

    private static async Task DropDatabaseAsync(string database, NpgsqlDataSource? dataSource)
    {
        if (dataSource is not null) await dataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(PgTestServer.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private sealed class WebHostLifetime(WebApplication app) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

/// <summary>A server engine started by <see cref="SyncHarness.StartServerAsync" />: its services, engine and capture consumer.</summary>
internal sealed class HarnessServer(string database, NpgsqlDataSource dataSource, bool ownsDataSource, ServiceProvider services,
    IReadOnlyList<IHostedService> consumers, bool usesWal) : IAsyncDisposable
{
    private bool _capturing;

    public string Database { get; } = database;
    public NpgsqlDataSource DataSource { get; } = dataSource;
    public IServiceProvider Services => services;
    public G9CSyncServerEngine Engine { get; } = services.GetRequiredService<G9CSyncServerEngine>();
    public G9CPostgresSyncStore Store { get; } = services.GetRequiredService<G9CPostgresSyncStore>();

    public async Task StartCaptureAsync()
    {
        if (_capturing) return;
        foreach (var consumer in consumers) await consumer.StartAsync(CancellationToken.None);
        _capturing = true;
    }

    public async Task StopCaptureAsync()
    {
        if (!_capturing) return;
        foreach (var consumer in consumers) await consumer.StopAsync(CancellationToken.None);
        _capturing = false;
    }

    public Task WaitForCaptureAsync() => usesWal ? Store.WaitForCaptureAsync(TimeSpan.FromSeconds(30)) : Task.CompletedTask;

    public async Task<T?> ScalarAsync<T>(string sql, params object?[] args)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public async Task ExecAsync(string sql, params object?[] args)
    {
        await using (var command = DataSource.CreateCommand(sql))
        {
            foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
            await command.ExecuteNonQueryAsync();
        }

        await WaitForCaptureAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopCaptureAsync();
        await services.DisposeAsync();
        if (ownsDataSource) await DataSource.DisposeAsync();
    }
}

/// <summary>A device: its SQLite file, store, transport and engine, plus helpers that write like the app does.</summary>
internal sealed class SyncClient(string path, G9CSqliteSyncStore store, IG9SyncTransport transport, G9CSyncEngine engine) : IAsyncDisposable
{
    private int _disposed;

    public string DatabasePath { get; } = path;
    public G9CSqliteSyncStore Store { get; } = store;
    public IG9SyncTransport Transport { get; } = transport;
    public G9CSyncEngine Engine { get; } = engine;

    /// <summary>The in-memory transport (fault injection, call counts, server swaps). Throws for a SignalR client.</summary>
    public G9CInMemorySyncTransport InMemory =>
        Transport as G9CInMemorySyncTransport ?? throw new InvalidOperationException("This client uses a real SignalR transport.");

    public Task<G9CSyncRunResult> SyncAsync(G9CSyncRequest? request = null) => Engine.SyncAsync(request ?? new G9CSyncRequest());

    /// <summary>Syncs and asserts a clean success, reporting the error and subscription problems otherwise.</summary>
    public async Task<G9CSyncRunResult> SyncOkAsync(G9CSyncRequest? request = null)
    {
        var result = await SyncAsync(request);
        Assert.True(result.Outcome == G9ESyncOutcome.Succeeded, Describe(result));
        return result;
    }

    /// <summary>
    ///     Syncs until a run succeeds, tolerating the conservative ResyncRequired a freshly purged tombstone may report while other tests'
    ///     transactions are in flight (xids are cluster-wide, LES-0029). Anything else fails at once.
    /// </summary>
    public async Task<G9CSyncRunResult> SyncUntilSettledAsync(G9CSyncRequest? request = null, int attempts = 4)
    {
        G9CSyncRunResult result = null!;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            result = await SyncAsync(request);
            if (result.Outcome == G9ESyncOutcome.Succeeded) return result;
            Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections
                        && result.Problems.Count > 0 && result.Problems.All(p => p.Action == G9SyncData.Protocol.G9ESubscriptionAction.ResyncRequired),
                Describe(result));
        }

        Assert.Fail("Still ResyncRequired after " + attempts + " runs: " + Describe(result));
        return result;
    }

    public static string Describe(G9CSyncRunResult result) =>
        $"Outcome {result.Outcome}; error {result.Error?.Code}: {result.Error?.Message} {result.Error?.Details}; " +
        $"problems [{string.Join("; ", result.Problems.Select(p => $"{p.Key} {p.Action} {p.Message}"))}]; " +
        $"pushed {result.Pushed}, rejected {result.Rejected}, conflicts {result.Conflicts}, downloaded {result.Downloaded}";

    public async Task<int> ExecAsync(string sql, params object?[] args)
    {
        await using var connection = await OpenAsync();
        await using var command = Prepare(connection, sql, args);
        return await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> ScalarAsync(string sql, params object?[] args)
    {
        await using var connection = await OpenAsync();
        await using var command = Prepare(connection, sql, args);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public async Task<long> CountAsync(string table) => (long)(await ScalarAsync($"SELECT count(*) FROM [{table}]"))!;

    public async Task<bool> ExistsAsync(string table, Guid id) =>
        (long)(await ScalarAsync($"SELECT count(*) FROM [{table}] WHERE Id = $p0", TestSchema.Key(id)))! == 1;

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=" + DatabasePath);
        await connection.OpenAsync();
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 30000";
        await pragma.ExecuteNonQueryAsync();
        return connection;
    }

    private static SqliteCommand Prepare(SqliteConnection connection, string sql, object?[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return command;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Engine.DisposeAsync();
        await Transport.DisposeAsync();
        await Store.DisposeAsync();
        SyncHarness.ClearPoolOf(DatabasePath);
        foreach (var file in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A late handle; the temp folder is disposable.
            }
        }
    }
}
