using G9SyncData.Client.Model;

namespace G9SyncData.DeviceTests.Shared;

/// <summary>
///     A real farm's first download, replayed: AgriPad's <c>farm-site</c> tables (Ridge, Pot, Placement, Tree, Graft) with exactly the
///     columns and storage types of the app's mirror entities (Agriculture.AgriPad.App/Common/Entities: GUID text keys and references,
///     geometry as hex EWKB text, enums as integers, booleans, DateTime text, the AuditedEntity columns). The rows come from a device
///     database of the farm (the host test loads them into the server, <c>FarmReplaySchema.Server.cs</c>); nothing of the farm's data is in
///     this repository. One subscription carries every table, as <c>farm-site</c> does for one FarmId.
/// </summary>
public static partial class FarmReplaySchema
{
    public const string Subscription = "farm-replay";

    /// <summary>The replayed tables, parents first (the order AgriPad's farm-site subscription declares them).</summary>
    public static readonly string[] Tables = ["Ridge", "Pot", "Placement", "Tree", "Graft"];

    public static G9CSyncClientModel Client { get; } = new G9CSyncClientModelBuilder()
        .Table("shah.Ridge", "Ridge", t => Audited(t
            .Key("Id")
            .Column("Index", G9ESyncColumnType.Int32)
            .Column("PotCount", G9ESyncColumnType.Int32, nullable: false)
            .Column("Line", G9ESyncColumnType.String, nullable: false)
            .Column("BlockId", G9ESyncColumnType.Guid, nullable: false)))
        .Table("shah.Pot", "Pot", t => Audited(t
            .Key("Id")
            .Column("Index", G9ESyncColumnType.Int32, nullable: false)
            .Column("Location", G9ESyncColumnType.String, nullable: false)
            .Column("Gender", G9ESyncColumnType.Int32, nullable: false)
            .Column("RidgeId", G9ESyncColumnType.Guid, nullable: false)
            .Column("VarietyId", G9ESyncColumnType.Guid, nullable: false)))
        .Table("shah.Placement", "Placement", t => Audited(t
            .Key("Id")
            .Column("PotId", G9ESyncColumnType.Guid)
            .Column("TreeId", G9ESyncColumnType.Guid, nullable: false)
            .Column("GreenHouseRowId", G9ESyncColumnType.Guid)
            .Column("GreenHouseIndexInRow", G9ESyncColumnType.Int32)
            .Column("IsCurrent", G9ESyncColumnType.Boolean, nullable: false)
            .Column("PlacementDate", G9ESyncColumnType.DateTime)))
        .Table("shah.Tree", "Tree", t => Audited(t
            .Key("Id")
            .Column("Code", G9ESyncColumnType.String)
            .Column("IsIndicator", G9ESyncColumnType.Boolean, nullable: false)
            .Column("VarietyId", G9ESyncColumnType.Guid, nullable: false)
            .Column("Gender", G9ESyncColumnType.Int32, nullable: false)
            .Column("Birthdate", G9ESyncColumnType.DateTime)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("VigorStatus", G9ESyncColumnType.Int32, nullable: false)))
        .Table("shah.Graft", "Graft", t => Audited(t
            .Key("Id")
            .Column("TreeId", G9ESyncColumnType.Guid, nullable: false)
            .Column("GraftVarietyId", G9ESyncColumnType.Guid, nullable: false)
            .Column("IsCurrent", G9ESyncColumnType.Boolean, nullable: false)
            .Column("GraftedDate", G9ESyncColumnType.DateTime)
            .Column("Method", G9ESyncColumnType.Int32, nullable: false)
            .Column("RootstockFruitingType", G9ESyncColumnType.Int32, nullable: false)
            .Column("EarlyEvaluationDays", G9ESyncColumnType.Int32, nullable: false)
            .Column("FinalEvaluationDays", G9ESyncColumnType.Int32, nullable: false)
            .Column("Status", G9ESyncColumnType.Int32, nullable: false)
            .Column("Description", G9ESyncColumnType.String)))
        .Build();

    /// <summary>AgriPad's AuditedEntity columns, after each table's own.</summary>
    private static G9CSyncClientTableBuilder Audited(G9CSyncClientTableBuilder t) => t
        .Column("CreatedTime", G9ESyncColumnType.DateTime, nullable: false)
        .Column("UpdatedTime", G9ESyncColumnType.DateTime, nullable: false)
        .Column("CreatedByUserId", G9ESyncColumnType.Guid)
        .Column("UpdatedByUserId", G9ESyncColumnType.Guid);
}
