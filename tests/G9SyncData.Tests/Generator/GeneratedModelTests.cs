using G9SyncData.Client.Model;
using G9SyncData.Generated;
using SQLite;

namespace G9SyncData.Tests.Generator
{
    /// <summary>
    ///     Compile-time proof of the source generator: the entities below are generated into <see cref="G9SyncGeneratedModel" /> while
    ///     this assembly builds, and the tests read the result.
    /// </summary>
    public sealed class GeneratedModelTests
    {
        [Fact]
        public void An_entity_honours_sqlite_net_names_an_inherited_key_and_inferred_types()
        {
            var pot = G9SyncGeneratedModel.Model.FindByServerName("Operation.Pot")!;

            Assert.Equal("Pot", pot.LocalName);
            Assert.Equal("Id", pot.KeyColumn);
            Assert.Equal(G9ESyncColumnType.String, pot.KeyType);
            Assert.Equal(
                [
                    "UpdatedAt|DateTime|True", "Code|String|True", "SiteId|Guid|False", "Status|Int32|False", "Weight|Decimal|True",
                    "State|Int32|False", "Big|Int64|False", "Order|Int32|False", "Label|String|False", "Tag|String|False",
                    "IsDeleted|Boolean|False", "Thumbnail|Bytes|True"
                ],
                pot.Columns.Select(Describe));
        }

        [Fact]
        public void Declared_and_sqlite_net_indexes_are_generated()
        {
            var pot = G9SyncGeneratedModel.Model.FindByServerName("Operation.Pot")!;

            Assert.Equal(
                ["IX_Pot_SiteId_Code|True|SiteId,Code", "IX_Pot_Status|False|Status", "IX_Pot_Label|True|Label", "IX_Pot_State_Big|False|State,Big"],
                pot.Indexes.Select(i => $"{i.Name}|{i.Unique}|{string.Join(',', i.Columns)}"));
        }

        [Fact]
        public void Explicit_attributes_override_the_local_name_key_and_key_type()
        {
            var site = G9SyncGeneratedModel.Model.FindByServerName("Common.Site")!;

            Assert.Equal("SiteLocal", site.LocalName);
            Assert.Equal("SiteKey", site.KeyColumn);
            Assert.Equal(G9ESyncColumnType.Guid, site.KeyType);
            Assert.Equal(
                ["Name|String|True", "CreatedAt|DateTimeOffset|False", "Duration|TimeSpan|False", "Area|Double|False", "Counter|Int64|False"],
                site.Columns.Select(Describe));
        }

        [Fact]
        public void Configure_lets_hand_written_tables_join_the_generated_ones()
        {
            var model = G9SyncGeneratedModel.Configure(new G9CSyncClientModelBuilder())
                .Table("Common.Extra", "Extra", t => t.Key("Id").Column("Value", G9ESyncColumnType.String))
                .Build();

            Assert.Equal(3, model.Tables.Count);
        }

        private static string Describe(G9CSyncClientColumn column) => $"{column.Name}|{column.Type}|{column.Nullable}";
    }
}

namespace G9SyncData.Tests.Generator.Entities
{
    public abstract class TestBaseEntity
    {
        [PrimaryKey] public string Id { get; set; } = string.Empty;

        public DateTime? UpdatedAt { get; set; }
    }

    public enum PotState : byte
    {
        Empty,
        Planted
    }

    public enum BigState : long
    {
        Small,
        Huge = long.MaxValue
    }

    [Table("Pot")]
    [G9AttrSyncTable("Operation.Pot")]
    [G9AttrSyncIndex("SiteId", "Code", Unique = true)]
    public sealed class PotTestEntity : TestBaseEntity
    {
        public string Code { get; set; } = string.Empty;
        public Guid SiteId { get; set; }

        [Indexed] public int Status { get; set; }

        public decimal? Weight { get; set; }

        [Indexed("IX_Pot_State_Big", 1)] public PotState State { get; set; }

        [Indexed("IX_Pot_State_Big", 2)] public BigState Big { get; set; }

        [Column("Order")] public int SortOrder { get; set; }

        [Unique]
        [G9AttrSyncColumn(Nullability = G9ESyncNullability.NotNull)]
        public string Label { get; set; } = string.Empty;

        [NotNull] public string Tag { get; set; } = string.Empty;

        public string Computed => Code;

        [Ignore] public string Transient { get; set; } = string.Empty;

        [G9AttrSyncIgnore] public Uri? Link { get; set; }

        public bool IsDeleted { get; set; }

        public byte[]? Thumbnail { get; set; }
    }

    [G9AttrSyncTable("Common.Site", LocalName = "SiteLocal")]
    public sealed class SiteTestEntity
    {
        [G9AttrSyncKey]
        [G9AttrSyncColumn(Type = G9ESyncColumnType.Guid)]
        public string SiteKey { get; set; } = string.Empty;

        public string? Name { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public TimeSpan Duration { get; set; }
        public double Area { get; set; }
        public long Counter { get; set; }
    }
}
