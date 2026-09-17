using System.Globalization;
using G9SyncData.Client.Model;
using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Model;
using G9SyncData.Testing;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Keys;

/// <summary>
///     Keys that are not GUIDs, end to end: text keys that differ only by case, contain separators, quotes, non-Latin scripts and
///     emoji; 32-bit keys at the edges; 64-bit keys beyond double precision (2^53 + 1). Every path a key travels — snapshot, delta,
///     push insert/update/delete, tombstones, reconciliation digests computed in C# and in SQL — must keep it EXACT. And a computed
///     column, whose value can change without its row's version changing, is repaired by reconciliation.
/// </summary>
public sealed class KeyTypesEndToEndTests(ITestOutputHelper output)
{
    private static readonly string[] TextKeys =
    [
        "alpha", "ALPHA", "Alpha", "with space", " padded ", "quote'd \"both\"", "a:b|c;d,e", "tab\tnewline\n", "سلام دنیا", "日本語のキー",
        "emoji 🌱🍊", "combining é vs é", new string('x', 250), "null\\0 is text", "%_like_%"
    ];

    private static readonly int[] IntKeys = [0, 1, -1, -5, int.MaxValue, int.MinValue, 1_000_000];

    private static readonly long[] LongKeys = [0, 1, -1, 9_007_199_254_740_993, long.MaxValue, long.MinValue + 1, 4_294_967_296];

    [PostgresFact]
    public async Task Text_and_integer_keys_stay_exact_through_snapshot_delta_push_delete_and_reconciliation()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await SeedAsync(harness, site);
        await using var client = await SubscribedClientAsync(harness, site);

        // ---- snapshot: every key, byte for byte (case-sensitive: three "alpha" rows)
        Assert.Equal(TextKeys.OrderBy(k => k, StringComparer.Ordinal), await LocalKeysAsync<string>(client, "Tag"));
        Assert.Equal(IntKeys.Order().Select(k => (long)k), await LocalKeysAsync<long>(client, "Reading"));
        Assert.Equal(LongKeys.Order(), await LocalKeysAsync<long>(client, "Counter"));
        Assert.Equal(3.25, Convert.ToDouble(await client.ScalarAsync("SELECT Value FROM Reading WHERE Id = $p0", int.MinValue), CultureInfo.InvariantCulture));
        Assert.Equal(long.MaxValue, await client.ScalarAsync("SELECT Total FROM Counter WHERE Id = $p0", long.MaxValue));

        // ---- server delta: update and delete by exact key
        await harness.ExecServerAsync("""UPDATE farm."Tag" SET "Label" = 'server edit' WHERE "Code" = ANY($1)""", (object)new[] { "ALPHA", "سلام دنیا", " padded " });
        await harness.ExecServerAsync("""DELETE FROM farm."Tag" WHERE "Code" = ANY($1)""", (object)new[] { "Alpha", "a:b|c;d,e" });
        await harness.ExecServerAsync("""UPDATE farm."Reading" SET "Value" = -1.5 WHERE "Id" = $1""", int.MaxValue);
        await harness.ExecServerAsync("""DELETE FROM farm."Counter" WHERE "Id" = ANY($1)""", new[] { long.MaxValue, 9_007_199_254_740_993 });
        await client.SyncOkAsync();

        Assert.Equal("server edit", await client.ScalarAsync("SELECT Label FROM Tag WHERE Code = $p0", "ALPHA"));
        Assert.Null(await client.ScalarAsync("SELECT Label FROM Tag WHERE Code = $p0", "alpha"));            // case-distinct row untouched
        Assert.Equal("server edit", await client.ScalarAsync("SELECT Label FROM Tag WHERE Code = $p0", " padded "));
        Assert.Equal(0L, await client.ScalarAsync("SELECT count(*) FROM Tag WHERE Code IN ('Alpha', 'a:b|c;d,e')"));
        Assert.Equal(-1.5, Convert.ToDouble(await client.ScalarAsync("SELECT Value FROM Reading WHERE Id = $p0", int.MaxValue), CultureInfo.InvariantCulture));
        Assert.Equal(0L, await client.ScalarAsync("SELECT count(*) FROM Counter WHERE Id IN ($p0, $p1)", long.MaxValue, 9_007_199_254_740_993));
        Assert.Equal(1L, await client.ScalarAsync("SELECT count(*) FROM Counter WHERE Id = $p0", 9_007_199_254_740_992 + 4_294_967_296 - 9_007_199_254_740_992));

        // ---- device push: insert, update and delete by exact key, including keys only one character (case) apart
        await client.ExecAsync("INSERT INTO Tag (Code, SiteId, Label) VALUES ($p0, $p1, 'device')", "alphA", Key(site));
        await client.ExecAsync("INSERT INTO Tag (Code, SiteId, Label) VALUES ($p0, $p1, 'device')", "ключ 🔑", Key(site));
        await client.ExecAsync("UPDATE Tag SET Label = 'device edit' WHERE Code = $p0", "alpha");
        await client.ExecAsync("DELETE FROM Tag WHERE Code = $p0", "with space");
        await client.ExecAsync("INSERT INTO Reading (Id, SiteId, Value) VALUES ($p0, $p1, 42.5)", 42, Key(site));
        await client.ExecAsync("DELETE FROM Reading WHERE Id = $p0", int.MinValue);
        await client.ExecAsync("INSERT INTO Counter (Id, SiteId, Total) VALUES ($p0, $p1, $p2)", 9_007_199_254_740_995, Key(site), long.MinValue + 7);
        await client.ExecAsync("UPDATE Counter SET Total = $p0 WHERE Id = $p1", 9_007_199_254_740_993, long.MinValue + 1);
        await client.SyncOkAsync();

        Assert.Equal("device", await harness.ScalarServerAsync<string>("""SELECT "Label" FROM farm."Tag" WHERE "Code" = $1""", "alphA"));
        Assert.Equal("device", await harness.ScalarServerAsync<string>("""SELECT "Label" FROM farm."Tag" WHERE "Code" = $1""", "ключ 🔑"));
        Assert.Equal("device edit", await harness.ScalarServerAsync<string>("""SELECT "Label" FROM farm."Tag" WHERE "Code" = $1""", "alpha"));
        Assert.Equal("server edit", await harness.ScalarServerAsync<string>("""SELECT "Label" FROM farm."Tag" WHERE "Code" = $1""", "ALPHA"));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Tag" WHERE "Code" = 'with space'"""));
        Assert.Equal(42.5, await harness.ScalarServerAsync<double>("""SELECT "Value" FROM farm."Reading" WHERE "Id" = 42"""));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Reading" WHERE "Id" = $1""", int.MinValue));
        Assert.Equal(long.MinValue + 7, await harness.ScalarServerAsync<long>("""SELECT "Total" FROM farm."Counter" WHERE "Id" = $1""", 9_007_199_254_740_995));
        Assert.Equal(9_007_199_254_740_993, await harness.ScalarServerAsync<long>("""SELECT "Total" FROM farm."Counter" WHERE "Id" = $1""", long.MinValue + 1));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        // ---- reconciliation: C# and SQL digests agree for every key, so an in-step device fetches and removes nothing
        var reconciled = await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true, Push = false });
        Assert.Equal(0, reconciled.Changes.Total);
        await AssertSameRowsAsync(harness, client);
    }

    [PostgresFact]
    public async Task Reconciliation_repairs_text_and_integer_key_tables_after_tombstones_were_purged()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await SeedAsync(harness, site);
        await using var client = await SubscribedClientAsync(harness, site);

        await harness.ExecServerAsync("""DELETE FROM farm."Tag" WHERE "Code" = ANY($1)""", (object)new[] { "ALPHA", "emoji 🌱🍊", new string('x', 250) });
        await harness.ExecServerAsync("""DELETE FROM farm."Reading" WHERE "Id" = ANY($1)""", new[] { -1, int.MaxValue });
        await harness.ExecServerAsync("""DELETE FROM farm."Counter" WHERE "Id" = ANY($1)""", new[] { long.MinValue + 1, 9_007_199_254_740_993 });
        await harness.ExecServerAsync("""UPDATE farm."Tag" SET "Label" = 'after purge' WHERE "Code" = $1""", "alpha");
        await Task.Delay(50);
        Assert.True(await harness.Store.CleanupTombstonesAsync(TimeSpan.Zero, CancellationToken.None) >= 7);

        await client.SyncUntilSettledAsync();
        await client.SyncUntilSettledAsync();
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Reconcile) >= 1, "Past the horizon only reconciliation can remove the deleted rows.");
        await AssertSameRowsAsync(harness, client);
        Assert.Equal("after purge", await client.ScalarAsync("SELECT Label FROM Tag WHERE Code = $p0", "alpha"));
    }

    [PostgresFact]
    public async Task A_computed_column_that_changed_without_its_row_is_repaired_by_reconciliation()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await SeedAsync(harness, site);
        await using var client = await SubscribedClientAsync(harness, site);
        Assert.Equal((long)TextKeys.Length, await client.ScalarAsync("SELECT count(*) FROM Tag WHERE SiteName = 'North'"));

        // Renaming the site changes every tag's SiteName, but no tag row: an ordinary delta does not carry them.
        await harness.ExecServerAsync("""UPDATE farm."Site" SET "Name" = 'North (renamed)' WHERE "Id" = $1""", site);
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = false });
        Assert.Equal((long)TextKeys.Length, await client.ScalarAsync("SELECT count(*) FROM Tag WHERE SiteName = 'North'"));

        // The reconciliation hash includes computed columns: every tag differs, and every tag is refetched.
        var reconciled = await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true, Push = false });
        Assert.True(reconciled.Changes.Total >= TextKeys.Length, SyncClient.Describe(reconciled));
        Assert.Equal((long)TextKeys.Length, await client.ScalarAsync("SELECT count(*) FROM Tag WHERE SiteName = 'North (renamed)'"));
        Assert.Equal(0L, await client.ScalarAsync("SELECT count(*) FROM Reading WHERE SiteId IS NULL"));
    }

    // ================================================================ fixture

    private const string ExtraSchema = """
        CREATE TABLE farm."Tag" (
            "Code" text NOT NULL PRIMARY KEY,
            "SiteId" uuid NOT NULL REFERENCES farm."Site" ("Id"),
            "Label" text NULL);
        CREATE TABLE farm."Reading" (
            "Id" integer NOT NULL PRIMARY KEY,
            "SiteId" uuid NOT NULL REFERENCES farm."Site" ("Id"),
            "Value" double precision NOT NULL);
        CREATE TABLE farm."Counter" (
            "Id" bigint NOT NULL PRIMARY KEY,
            "SiteId" uuid NOT NULL REFERENCES farm."Site" ("Id"),
            "Total" bigint NOT NULL);
        """;

    private Task<SyncHarness> CreateHarnessAsync() => SyncHarness.CreateAsync(output,
        seedBeforeCapture: async dataSource =>
        {
            await using var command = dataSource.CreateCommand(ExtraSchema);
            await command.ExecuteNonQueryAsync();
        },
        model: model =>
        {
            ConfigureServer(model);
            model
                .Table("farm", "Tag", t => t
                    .Key("Code", G9ESyncColumnType.String)
                    .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
                    .Column("Label", G9ESyncColumnType.String)
                    .Computed("SiteName", G9ESyncColumnType.String, """(SELECT s."Name" FROM farm."Site" AS s WHERE s."Id" = {row}."SiteId")"""))
                .Table("farm", "Reading", t => t
                    .Key("Id", G9ESyncColumnType.Int32)
                    .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
                    .Column("Value", G9ESyncColumnType.Double, nullable: false))
                .Table("farm", "Counter", t => t
                    .Key("Id", G9ESyncColumnType.Int64)
                    .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
                    .Column("Total", G9ESyncColumnType.Int64, nullable: false))
                .Subscription("site-extras", s => s
                    .Parameter("SiteId", G9ESyncColumnType.Guid)
                    .Include("farm.Tag", "{row}.\"SiteId\" = @SiteId")
                    .Include("farm.Reading", "{row}.\"SiteId\" = @SiteId")
                    .Include("farm.Counter", "{row}.\"SiteId\" = @SiteId")
                    .DependsOn("sites"));
        });

    private static readonly G9CSyncClientModel ClientModel = new G9CSyncClientModelBuilder()
        .Table("farm.Site", "Site", t => t
            .Key("Id")
            .Column("Name", G9ESyncColumnType.String, nullable: false)
            .Column("IsActive", G9ESyncColumnType.Boolean, nullable: false))
        .Table("farm.Tag", "Tag", t => t
            .Key("Code", G9ESyncColumnType.String)
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Label", G9ESyncColumnType.String)
            .Column("SiteName", G9ESyncColumnType.String))
        .Table("farm.Reading", "Reading", t => t
            .Key("Id", G9ESyncColumnType.Int32)
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Value", G9ESyncColumnType.Double, nullable: false))
        .Table("farm.Counter", "Counter", t => t
            .Key("Id", G9ESyncColumnType.Int64)
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Total", G9ESyncColumnType.Int64, nullable: false))
        .Build();

    private static async Task SeedAsync(SyncHarness harness, Guid site)
    {
        foreach (var code in TextKeys) await harness.ExecServerAsync("""INSERT INTO farm."Tag" ("Code", "SiteId") VALUES ($1, $2)""", code, site);
        foreach (var id in IntKeys) await harness.ExecServerAsync("""INSERT INTO farm."Reading" ("Id", "SiteId", "Value") VALUES ($1, $2, 3.25)""", id, site);
        foreach (var id in LongKeys) await harness.ExecServerAsync("""INSERT INTO farm."Counter" ("Id", "SiteId", "Total") VALUES ($1, $2, $1)""", id, site);
    }

    private static async Task<SyncClient> SubscribedClientAsync(SyncHarness harness, Guid site)
    {
        var client = await harness.CreateClientAsync(clientModel: ClientModel);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-extras", new G9CSyncParameters().Set("SiteId", site));
        await client.SyncOkAsync();
        return client;
    }

    private static async Task<List<T>> LocalKeysAsync<T>(SyncClient client, string table)
    {
        var keyColumn = table == "Tag" ? "Code" : "Id";
        var count = (long)(await client.ScalarAsync($"SELECT count(*) FROM {table}"))!;
        var keys = new List<T>();
        for (var i = 0; i < count; i++)
        {
            keys.Add((T)(await client.ScalarAsync($"SELECT {keyColumn} FROM {table} ORDER BY {keyColumn} LIMIT 1 OFFSET {i}"))!);
        }

        return keys;
    }

    private static async Task AssertSameRowsAsync(SyncHarness harness, SyncClient client)
    {
        foreach (var (table, key, columns) in new[] { ("Tag", "Code", "\"Label\""), ("Reading", "Id", "\"Value\""), ("Counter", "Id", "\"Total\"") })
        {
            var server = new List<string>();
            await using (var command = harness.DataSource.CreateCommand($"SELECT \"{key}\"::text || '|' || coalesce({columns}::text, '∅') FROM farm.\"{table}\" ORDER BY \"{key}\"{(table == "Tag" ? " COLLATE \"C\"" : "")}"))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) server.Add(reader.GetString(0));
            }

            var device = new List<string>();
            var count = (long)(await client.ScalarAsync($"SELECT count(*) FROM {table}"))!;
            for (var i = 0; i < count; i++)
            {
                device.Add((string)(await client.ScalarAsync(
                    $"SELECT CAST({key} AS TEXT) || '|' || coalesce(CAST({columns.Trim('"')} AS TEXT), '∅') FROM {table} ORDER BY {key} LIMIT 1 OFFSET {i}"))!);
            }

            if (table != "Tag")
            {
                // Numeric keys: order numerically on both sides; doubles print differently, so compare the parsed values.
                server = server.OrderBy(r => long.Parse(r.Split('|')[0], CultureInfo.InvariantCulture)).ToList();
                device = device.OrderBy(r => long.Parse(r.Split('|')[0], CultureInfo.InvariantCulture)).ToList();
                Assert.Equal(server.Select(Normalize), device.Select(Normalize));
            }
            else
            {
                Assert.Equal(server, device.OrderBy(r => r.Split('|')[0], StringComparer.Ordinal).ToList());
            }
        }

        static string Normalize(string row)
        {
            var parts = row.Split('|');
            return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? parts[0] + "|" + value.ToString("R", CultureInfo.InvariantCulture)
                : row;
        }
    }
}
