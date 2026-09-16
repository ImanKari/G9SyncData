using G9SyncData.Client;
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
using Microsoft.Extensions.DependencyInjection;
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
    private readonly ITestOutputHelper _output;
    private readonly Action<G9CSyncServerOptions>? _configure;
    private readonly List<SyncClient> _clients = [];
    private readonly List<ServiceProvider> _additionalServers = [];

    private SyncHarness(string database, NpgsqlDataSource dataSource, IServiceProvider services, IAsyncDisposable host, string? hubUrl,
        ITestOutputHelper output, Action<G9CSyncServerOptions>? configure)
    {
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

    /// <param name="output">Test output for library logs.</param>
    /// <param name="configure">Server options.</param>
    /// <param name="transport">In-memory or real SignalR.</param>
    /// <param name="model">Server model; default <see cref="TestSchema.ConfigureServer(G9CSyncModelBuilder)" />.</param>
    /// <param name="server">Extra server registrations (auth provider, pipeline hooks, gates).</param>
    /// <param name="seedBeforeCapture">Runs after the schema exists and before capture is installed: rows it writes have no tracking history.</param>
    public static async Task<SyncHarness> CreateAsync(ITestOutputHelper output, Action<G9CSyncServerOptions>? configure = null,
        SyncTransportKind transport = SyncTransportKind.InMemory, Action<G9CSyncModelBuilder>? model = null, Action<G9CSyncServerBuilder>? server = null,
        Func<NpgsqlDataSource, Task>? seedBeforeCapture = null)
    {
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
                var serverBuilder = AddServer(services, dataSource, configure, notifications: false, model);
                server?.Invoke(serverBuilder);
                var provider = services.BuildServiceProvider();
                harness = new SyncHarness(database, dataSource, provider, provider, hubUrl: null, output, configure);
            }
            else
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders().AddProvider(new XunitLoggerProvider(output)).SetMinimumLevel(LogLevel.Information);
                var serverBuilder = AddServer(builder.Services, dataSource, configure, notifications: true, model)
                    .UseSignalR(o => o.PokeCoalescence = TimeSpan.FromMilliseconds(100));
                server?.Invoke(serverBuilder);

                var app = builder.Build();
                app.UseWebSockets();
                app.MapG9SyncHub();
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
                harness = new SyncHarness(database, dataSource, app.Services, new WebHostLifetime(app), address.TrimEnd('/') + "/g9sync", output, configure);
            }

            await harness.Engine.EnsureInitializedAsync();
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

    private static G9CSyncServerBuilder AddServer(IServiceCollection services, NpgsqlDataSource dataSource, Action<G9CSyncServerOptions>? configure,
        bool notifications, Action<G9CSyncModelBuilder>? model) =>
        services.AddG9SyncDataServer(model ?? TestSchema.ConfigureServer, options =>
            {
                options.InitializeOnStartup = false;
                options.EnableChangeNotifications = notifications;
                options.PageRows = 50;
                configure?.Invoke(options);
            })
            .UsePostgreSql(dataSource);

    /// <summary>A second server engine on the same database — e.g. the next deployment with a changed model.</summary>
    public async Task<G9CSyncServerEngine> CreateAdditionalServerAsync(Action<G9CSyncModelBuilder> model, Action<G9CSyncServerBuilder>? server = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(new XunitLoggerProvider(_output)).SetMinimumLevel(LogLevel.Information));
        var serverBuilder = AddServer(services, DataSource, _configure, notifications: false, model);
        server?.Invoke(serverBuilder);
        var provider = services.BuildServiceProvider();
        _additionalServers.Add(provider);
        var engine = provider.GetRequiredService<G9CSyncServerEngine>();
        await engine.EnsureInitializedAsync();
        return engine;
    }

    /// <param name="configure">Engine options.</param>
    /// <param name="caller">Identity the in-memory transport presents.</param>
    /// <param name="prepareDatabase">Writes the SQLite file before the store opens it (simulates data from an earlier sync system).</param>
    public async Task<SyncClient> CreateClientAsync(Action<G9CSyncEngineOptions>? configure = null, G9CSyncCaller? caller = null,
        Func<string, Task>? prepareDatabase = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "g9sync-tests");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
        if (prepareDatabase is not null)
        {
            await prepareDatabase(path);
            SqliteConnection.ClearAllPools();
        }

        var loggers = Services.GetRequiredService<ILoggerFactory>();
        var store = new G9CSqliteSyncStore(new G9CSqliteSyncStoreOptions { DatabasePath = path }, loggers.CreateLogger<G9CSqliteSyncStore>());
        IG9SyncTransport transport = _hubUrl is null
            ? new G9CInMemorySyncTransport(Engine, Notifier, caller)
            : new G9CSignalRSyncTransport(new G9CSignalRSyncTransportOptions { Url = _hubUrl }, loggers.CreateLogger<G9CSignalRSyncTransport>());
        var options = new G9CSyncEngineOptions
        {
            AppVersion = "integration-tests",
            RetryBaseDelay = TimeSpan.FromMilliseconds(20),
            PullOnChangesAvailable = false
        };
        configure?.Invoke(options);
        var engine = new G9CSyncEngine(store, transport, TestSchema.Client, options, loggers.CreateLogger<G9CSyncEngine>());
        await engine.InitializeAsync();

        var client = new SyncClient(path, store, transport, engine);
        _clients.Add(client);
        return client;
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
        await using var command = DataSource.CreateCommand(sql);
        foreach (var arg in args) command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync();
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
            await _host.DisposeAsync();
            foreach (var provider in _additionalServers) await provider.DisposeAsync();
        }
        finally
        {
            await DropDatabaseAsync(_database, DataSource);
        }
    }

    private static async Task DropDatabaseAsync(string database, NpgsqlDataSource dataSource)
    {
        await dataSource.DisposeAsync();
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
        SqliteConnection.ClearAllPools();
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
