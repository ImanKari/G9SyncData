using G9SyncData.Client.Model;
using G9SyncData.Server.Model;

namespace G9SyncData.Benchmarks;

/// <summary>A pot table shaped like AgriPad's (12 columns, GUID key, one site holding every row), plus an uncaptured twin.</summary>
internal static class BenchSchema
{
    public const string Sql = """
        CREATE SCHEMA bench;
        CREATE TABLE bench."Site" ("Id" uuid NOT NULL PRIMARY KEY, "Name" text NOT NULL);
        CREATE TABLE bench."Pot" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "SiteId" uuid NOT NULL REFERENCES bench."Site" ("Id"),
            "Code" text NOT NULL,
            "Status" integer NOT NULL DEFAULT 0,
            "Row" integer NOT NULL DEFAULT 0,
            "Col" integer NOT NULL DEFAULT 0,
            "Weight" numeric(18,4) NULL,
            "Latitude" double precision NULL,
            "Longitude" double precision NULL,
            "PlantedAt" timestamp without time zone NULL,
            "Note" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false);
        CREATE INDEX "IX_Pot_SiteId" ON bench."Pot" ("SiteId");
        CREATE TABLE bench."PotPlain" (LIKE bench."Pot" INCLUDING DEFAULTS INCLUDING CONSTRAINTS INCLUDING INDEXES);
        """;

    public static string InsertRows(string table) => $"""
        INSERT INTO bench."{table}" ("Id", "SiteId", "Code", "Status", "Row", "Col", "Weight", "Latitude", "Longitude", "PlantedAt", "Note")
        SELECT gen_random_uuid(), $1, 'P' || lpad(g::text, 7, '0'), g % 7, g / 400, g % 400, (g % 1000) / 10.0,
               32 + (g % 10000) / 100000.0, 51 + (g % 10000) / 100000.0, timestamp '2024-01-01' + make_interval(mins => g % 500000),
               CASE WHEN g % 5 = 0 THEN 'note ' || g END
        FROM generate_series(1, $2) AS g
        """;

    public static void ConfigureServer(G9CSyncModelBuilder model) => model
        .Table("bench", "Site", t => t
            .Key("Id")
            .Column("Name", G9ESyncColumnType.String, nullable: false))
        .Table("bench", "Pot", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("Row", G9ESyncColumnType.Int32, nullable: false)
            .Column("Col", G9ESyncColumnType.Int32, nullable: false)
            .Column("Weight", G9ESyncColumnType.Decimal)
            .Column("Latitude", G9ESyncColumnType.Double)
            .Column("Longitude", G9ESyncColumnType.Double)
            .Column("PlantedAt", G9ESyncColumnType.DateTime)
            .Column("Note", G9ESyncColumnType.String)
            .Column("IsDeleted", G9ESyncColumnType.Boolean, nullable: false))
        .Subscription("sites", s => s.Include("bench.Site"))
        .Subscription("site-pots", s => s
            .Parameter("SiteId", G9ESyncColumnType.Guid)
            .Include("bench.Pot", "{row}.\"SiteId\" = @SiteId")
            .DependsOn("sites"));

    public static G9CSyncClientModel Client { get; } = new G9CSyncClientModelBuilder()
        .Table("bench.Site", "Site", t => t
            .Key("Id")
            .Column("Name", G9ESyncColumnType.String, nullable: false))
        .Table("bench.Pot", "Pot", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("Row", G9ESyncColumnType.Int32, nullable: false)
            .Column("Col", G9ESyncColumnType.Int32, nullable: false)
            .Column("Weight", G9ESyncColumnType.Decimal)
            .Column("Latitude", G9ESyncColumnType.Double)
            .Column("Longitude", G9ESyncColumnType.Double)
            .Column("PlantedAt", G9ESyncColumnType.DateTime)
            .Column("Note", G9ESyncColumnType.String)
            .Column("IsDeleted", G9ESyncColumnType.Boolean, nullable: false)
            .Index(null, false, "SiteId"))
        .Build();
}
