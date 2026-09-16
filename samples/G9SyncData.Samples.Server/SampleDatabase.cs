using G9SyncData.Server.Model;
using Npgsql;

namespace G9SyncData.Samples.Server;

/// <summary>The sample farm schema, its seed data and its sync model.</summary>
internal static class SampleDatabase
{
    private const string Schema = """
        CREATE SCHEMA IF NOT EXISTS farm;
        CREATE TABLE IF NOT EXISTS farm."Site" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "Name" text NOT NULL,
            "IsActive" boolean NOT NULL DEFAULT true);
        CREATE TABLE IF NOT EXISTS farm."Pot" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "SiteId" uuid NOT NULL REFERENCES farm."Site" ("Id"),
            "Code" text NOT NULL,
            "Status" integer NOT NULL DEFAULT 0,
            "Weight" numeric(18,4) NULL,
            "PlantedAt" timestamp without time zone NULL,
            "Note" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false);
        CREATE UNIQUE INDEX IF NOT EXISTS "UX_Pot_SiteId_Code" ON farm."Pot" ("SiteId", "Code") WHERE NOT "IsDeleted";
        INSERT INTO farm."Site" ("Id", "Name")
            SELECT gen_random_uuid(), v.name FROM (VALUES ('North'), ('South')) AS v(name)
            WHERE NOT EXISTS (SELECT 1 FROM farm."Site");
        INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status", "PlantedAt")
            SELECT gen_random_uuid(), s."Id", 'P' || lpad(g::text, 6, '0'), g % 5, now()::timestamp - make_interval(days => g % 400)
            FROM farm."Site" AS s CROSS JOIN generate_series(1, 25000) AS g
            WHERE NOT EXISTS (SELECT 1 FROM farm."Pot");
        """;

    public static void ConfigureModel(G9CSyncModelBuilder model) => model
        .Table("farm", "Site", t => t
            .Key("Id")
            .Column("Name", G9ESyncColumnType.String, nullable: false)
            .Column("IsActive", G9ESyncColumnType.Boolean, nullable: false))
        .Table("farm", "Pot", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Code", G9ESyncColumnType.String, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("Weight", G9ESyncColumnType.Decimal)
            .Column("PlantedAt", G9ESyncColumnType.DateTime)
            .Column("Note", G9ESyncColumnType.String)
            .Column("IsDeleted", G9ESyncColumnType.Boolean, nullable: false)
            .Conflicts(G9EConflictPolicy.ColumnMerge))
        .Subscription("sites", s => s
            .Include("farm.Site", "true", "{row}.\"IsActive\""))
        .Subscription("site-pots", s => s
            .Parameter("SiteId", G9ESyncColumnType.Guid)
            .Include("farm.Pot", "{row}.\"SiteId\" = @SiteId", "NOT {row}.\"IsDeleted\"")
            .DependsOn("sites"));

    /// <summary>Creates the database when missing, then the schema and seed rows when missing.</summary>
    public static async Task EnsureAsync(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var database = target.Database ?? throw new InvalidOperationException("The connection string names no database.");
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString))
        {
            await admin.OpenAsync();
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = $1", admin);
            exists.Parameters.Add(new NpgsqlParameter { Value = database });
            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", admin);
                await create.ExecuteNonQueryAsync();
            }
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var schema = new NpgsqlCommand(Schema, connection);
        await schema.ExecuteNonQueryAsync();
    }
}
