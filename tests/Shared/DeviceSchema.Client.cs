using G9SyncData.Client.Model;

namespace G9SyncData.DeviceTests.Shared;

/// <summary>
///     The large-site workload the Android tests sync: AgriPad's real <c>Pot</c> shape (GUID text keys, four GUID reference columns,
///     audit timestamps, an integer index, a WKT location) under ridges of one site, a thousand pots per ridge. The device half is
///     compiled into the Android test app; the server half (<c>DeviceSchema.Server.cs</c>) only into the host tests.
/// </summary>
public static partial class DeviceSchema
{
    public const string SitePotsSubscription = "site-pots";
    public const string SiteRidgesSubscription = "site-ridges";
    public const int PotsPerRidge = 1_000;

    public static G9CSyncClientModel Client { get; } = new G9CSyncClientModelBuilder()
        .Table("device.Ridge", "Ridge", t => t
            .Key("Id")
            .Column("SiteId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Name", G9ESyncColumnType.String, nullable: false)
            .Index("IX_Ridge_SiteId", false, "SiteId"))
        .Table("device.Pot", "Pot", t => t
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
            .Index("IX_Pot_RidgeId", false, "RidgeId"))
        .Build();

    public static G9CSyncParameters Site(Guid siteId) => new G9CSyncParameters().Set("SiteId", siteId);
}
