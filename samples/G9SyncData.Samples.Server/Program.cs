using G9SyncData.Samples.Server;
using Npgsql;

// G9SyncData sample server: PostgreSQL change capture served over SignalR.
// Database: ConnectionStrings:G9Sync, else G9SYNC_SAMPLE_PG, else a local disposable server. The sample creates and seeds
// its own database, so never point it at a shared one.
var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("G9Sync") is { Length: > 0 } configured
    ? configured
    : Environment.GetEnvironmentVariable("G9SYNC_SAMPLE_PG") ?? "Host=localhost;Port=15433;Username=postgres;Database=g9sync_sample";

await SampleDatabase.EnsureAsync(connectionString);
var dataSource = NpgsqlDataSource.Create(connectionString);

builder.Services.AddG9SyncDataServer(SampleDatabase.ConfigureModel)
    .UsePostgreSql(dataSource)
    .UseSignalR();

var app = builder.Build();
app.UseWebSockets();
app.MapG9SyncHub(); // A real deployment adds .RequireAuthorization() and an auth context provider.

app.MapGet("/", () => "G9SyncData sample server. Hub: /g9sync. POST /sample/pots?count=10 adds pots to every site and pokes connected clients.");

app.MapPost("/sample/pots", async (int? count) =>
{
    await using var command = dataSource.CreateCommand("""
        INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status")
        SELECT gen_random_uuid(), s."Id", 'N-' || substr(md5(random()::text), 1, 12), 0
        FROM farm."Site" AS s CROSS JOIN generate_series(1, $1)
        """);
    command.Parameters.Add(new NpgsqlParameter { Value = Math.Clamp(count ?? 10, 1, 100_000) });
    return Results.Text($"Inserted {await command.ExecuteNonQueryAsync()} pots.\n");
});

await app.RunAsync();
await dataSource.DisposeAsync();
