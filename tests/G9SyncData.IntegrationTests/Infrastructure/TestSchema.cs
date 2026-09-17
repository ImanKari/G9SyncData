using G9SyncData.Client.Model;
using G9SyncData.Server.Model;

namespace G9SyncData.IntegrationTests.Infrastructure;

/// <summary>Knobs for server model variants (conflict policy, a column the server starts serving later, a download-only table).</summary>
internal sealed record ServerModelOptions(
    G9EConflictPolicy PotConflicts = G9EConflictPolicy.ColumnMerge,
    bool IncludePotNote = true,
    G9ESyncDirection SiteDirection = G9ESyncDirection.Bidirectional,
    Action<G9CConflictRulesBuilder>? PotConflictRules = null,
    bool PotNoteReadOnly = false,
    string? PotWriteRule = null,
    G9EMoveOutPolicy PotMoveOut = G9EMoveOutPolicy.Delete);

/// <summary>
///     A small farm domain that exercises the hard parts: a partial unique index (the Dotmim 25P02 case), a row-state
///     filter that moves rows out, a parameterised subscription with a dependency, a permission-scoped subscription fed by
///     the auth context, decimals, timestamps and ColumnMerge.
/// </summary>
internal static class TestSchema
{
    public const string Sql = """
        CREATE SCHEMA farm;
        CREATE TABLE farm."Site" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "Name" text NOT NULL,
            "IsActive" boolean NOT NULL DEFAULT true);
        CREATE TABLE farm."Pot" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "SiteId" uuid NOT NULL REFERENCES farm."Site" ("Id"),
            "Code" text NOT NULL,
            "Status" integer NOT NULL DEFAULT 0,
            "Weight" numeric(18,4) NULL,
            "PlantedAt" timestamp without time zone NULL,
            "Note" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false);
        CREATE UNIQUE INDEX "UX_Pot_SiteId_Code" ON farm."Pot" ("SiteId", "Code") WHERE NOT "IsDeleted";
        """;

    public static void ConfigureServer(G9CSyncModelBuilder model) => ConfigureServer(model, new ServerModelOptions());

    public static void ConfigureServer(G9CSyncModelBuilder model, ServerModelOptions options) => model
        .Table("farm", "Site", t => t
            .Key("Id")
            .Column("Name", G9ESyncColumnType.String, nullable: false)
            .Column("IsActive", G9ESyncColumnType.Boolean, nullable: false)
            .Direction(options.SiteDirection))
        .Table("farm", "Pot", t =>
        {
            t.Key("Id")
                .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
                .Column("Code", G9ESyncColumnType.String, nullable: false)
                .Column("Status", G9ESyncColumnType.Int32, nullable: false)
                .Column("Weight", G9ESyncColumnType.Decimal)
                .Column("PlantedAt", G9ESyncColumnType.DateTime);
            if (options.IncludePotNote && options.PotNoteReadOnly) t.Column("Note", G9ESyncColumnType.String, nullable: true, column => column.ReadOnly());
            else if (options.IncludePotNote) t.Column("Note", G9ESyncColumnType.String);
            t.Column("IsDeleted", G9ESyncColumnType.Boolean, nullable: false)
                .Conflicts(options.PotConflicts);
            if (options.PotConflictRules is not null) t.OnConflict(options.PotConflictRules);
            if (options.PotWriteRule is not null) t.WriteRule(options.PotWriteRule);
        })
        .Subscription("sites", s => s
            .Include("farm.Site", "true", "{row}.\"IsActive\""))
        .Subscription("site-pots", s => s
            .Parameter("SiteId", G9ESyncColumnType.Guid)
            .Include("farm.Pot", "{row}.\"SiteId\" = @SiteId", "NOT {row}.\"IsDeleted\"")
            .MoveOut(options.PotMoveOut)
            .DependsOn("sites"))
        .Subscription("allowed-pots", s => s
            .ServerParameter("AllowedSites", G9ESyncColumnType.Guid, isList: true)
            .Include("farm.Pot", "{row}.\"SiteId\" = ANY(@AllowedSites)", "NOT {row}.\"IsDeleted\"")
            .DependsOn("sites"));

    public static G9CSyncClientModel Client { get; } = new G9CSyncClientModelBuilder()
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
            .Index(null, false, "SiteId"))
        .Build();

    public static G9CSyncParameters SitePots(Guid siteId) => new G9CSyncParameters().Set("SiteId", siteId);

    /// <summary>The canonical local key text of a Guid.</summary>
    public static string Key(Guid id) => id.ToString("D");
}
