using G9SyncData.Server.EfCore;
using G9SyncData.Server.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace G9SyncData.Tests.Server;

/// <summary>
///     The EF Core model is the single declaration of the schema (v2 A-01). These tests pin the translation,
///     including the three things that silently went wrong under the previous engine: a column whose PROVIDER type
///     differs from its CLR type, a server-generated column an upload must never set, and the indexes that have to
///     reach the device with the schema instead of in a hand-written migration.
/// </summary>
public sealed class EfCoreModelExtractorTests
{
    private static G9CSyncServerModel BuildModel(Action<G9CEfCoreSyncBuilder>? extra = null)
    {
        using var context = new FarmContext();
        var builder = new G9CSyncModelBuilder();
        builder.FromEfCore(context.Model, ef =>
        {
            ef.Entity<Pot>();
            ef.Entity<Placement>(e => e.Conflicts(G9EConflictPolicy.ColumnMerge));
            extra?.Invoke(ef);
        });

        // A model with no subscription is still a valid model to build; the tables are what these tests assert.
        builder.Subscription("all", s => s.Include("Operation.Pot", "true"));
        return builder.Build();
    }

    [Fact]
    public void Reads_schema_table_key_and_column_names_from_the_model()
    {
        var model = BuildModel();
        var pot = model.Tables.Single(t => t.LogicalName == "Operation.Pot");

        Assert.Equal("Operation", pot.Schema);
        Assert.Equal("Pot", pot.Name);
        Assert.Equal("Id", pot.KeyColumn);
        Assert.Equal(G9ESyncColumnType.Guid, pot.KeyType);

        // The COLUMN name wins over the property name - the mirror-drift trap the old engine punished at runtime.
        Assert.NotNull(pot.FindColumn("code"));
        Assert.Null(pot.FindColumn("CodeProperty"));
    }

    [Fact]
    public void Provider_type_wins_over_clr_type_for_a_converted_property()
    {
        var model = BuildModel();
        var pot = model.Tables.Single(t => t.LogicalName == "Operation.Pot");

        // Kind is a C# enum stored as text: the wire must carry String, not Int32.
        var kind = pot.FindColumn("Kind");
        Assert.NotNull(kind);
        Assert.Equal(G9ESyncColumnType.String, kind!.Type);

        // Status is an enum with no converter, so it stays integral.
        var status = pot.FindColumn("Status");
        Assert.NotNull(status);
        Assert.Equal(G9ESyncColumnType.Int32, status!.Type);
    }

    [Fact]
    public void A_converter_instance_decides_the_wire_type_like_a_generic_conversion()
    {
        var model = BuildModel();
        var placement = model.Tables.Single(t => t.LogicalName == "Operation.Placement");

        // HasConversion(new ValueConverter<Config, string>(...)) leaves GetProviderClrType() null (LES-0034).
        var config = placement.FindColumn("Config");
        Assert.NotNull(config);
        Assert.Equal(G9ESyncColumnType.String, config!.Type);
    }

    [Fact]
    public void Spatial_columns_travel_as_text_and_are_cast_back_on_write()
    {
        var model = BuildModel();
        var pot = model.Tables.Single(t => t.LogicalName == "Operation.Pot");

        var location = pot.FindColumn("Location");
        Assert.NotNull(location);
        Assert.Equal(G9ESyncColumnType.String, location!.Type);
        Assert.Equal("{row}.\"Location\"::text", location.ReadSql);
        Assert.Equal("{value}::geometry", location.WriteSql);
    }

    [Fact]
    public void Store_generated_columns_are_never_writable()
    {
        var model = BuildModel();
        var pot = model.Tables.Single(t => t.LogicalName == "Operation.Pot");

        var touched = pot.FindColumn("TouchedAt");
        Assert.NotNull(touched);
        Assert.False(touched!.IsWritable);

        var code = pot.FindColumn("code");
        Assert.NotNull(code);
        Assert.True(code!.IsWritable);
    }

    [Fact]
    public void Nullability_comes_from_the_model()
    {
        var model = BuildModel();
        var pot = model.Tables.Single(t => t.LogicalName == "Operation.Pot");

        Assert.False(pot.FindColumn("code")!.Nullable);
        Assert.True(pot.FindColumn("Note")!.Nullable);
    }

    [Fact]
    public void Excluded_properties_are_not_synced()
    {
        var model = BuildModel(ef => ef.Entity<Ridge>(e => e.Exclude("InternalNote", "Sensors")));
        var ridge = model.Tables.Single(t => t.LogicalName == "Operation.Ridge");

        Assert.Null(ridge.FindColumn("InternalNote"));
        Assert.Null(ridge.FindColumn("Sensors"));
        Assert.NotNull(ridge.FindColumn("Title"));
    }

    [Fact]
    public void Per_entity_overrides_are_applied()
    {
        var model = BuildModel();
        var placement = model.Tables.Single(t => t.LogicalName == "Operation.Placement");

        Assert.Equal(G9EConflictPolicy.ColumnMerge, placement.ConflictPolicy);
        Assert.True(placement.TracksColumnVersions);
    }

    [Fact]
    public void Indexes_including_the_partial_unique_claim_key_come_from_the_model()
    {
        using var context = new FarmContext();
        var indexes = G9CEfCoreModelExtractor.ExtractIndexes(context.Model, [typeof(Pot), typeof(Placement)]);

        var ridgeIndex = indexes.Single(i => i.LogicalName == "Operation.Pot" && i.Columns.Count == 1 && i.Columns[0] == "RidgeId");
        Assert.False(ridgeIndex.IsUnique);

        // A FILTERED unique index is a claim key: two claims on it in one batch is the defect that wedged a scope
        // permanently under the old engine, so the sync layer has to know the filter exists.
        var claimKey = indexes.Single(i => i.LogicalName == "Operation.Placement" && i.IsUnique);
        Assert.Equal(["PotId"], claimKey.Columns);
        Assert.False(string.IsNullOrEmpty(claimKey.Filter));
    }

    [Fact]
    public void A_composite_key_is_refused_with_both_column_names()
    {
        using var context = new FarmContext();
        var builder = new G9CSyncModelBuilder();

        var error = Assert.Throws<G9CSyncException>(() =>
            builder.FromEfCore(context.Model, ef => ef.Entity<PotTag>()));

        Assert.Equal(G9SyncErrorCodes.ModelInvalid, error.Code);
        Assert.Contains("composite primary key", error.Message, StringComparison.Ordinal);
        Assert.Contains("PotId", error.Message, StringComparison.Ordinal);
        Assert.Contains("TagId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmappable_property_names_the_property_and_the_two_ways_out()
    {
        using var context = new FarmContext();
        var builder = new G9CSyncModelBuilder();

        var error = Assert.Throws<G9CSyncException>(() =>
            builder.FromEfCore(context.Model, ef => ef.Entity<Ridge>()));

        Assert.Contains("Ridge.Sensors", error.Message, StringComparison.Ordinal);
        Assert.Contains("As(", error.Message, StringComparison.Ordinal);
        Assert.Contains("Exclude", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_type_override_rescues_an_unmappable_property()
    {
        var model = BuildModel(ef => ef.Entity<Ridge>(e => e
            .Exclude("InternalNote")
            .Property("Sensors", p => p.As(G9ESyncColumnType.String).ReadAs("array_to_string({row}.\"Sensors\", ',')"))));

        var sensors = model.Tables.Single(t => t.LogicalName == "Operation.Ridge").FindColumn("Sensors");
        Assert.NotNull(sensors);
        Assert.Equal(G9ESyncColumnType.String, sensors!.Type);
        Assert.Equal("array_to_string({row}.\"Sensors\", ',')", sensors.ReadSql);
    }

    [Fact]
    public void Every_problem_is_reported_at_once_not_one_per_run()
    {
        using var context = new FarmContext();
        var builder = new G9CSyncModelBuilder();

        var error = Assert.Throws<G9CSyncException>(() =>
            builder.FromEfCore(context.Model, ef =>
            {
                ef.Entity<PotTag>();   // composite key
                ef.Entity<Ridge>();    // unmappable property
            }));

        Assert.Contains("composite primary key", error.Message, StringComparison.Ordinal);
        Assert.Contains("Ridge.Sensors", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_owned_type_mapped_to_json_is_one_text_column_cast_back_to_its_store_type()
    {
        var model = BuildModel();
        var pot = model.Tables.Single(t => t.LogicalName == "Operation.Pot");

        // AgriPad's pilot found AttachmentCount (OwnsOne(...).ToJson(), jsonb NOT NULL) missing from the model (LES-0034).
        var counts = pot.FindColumn("Counts");
        Assert.NotNull(counts);
        Assert.Equal(G9ESyncColumnType.String, counts!.Type);
        Assert.False(counts.Nullable);   // a required owned document is a NOT NULL column
        Assert.Equal("{row}.\"Counts\"::text", counts.ReadSql);
        Assert.Equal("{value}::jsonb", counts.WriteSql);
    }

    [Fact]
    public void Owned_and_complex_types_split_into_the_table_contribute_their_columns()
    {
        var model = BuildModel();
        var pot = model.Tables.Single(t => t.LogicalName == "Operation.Pot");
        var placement = model.Tables.Single(t => t.LogicalName == "Operation.Placement");

        var width = pot.FindColumn("Dimensions_Width");
        Assert.NotNull(width);
        Assert.Equal(G9ESyncColumnType.Int32, width!.Type);
        Assert.False(width.Nullable);

        // An OPTIONAL owned type makes even its non-nullable properties nullable columns: nullability comes from the table.
        var at = placement.FindColumn("Audit_At");
        Assert.NotNull(at);
        Assert.Equal(G9ESyncColumnType.DateTime, at!.Type);
        Assert.True(at.Nullable);
        Assert.NotNull(placement.FindColumn("Audit_By"));
    }

    [Fact]
    public void Owned_documents_and_their_members_can_be_excluded_or_made_read_only()
    {
        var model = BuildModel(ef => ef.Entity<Ridge>(e => e.Exclude("Sensors")));
        Assert.NotNull(model.Tables.Single(t => t.LogicalName == "Operation.Pot").FindColumn("Counts"));

        using var context = new FarmContext();
        var builder = new G9CSyncModelBuilder();
        builder.FromEfCore(context.Model, ef =>
        {
            ef.Entity<Pot>(e => e.Exclude("Counts", "Dimensions.Length").Property("Dimensions.Width", p => p.ReadOnly()));
            ef.Entity<Placement>(e => e.Property("Audit", p => p.ReadOnly()).Exclude("Audit_By"));
        });
        builder.Subscription("all", s => s.Include("Operation.Pot", "true"));
        var built = builder.Build();
        var pot = built.Tables.Single(t => t.LogicalName == "Operation.Pot");

        Assert.Null(pot.FindColumn("Counts"));
        Assert.Null(pot.FindColumn("Dimensions_Length"));
        Assert.True(pot.FindColumn("Dimensions_Width")!.IsReadOnly);
        Assert.Null(built.Tables.Single(t => t.LogicalName == "Operation.Placement").FindColumn("Audit_By"));
    }

    [Fact]
    public async Task The_ef_writer_needs_a_converter_for_a_json_document_unless_the_server_owns_it()
    {
        await using var services = new ServiceCollection().AddDbContext<FarmContext>().BuildServiceProvider();

        using (var context = new FarmContext())
        {
            var writable = WriterTable(context, ef => ef.Entity<Pot>(e => e.Exclude("Location")));
            var writer = new G9CEfCoreRowWriter<FarmContext>([new G9CEfCoreWriteEntity(typeof(Pot))]);
            var problems = await writer.ValidateAsync(writable, services, CancellationToken.None);
            Assert.Contains(problems, p => p.Contains("JSON document of Pot.Counts", StringComparison.Ordinal));

            var converted = new G9CEfCoreWriteEntity(typeof(Pot)).Convert("Counts", json => System.Text.Json.JsonSerializer.Deserialize<TallyCount>((string)json!));
            Assert.Empty(await new G9CEfCoreRowWriter<FarmContext>([converted]).ValidateAsync(writable, services, CancellationToken.None));

            var serverOwned = WriterTable(context, ef => ef.Entity<Pot>(e => e.Exclude("Location").Property("Counts", p => p.ReadOnly())));
            Assert.Empty(await new G9CEfCoreRowWriter<FarmContext>([new G9CEfCoreWriteEntity(typeof(Pot))]).ValidateAsync(serverOwned, services, CancellationToken.None));
        }

        static G9CSyncServerTable WriterTable(FarmContext context, Action<G9CEfCoreSyncBuilder> configure)
        {
            var builder = new G9CSyncModelBuilder();
            builder.FromEfCore(context.Model, configure);
            builder.Subscription("all", s => s.Include("Operation.Pot", "true"));
            return builder.Build().Tables.Single(t => t.LogicalName == "Operation.Pot");
        }
    }

    [Fact]
    public void A_deferred_model_is_read_from_the_applications_own_context_registration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<FarmContext>();
        var built = 0;
        services.AddG9SyncDataServer((sp, model) =>
        {
            built++;
            model.FromEfCore<FarmContext>(sp, ef => ef.Entity<Pot>(e => e.Exclude("Location")))
                .Subscription("all", s => s.Include("Operation.Pot", "true"));
        });

        Assert.Equal(0, built);   // nothing is built at registration
        using var provider = services.BuildServiceProvider();
        var model = provider.GetRequiredService<G9CSyncServerModel>();
        Assert.Equal(1, built);
        Assert.NotNull(model.Tables.Single(t => t.LogicalName == "Operation.Pot").FindColumn("Counts"));
        Assert.Same(model, provider.GetRequiredService<G9CSyncServerModel>());
    }

    // ---- the miniature model under test -------------------------------------------------------

    private enum PotKind { Basin, Pot }

    private enum PotStatus { Empty, Planted }

    private sealed class Pot
    {
        public Guid Id { get; set; }
        public Guid RidgeId { get; set; }
        public string CodeProperty { get; set; } = string.Empty;
        public string? Note { get; set; }
        public PotKind Kind { get; set; }
        public PotStatus Status { get; set; }
        public string? Location { get; set; }
        public DateTime TouchedAt { get; set; }
        public TallyCount Counts { get; set; } = new();
        public PotDimensions Dimensions { get; set; } = new();
    }

    private sealed class TallyCount
    {
        public int Total { get; set; }
        public List<TallyProfile> ByProfiles { get; set; } = [];
    }

    private sealed class TallyProfile
    {
        public string Profile { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class PotDimensions
    {
        public int Width { get; set; }
        public int Length { get; set; }
    }

    private sealed class PlacementAudit
    {
        public string? By { get; set; }
        public DateTime At { get; set; }
    }

    private sealed class Placement
    {
        public Guid Id { get; set; }
        public Guid PotId { get; set; }
        public bool IsCurrent { get; set; }
        public PlacementAudit? Audit { get; set; }
        public PlacementConfig? Config { get; set; }
    }

    private sealed class PlacementConfig
    {
        public int Spacing { get; set; }
    }

    private sealed class Ridge
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? InternalNote { get; set; }
        public int[] Sensors { get; set; } = [];
    }

    private sealed class PotTag
    {
        public Guid PotId { get; set; }
        public Guid TagId { get; set; }
    }

    private sealed class FarmContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseNpgsql("Host=model-only;Database=model-only");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("Operation");

            modelBuilder.Entity<Pot>(e =>
            {
                e.ToTable("Pot");
                e.HasKey(x => x.Id);
                e.Property(x => x.CodeProperty).HasColumnName("code").IsRequired();
                e.Property(x => x.Kind).HasConversion<string>();
                e.Property(x => x.Location).HasColumnType("geometry");
                e.Property(x => x.TouchedAt).ValueGeneratedOnAddOrUpdate();
                e.HasIndex(x => x.RidgeId);
                e.OwnsOne(x => x.Counts, c =>
                {
                    c.ToJson();
                    c.OwnsMany(x => x.ByProfiles);
                });
                e.Navigation(x => x.Counts).IsRequired();
                e.ComplexProperty(x => x.Dimensions);
            });

            modelBuilder.Entity<Placement>(e =>
            {
                e.ToTable("Placement");
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.PotId).IsUnique().HasFilter("\"IsCurrent\" = true");
                e.OwnsOne(x => x.Audit);
                e.Property(x => x.Config).HasConversion(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<PlacementConfig?, string?>(
                    v => v == null ? null : v.Spacing.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    v => v == null ? null : new PlacementConfig { Spacing = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) }));
            });

            modelBuilder.Entity<Ridge>(e =>
            {
                e.ToTable("Ridge");
                e.HasKey(x => x.Id);
            });

            modelBuilder.Entity<PotTag>(e =>
            {
                e.ToTable("PotTag");
                e.HasKey(x => new { x.PotId, x.TagId });
            });
        }
    }
}
