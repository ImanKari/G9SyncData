using Microsoft.EntityFrameworkCore;

namespace G9SyncData.IntegrationTests.Infrastructure;

/// <summary>
///     The test farm schema as an application would model it in EF Core — including a table the sync model does not know
///     (<see cref="FarmSiteMember" />) and a NAMED query filter, the two things an application's visibility rules are made of.
/// </summary>
internal sealed class FarmDbContext(DbContextOptions<FarmDbContext> options) : DbContext(options)
{
    /// <summary>Name of the application's own filter: pots whose code starts with HIDDEN never leave the server.</summary>
    public const string HiddenFilter = "hidden-codes";

    /// <summary>The table behind <see cref="Members" />; the sync model does not include it.</summary>
    public const string MembersSql = """
        CREATE TABLE farm."SiteMember" (
            "SiteId" uuid NOT NULL REFERENCES farm."Site" ("Id"),
            "UserId" text NOT NULL,
            PRIMARY KEY ("SiteId", "UserId"));
        """;

    public DbSet<FarmSite> Sites => Set<FarmSite>();
    public DbSet<FarmPot> Pots => Set<FarmPot>();
    public DbSet<FarmSiteMember> Members => Set<FarmSiteMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("farm");
        modelBuilder.Entity<FarmSite>().ToTable("Site");
        modelBuilder.Entity<FarmPot>(pot =>
        {
            pot.ToTable("Pot");
            pot.Property(p => p.Weight).HasColumnType("numeric(18,4)");
            pot.HasQueryFilter(HiddenFilter, p => !p.Code.StartsWith("HIDDEN"));
        });
        modelBuilder.Entity<FarmSiteMember>(member =>
        {
            member.ToTable("SiteMember");
            member.HasKey(m => new { m.SiteId, m.UserId });
        });
    }
}

internal sealed class FarmSite
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
}

internal sealed class FarmPot
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public string Code { get; set; } = "";
    public int Status { get; set; }
    public decimal? Weight { get; set; }
    public DateTime? PlantedAt { get; set; }
    public string? Note { get; set; }
    public bool IsDeleted { get; set; }
}

internal sealed class FarmSiteMember
{
    public Guid SiteId { get; set; }
    public string UserId { get; set; } = "";
}
