namespace G9SyncData.Samples.AotClient;

// The client model comes from these classes at compile time (G9SyncData.SourceGenerators): no reflection at runtime.

[G9AttrSyncTable("farm.Site", LocalName = "Site")]
public sealed class Site
{
    public Guid Id { get; set; }

    [G9AttrSyncColumn(Nullability = G9ESyncNullability.NotNull)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; }
}

[G9AttrSyncTable("farm.Pot", LocalName = "Pot")]
[G9AttrSyncIndex("SiteId")]
public sealed class Pot
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }

    [G9AttrSyncColumn(Nullability = G9ESyncNullability.NotNull)]
    public string Code { get; set; } = string.Empty;

    public int Status { get; set; }
    public decimal? Weight { get; set; }
    public DateTime? PlantedAt { get; set; }
    public string? Note { get; set; }
    public bool IsDeleted { get; set; }
}
