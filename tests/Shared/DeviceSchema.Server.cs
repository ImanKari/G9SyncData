using G9SyncData.Server.Model;

namespace G9SyncData.DeviceTests.Shared;

public static partial class DeviceSchema
{
    public const string Sql = """
        CREATE SCHEMA device;
        CREATE TABLE device."Ridge" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "SiteId" uuid NOT NULL,
            "Name" text NOT NULL);
        CREATE INDEX "IX_Ridge_SiteId" ON device."Ridge" ("SiteId");
        CREATE TABLE device."Pot" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "Index" integer NOT NULL,
            "Location" text NOT NULL,
            "Gender" integer NOT NULL,
            "RidgeId" uuid NOT NULL REFERENCES device."Ridge" ("Id"),
            "VarietyId" uuid NOT NULL,
            "CreatedTime" timestamp without time zone NOT NULL,
            "UpdatedTime" timestamp without time zone NOT NULL,
            "CreatedByUserId" uuid NULL,
            "UpdatedByUserId" uuid NULL);
        CREATE INDEX "IX_Pot_RidgeId" ON device."Pot" ("RidgeId");
        """;

    /// <summary>
    ///     Two statements, each with <c>$1</c> site and <c>$2</c> pot count. Ridges first, then pots filling them a thousand at a time, with random GUID keys (as
    ///     AgriPad's are) so neither side can profit from insertion order.
    /// </summary>
    public static readonly string[] SeedSql =
    [
        """
        INSERT INTO device."Ridge" ("Id", "SiteId", "Name")
        SELECT gen_random_uuid(), $1, 'R' || lpad(r::text, 4, '0') FROM generate_series(1, ($2 + 999) / 1000) AS r
        """,
        """
        INSERT INTO device."Pot" ("Id", "Index", "Location", "Gender", "RidgeId", "VarietyId", "CreatedTime", "UpdatedTime", "CreatedByUserId", "UpdatedByUserId")
        SELECT gen_random_uuid(), g, 'POINT(' || (51.3 + (g % 1000) / 100000.0)::text || ' ' || (35.7 + g / 10000000.0)::text || ')', g % 3,
               ridges.ids[(g - 1) / 1000 + 1], '6f1e2d3c-4b5a-4978-8a6b-5c4d3e2f1a0b'::uuid,
               timestamp '2024-01-01' + make_interval(mins => g % 500000), timestamp '2025-06-01' + make_interval(secs => g),
               '0b1a2f3e-4d5c-4b6a-8978-1a2b3c4d5e6f'::uuid, CASE WHEN g % 4 = 0 THEN NULL ELSE '0b1a2f3e-4d5c-4b6a-8978-1a2b3c4d5e6f'::uuid END
        FROM generate_series(1, $2) AS g,
             (SELECT array_agg("Id" ORDER BY "Name") AS ids FROM device."Ridge" WHERE "SiteId" = $1) AS ridges
        """
    ];

    public static void ConfigureServer(G9CSyncModelBuilder model) => model
        .Table("device", "Ridge", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Name", G9ESyncColumnType.String, nullable: false))
        .Table("device", "Pot", t => t
            .Key("Id")
            .Column("Index", G9ESyncColumnType.Int32, nullable: false)
            .Column("Location", G9ESyncColumnType.String, nullable: false)
            .Column("Gender", G9ESyncColumnType.Int32, nullable: false)
            .Column("RidgeId", G9ESyncColumnType.Guid, nullable: false)
            .Column("VarietyId", G9ESyncColumnType.Guid, nullable: false)
            .Column("CreatedTime", G9ESyncColumnType.DateTime, nullable: false)
            .Column("UpdatedTime", G9ESyncColumnType.DateTime, nullable: false)
            .Column("CreatedByUserId", G9ESyncColumnType.Guid)
            .Column("UpdatedByUserId", G9ESyncColumnType.Guid)
            .Conflicts(G9EConflictPolicy.ColumnMerge))
        .Subscription(SiteRidgesSubscription, s => s
            .Parameter("SiteId", G9ESyncColumnType.Guid)
            .Include("device.Ridge", "{row}.\"SiteId\" = @SiteId"))
        .Subscription(SitePotsSubscription, s => s
            .Parameter("SiteId", G9ESyncColumnType.Guid)
            .Include("device.Pot", "EXISTS (SELECT 1 FROM device.\"Ridge\" AS r WHERE r.\"Id\" = {row}.\"RidgeId\" AND r.\"SiteId\" = @SiteId)"));
}
