using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server;
using G9SyncData.Server.Auth;
using G9SyncData.Server.EfCore;
using G9SyncData.Server.Model;
using G9SyncData.Server.PostgreSql;
using G9SyncData.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Scopes;

/// <summary>The server model, auth provider and harness shared by the EF Core scope and key-set tests.</summary>
internal static class EfScopeFixture
{
    /// <summary>The DbContext is configured with a server that does not exist: a provider that EXECUTED its query would fail.</summary>
    public const string NeverOpened = "Host=g9sync-never-opened.invalid;Database=never;Timeout=1";

    public static Task<SyncHarness> CreateHarnessAsync(ITestOutputHelper output, Action<G9CSyncModelBuilder>? model = null, bool createMembers = true,
        Action<G9CPostgresSyncOptions>? postgres = null, Action<G9CSyncServerBuilder>? server = null) => SyncHarness.CreateAsync(output,
        model: model ?? Model,
        server: builder =>
        {
            builder.UseAuthContextProvider<EfScopeAuthProvider>();
            builder.Services.AddDbContext<FarmDbContext>(options => options.UseNpgsql(NeverOpened));
            server?.Invoke(builder);
        },
        seedBeforeCapture: async dataSource =>
        {
            if (!createMembers) return;
            await using var command = dataSource.CreateCommand(FarmDbContext.MembersSql);
            await command.ExecuteNonQueryAsync();
        },
        postgres: postgres);

    public static void Model(G9CSyncModelBuilder model)
    {
        ConfigureServer(model);
        model
            .Subscription("ef-allowed-pots", s => s
                .ServerParameter("AllowedSites", G9ESyncColumnType.Guid, isList: true)
                .IncludeQuery<FarmDbContext, FarmPot>("farm.Pot", (db, scope) =>
                {
                    var allowed = scope.Get<Guid[]>("AllowedSites") ?? [];
                    return db.Pots.IgnoreQueryFilters([FarmDbContext.HiddenFilter]).Where(p => allowed.Contains(p.SiteId));
                }, whenSql: "NOT {row}.\"IsDeleted\"")
                .DependsOn("sites"))
            .Subscription("ef-member-pots", s => s
                .ServerParameter("PermissionStamp", G9ESyncColumnType.String)
                .IncludeQuery<FarmDbContext, FarmPot>("farm.Pot", (db, scope) =>
                {
                    var user = scope.Auth.UserId;
                    return db.Pots.Where(p => db.Members.Any(m => m.SiteId == p.SiteId && m.UserId == user));
                }, whenSql: "NOT {row}.\"IsDeleted\"")
                .DependsOn("sites"))
            .Subscription("ef-shadowed", s => s
                .Parameter("SiteId", G9ESyncColumnType.Guid)
                .Parameter("OtherSiteId", G9ESyncColumnType.Guid)
                .IncludeQuery<FarmDbContext, FarmPot>("farm.Pot", (db, scope) =>
                {
#pragma warning disable IDE1006 // the name IS the test: it must equal the declared parameter's
                    var SiteId = scope.Get<Guid>("OtherSiteId");
                    return db.Pots.Where(p => p.SiteId == SiteId);
#pragma warning restore IDE1006
                })
                .DependsOn("sites"));
    }

    public static EfScopeAuthProvider Auth(SyncHarness harness) => (EfScopeAuthProvider)harness.Services.GetRequiredService<IG9SyncAuthContextProvider>();

    /// <summary>A store-level part request for <c>ef-allowed-pots</c> over <c>farm.Pot</c>, resolved the way the engine resolves one.</summary>
    public static G9CStorePartRequest AllowedPotsPart(SyncHarness harness, Guid[] allowedSites, G9EPullPartKind kind, string newSnapshot,
        string? previousSnapshot = null, string? afterKey = null)
    {
        var definition = harness.Engine.Model.FindSubscription("ef-allowed-pots")!;
        var include = definition.Tables.Single();
        var auth = new G9CSyncAuthContext
        {
            UserId = "tester",
            ServerParameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["AllowedSites"] = allowedSites }
        };
        return new G9CStorePartRequest
        {
            Table = include.Table,
            Include = include,
            Kind = kind,
            Columns = include.Table.Columns,
            Subscription = new G9CResolvedSubscription
            {
                Definition = definition,
                Key = definition.Name,
                Parameters = new G9CSyncParameters(),
                Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["AllowedSites"] = allowedSites },
                AuthHash = string.Join(',', allowedSites.Order()),
                Auth = auth
            },
            NewSnapshot = newSnapshot,
            PreviousSnapshot = previousSnapshot,
            AfterKey = afterKey,
            PageRows = 50
        };
    }
}

internal sealed class EfScopeAuthProvider : IG9SyncAuthContextProvider
{
    public Guid[]? AllowedSites { get; set; }

    public string PermissionStamp { get; set; } = "v1";

    public ValueTask<G9CSyncAuthContext> ResolveAsync(G9CSyncCaller caller, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new G9CSyncAuthContext
        {
            UserId = "tester",
            ServerParameters = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["AllowedSites"] = AllowedSites,
                ["PermissionStamp"] = PermissionStamp
            }
        });
}
