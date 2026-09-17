using System.Text.Json;
using G9SyncData.Client.Model;
using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.IntegrationTests.Scopes;
using G9SyncData.Server.EfCore;
using G9SyncData.Server.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Writes;

/// <summary>
///     The EF Core row writer against a real database for the two shapes LES-0034 added: a JSON document an owned type is stored in
///     (written through the application's converter into the owned NAVIGATION, so SaveChanges sees a real object), and a complex type
///     split into the row (written into the complex property's scalars). The sync model is derived from the same EF model.
/// </summary>
public sealed class EfCoreJsonAndComplexWriteTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Json_documents_and_complex_properties_download_and_upload_through_the_applications_context()
    {
        var saved = new SavedTallies();
        await using var harness = await CreateHarnessAsync(saved);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        var existing = Guid.NewGuid();
        await harness.ExecServerAsync("""
            INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status", "Tally", "Dimensions_Width", "Dimensions_Length")
            VALUES ($1, $2, 'SRV-1', 0, '{"Total": 2, "Tags": ["server"]}', 10, 20)
            """, existing, site);
        await using var client = await SubscribedClientAsync(harness, site);

        // ---- download: the document as text, the complex property as its own columns
        var downloaded = JsonDocument.Parse((string)(await client.ScalarAsync("SELECT Tally FROM Pot WHERE Id = $p0", Key(existing)))!).RootElement;
        Assert.Equal(2, downloaded.GetProperty("Total").GetInt32());
        Assert.Equal(10L, await client.ScalarAsync("SELECT Dimensions_Width FROM Pot WHERE Id = $p0", Key(existing)));

        // ---- upload: an insert and an update, both through SaveChanges
        var created = Guid.NewGuid();
        await client.ExecAsync("""
            INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted, Tally, Dimensions_Width, Dimensions_Length)
            VALUES ($p0, $p1, 'DEV-1', 0, 0, '{"Total":5,"Tags":["a","b"]}', 3, 4)
            """, Key(created), Key(site));
        await client.ExecAsync("""UPDATE Pot SET Tally = '{"Total":9,"Tags":[]}', Dimensions_Width = 11 WHERE Id = $p0""", Key(existing));
        await client.SyncOkAsync();

        Assert.Equal(5, saved.TotalOf(created));   // the application saw an object, not a string
        Assert.Equal(9, saved.TotalOf(existing));
        Assert.Equal(5, await harness.ScalarServerAsync<int>("""SELECT ("Tally"->>'Total')::int FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.Equal("b", await harness.ScalarServerAsync<string>("""SELECT "Tally"->'Tags'->>1 FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.Equal(3, await harness.ScalarServerAsync<int>("""SELECT "Dimensions_Width" FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.Equal(4, await harness.ScalarServerAsync<int>("""SELECT "Dimensions_Length" FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.Equal(9, await harness.ScalarServerAsync<int>("""SELECT ("Tally"->>'Total')::int FROM farm."Pot" WHERE "Id" = $1""", existing));
        Assert.Equal(11, await harness.ScalarServerAsync<int>("""SELECT "Dimensions_Width" FROM farm."Pot" WHERE "Id" = $1""", existing));
        Assert.Equal(20, await harness.ScalarServerAsync<int>("""SELECT "Dimensions_Length" FROM farm."Pot" WHERE "Id" = $1""", existing));   // untouched
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        // ---- the canonical row the device got back is the server's document
        var canonical = JsonDocument.Parse((string)(await client.ScalarAsync("SELECT Tally FROM Pot WHERE Id = $p0", Key(created)))!).RootElement;
        Assert.Equal(5, canonical.GetProperty("Total").GetInt32());
        Assert.Equal(2, canonical.GetProperty("Tags").GetArrayLength());
    }

    [PostgresFact]
    public async Task A_document_the_converter_cannot_read_rejects_only_its_row()
    {
        var saved = new SavedTallies();
        await using var harness = await CreateHarnessAsync(saved);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await SubscribedClientAsync(harness, site);

        var broken = Guid.NewGuid();
        var fine = Guid.NewGuid();
        const string insert = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted, Tally, Dimensions_Width, Dimensions_Length) VALUES ($p0, $p1, $p2, 0, 0, $p3, 1, 1)";
        await client.ExecAsync(insert, Key(broken), Key(site), "BROKEN", "{not json");
        await client.ExecAsync(insert, Key(fine), Key(site), "FINE", """{"Total":1,"Tags":[]}""");

        var result = await client.SyncAsync();
        Assert.Equal(G9ESyncOutcome.CompletedWithRejections, result.Outcome);
        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal(Key(broken), rejection.Key);
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", broken));
        Assert.Equal(1, await harness.ScalarServerAsync<int>("""SELECT ("Tally"->>'Total')::int FROM farm."Pot" WHERE "Id" = $1""", fine));
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateHarnessAsync(SavedTallies saved) => SyncHarness.CreateAsync(output,
        seedBeforeCapture: async dataSource =>
        {
            await using var command = dataSource.CreateCommand("""
                ALTER TABLE farm."Pot" ADD COLUMN "Tally" jsonb NOT NULL DEFAULT '{"Total": 0, "Tags": []}';
                ALTER TABLE farm."Pot" ADD COLUMN "Dimensions_Width" integer NOT NULL DEFAULT 0;
                ALTER TABLE farm."Pot" ADD COLUMN "Dimensions_Length" integer NOT NULL DEFAULT 0;
                """);
            await command.ExecuteNonQueryAsync();
        },
        model: model =>
        {
            using var context = new JsonFarmDbContext(new DbContextOptionsBuilder<JsonFarmDbContext>().UseNpgsql(EfScopeFixture.NeverOpened).Options);
            model.FromEfCore(context.Model, ef =>
            {
                ef.Entity<JsonSite>();
                ef.Entity<JsonPot>(pot => pot.Conflicts(G9EConflictPolicy.ColumnMerge));
            });
            model
                .Subscription("sites", s => s.Include("farm.Site", "true", "{row}.\"IsActive\""))
                .Subscription("site-pots", s => s
                    .Parameter("SiteId", G9ESyncColumnType.Guid)
                    .Include("farm.Pot", "{row}.\"SiteId\" = @SiteId", "NOT {row}.\"IsDeleted\"")
                    .DependsOn("sites"));
        },
        server: builder =>
        {
            builder.Services.AddSingleton(saved);
            builder.Services.AddDbContext<JsonFarmDbContext>((services, options) =>
                options.UseNpgsql(EfScopeFixture.NeverOpened).AddInterceptors(services.GetRequiredService<SavedTallies>()));
            builder.WriteThroughEfCore<JsonFarmDbContext>(ef => ef
                .Entity<JsonPot>(pot => pot.Convert("Tally", json => JsonSerializer.Deserialize<JsonPotTally>((string)json!)
                                                                     ?? throw new JsonException("The tally document is null."))));
        });

    private static readonly G9CSyncClientModel ClientModel = new G9CSyncClientModelBuilder()
        .Table("farm.Site", "Site", t => t
            .Key("Id")
            .Column("Name", G9ESyncColumnType.String, nullable: false)
            .Column("IsActive", G9ESyncColumnType.Boolean, nullable: false))
        .Table("farm.Pot", "Pot", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("Weight", G9ESyncColumnType.Decimal)
            .Column("PlantedAt", G9ESyncColumnType.DateTime)
            .Column("Note", G9ESyncColumnType.String)
            .Column("IsDeleted", G9ESyncColumnType.Boolean, nullable: false)
            .Column("Tally", G9ESyncColumnType.String, nullable: false)
            .Column("Dimensions_Width", G9ESyncColumnType.Int32, nullable: false)
            .Column("Dimensions_Length", G9ESyncColumnType.Int32, nullable: false))
        .Build();

    private static async Task<SyncClient> SubscribedClientAsync(SyncHarness harness, Guid site)
    {
        var client = await harness.CreateClientAsync(clientModel: ClientModel);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        return client;
    }

    /// <summary>What the application's SaveChanges saw: the owned object's Total per pot.</summary>
    private sealed class SavedTallies : SaveChangesInterceptor
    {
        private readonly Dictionary<Guid, int> _totals = [];

        public int? TotalOf(Guid pot)
        {
            lock (_totals) return _totals.TryGetValue(pot, out var total) ? total : null;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<JsonPot>())
            {
                lock (_totals) _totals[entry.Entity.Id] = entry.Entity.Tally.Total;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}

internal sealed class JsonFarmDbContext(DbContextOptions<JsonFarmDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("farm");
        modelBuilder.Entity<JsonSite>().ToTable("Site");
        modelBuilder.Entity<JsonPot>(pot =>
        {
            pot.ToTable("Pot");
            pot.Property(p => p.Weight).HasColumnType("numeric(18,4)");
            pot.OwnsOne(p => p.Tally, tally => tally.ToJson());
            pot.Navigation(p => p.Tally).IsRequired();
            pot.ComplexProperty(p => p.Dimensions);
        });
    }
}

internal sealed class JsonSite
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}

internal sealed class JsonPot
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public string Code { get; set; } = "";
    public int Status { get; set; }
    public decimal? Weight { get; set; }
    public DateTime? PlantedAt { get; set; }
    public string? Note { get; set; }
    public bool IsDeleted { get; set; }
    public JsonPotTally Tally { get; set; } = new();
    public JsonPotDimensions Dimensions { get; set; } = new();
}

internal sealed class JsonPotTally
{
    public int Total { get; set; }
    public List<string> Tags { get; set; } = [];
}

internal sealed class JsonPotDimensions
{
    public int Width { get; set; }
    public int Length { get; set; }
}
