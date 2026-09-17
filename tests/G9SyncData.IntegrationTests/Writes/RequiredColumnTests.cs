using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.IntegrationTests.Scopes;
using G9SyncData.Server.EfCore;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Writes;

/// <summary>
///     A NOT NULL column without a default that devices cannot write: fatal for uploads through the store's SQL, fine when an
///     application row writer supplies it (AgriPad's <c>AttachmentCount</c>, filled by EF Core, found by the pilot - LES-0034).
/// </summary>
public sealed class RequiredColumnTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_required_column_uploads_cannot_fill_stops_the_server_at_startup()
    {
        SyncHarness? started = null;
        var error = await Assert.ThrowsAsync<G9CSyncException>(async () => started = await SyncHarness.CreateAsync(output, seedBeforeCapture: AddRequiredStampAsync));
        if (started is not null) await started.DisposeAsync();

        Assert.Equal(G9SyncErrorCodes.ModelInvalid, error.Code);
        Assert.Contains("farm.Pot.Stamp is NOT NULL without a default", error.Message, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task A_row_writer_that_supplies_a_required_column_owns_the_check_and_the_value()
    {
        await using var harness = await SyncHarness.CreateAsync(output, seedBeforeCapture: AddRequiredStampAsync,
            server: builder => builder.AddRowWriter(new StampingPotWriter(("Stamp", "writer"))));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);

        var pot = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'NEW-1', 0, 0)", Key(pot), Key(site));
        await client.SyncOkAsync();
        Assert.Equal("writer", await harness.ScalarServerAsync<string>("""SELECT "Stamp" FROM farm."Pot" WHERE "Id" = $1""", pot));

        await client.ExecAsync("UPDATE Pot SET Status = 2 WHERE Id = $p0", Key(pot));
        await client.SyncOkAsync();
        Assert.Equal(2, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    [PostgresFact]
    public async Task Rows_a_writer_returns_carry_every_column_the_device_holds_including_server_owned_ones()
    {
        // AgriPad's pilot: a read-only (server-owned) column plus a row writer made every push fail on the device with
        // "the frame has 12 columns, 13 were negotiated" - canonical rows were built in the PUSH shape (LES-0035).
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(PotNoteReadOnly: true)),
            server: builder => builder.AddRowWriter(new StampingPotWriter(("Note", "server-owned"))));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);

        var pot = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, Note, IsDeleted) VALUES ($p0, $p1, 'NEW-1', 0, 'device value', 0)", Key(pot), Key(site));
        await client.SyncOkAsync();

        Assert.Equal("server-owned", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal("server-owned", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pot)));   // applied from the canonical row
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task An_insert_through_EF_Core_saves_the_owned_json_document_its_constructor_created()
    {
        // The pilot's AttachmentCount: jsonb NOT NULL, server-owned, never sent by devices; EF must write the entity's default.
        await using var harness = await SyncHarness.CreateAsync(output,
            seedBeforeCapture: async dataSource =>
            {
                await using var command = dataSource.CreateCommand("""
                    ALTER TABLE farm."Pot" ADD COLUMN "Tally" jsonb NOT NULL DEFAULT '{}';
                    ALTER TABLE farm."Pot" ALTER COLUMN "Tally" DROP DEFAULT;
                    """);
                await command.ExecuteNonQueryAsync();
            },
            server: builder =>
            {
                builder.Services.AddDbContext<TalliedFarmDbContext>(options => options.UseNpgsql(EfScopeFixture.NeverOpened));
                builder.WriteThroughEfCore<TalliedFarmDbContext>(ef => ef.Entity<TalliedPot>());
            });
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);

        var pot = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'NEW-1', 0, 0)", Key(pot), Key(site));
        await client.SyncOkAsync();

        Assert.Equal(7, await harness.ScalarServerAsync<int>("""SELECT ("Tally"->>'Total')::int FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    private static async Task AddRequiredStampAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            ALTER TABLE farm."Pot" ADD COLUMN "Stamp" text NOT NULL DEFAULT '';
            ALTER TABLE farm."Pot" ALTER COLUMN "Stamp" DROP DEFAULT;
            """);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>An application writer that owns a column devices never send: it sets it on every insert.</summary>
    private sealed class StampingPotWriter((string Column, string Value) owned) : IG9RowWriter
    {
        public bool AppliesTo(G9CSyncServerTable table) => table.LogicalName == "farm.Pot";

        public async ValueTask WriteAsync(G9CRowWriteContext context, IReadOnlyList<G9CRowWrite> rows, CancellationToken cancellationToken)
        {
            foreach (var row in rows)
            {
                await using var command = new NpgsqlCommand { Connection = (NpgsqlConnection)context.Connection, Transaction = (NpgsqlTransaction)context.Transaction };
                command.Parameters.Add(new NpgsqlParameter("id", row.KeyValue));
                var columns = row.Values.Keys.ToList();
                for (var i = 0; i < columns.Count; i++) command.Parameters.Add(new NpgsqlParameter("c" + i, row.Values[columns[i]] ?? DBNull.Value));

                command.CommandText = row switch
                {
                    { Op: G9ERowWriteOp.Delete } => """DELETE FROM farm."Pot" WHERE "Id" = @id""",
                    { Exists: false } => $"""INSERT INTO farm."Pot" ("Id", {string.Concat(columns.Select(c => $"\"{c}\", "))}"{owned.Column}") VALUES (@id, {string.Concat(columns.Select((_, i) => $"@c{i}, "))}'{owned.Value}')""",
                    _ => $"""UPDATE farm."Pot" SET {string.Join(", ", columns.Select((c, i) => $"\"{c}\" = @c{i}"))} WHERE "Id" = @id"""
                };
                if (row.Exists && row.Op != G9ERowWriteOp.Delete && columns.Count == 0) continue;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }
}

/// <summary>farm."Pot" with a server-owned JSON document, as an application with owned types would model it.</summary>
internal sealed class TalliedFarmDbContext(DbContextOptions<TalliedFarmDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("farm");
        modelBuilder.Entity<TalliedPot>(pot =>
        {
            pot.ToTable("Pot");
            pot.Property(p => p.Weight).HasColumnType("numeric(18,4)");
            pot.OwnsOne(p => p.Tally, tally => tally.ToJson());
            pot.Navigation(p => p.Tally).IsRequired();
        });
    }
}

internal sealed class TalliedPot
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public string Code { get; set; } = "";
    public int Status { get; set; }
    public decimal? Weight { get; set; }
    public DateTime? PlantedAt { get; set; }
    public string? Note { get; set; }
    public bool IsDeleted { get; set; }
    public PotTally Tally { get; set; } = new() { Total = 7 };
}

internal sealed class PotTally
{
    public int Total { get; set; }
}
