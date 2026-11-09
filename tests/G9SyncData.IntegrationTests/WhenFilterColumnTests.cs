using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql;

namespace G9SyncData.IntegrationTests;

/// <summary>
///     A When filter reads only synced columns (the store refuses the model otherwise). Changes are captured per synced column, so a
///     filter on a column the model leaves out never sees the row change: the row never moves out and the device keeps it. Found when
///     an allow-list dropped the column a "current layout" filter read, and an office redraw stopped reaching tablets.
/// </summary>
public sealed class WhenFilterColumnTests
{
    private static G9CSyncServerTable Pot(bool withNote)
    {
        var builder = new G9CSyncModelBuilder();
        TestSchema.ConfigureServer(builder, new ServerModelOptions(IncludePotNote: withNote));
        return builder.Build().FindTable("farm.Pot")!;
    }

    [Fact]
    public void A_When_filter_on_an_unsynced_column_is_reported()
    {
        const string when = "NOT {row}.\"IsDeleted\" AND {row}.\"Note\" IS NULL AND {row}.\"Note\" <> ''";

        Assert.Equal(["Note"], G9CPostgresSyncStore.UntrackedWhenColumns(when, Pot(withNote: false)));
    }

    [Fact]
    public void A_When_filter_on_synced_columns_and_the_key_is_accepted()
    {
        Assert.Empty(G9CPostgresSyncStore.UntrackedWhenColumns("NOT {row}.\"IsDeleted\" AND {row}.\"Id\" IS NOT NULL", Pot(withNote: false)));
        Assert.Empty(G9CPostgresSyncStore.UntrackedWhenColumns("{row}.\"Note\" IS NULL", Pot(withNote: true)));
        Assert.Empty(G9CPostgresSyncStore.UntrackedWhenColumns(null, Pot(withNote: false)));
    }
}
