using System.Runtime.CompilerServices;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Resilience;

/// <summary>
///     The connection drops for real — Kestrel aborts the WebSocket mid-call, as a tablet losing coverage does — and the engine must
///     reconnect through the G9SignalRSuperNetCore client, resume what it had, and never apply anything twice or half.
/// </summary>
public sealed class SignalRConnectionLossTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_pull_cut_mid_stream_reconnects_and_resumes_from_its_continuation()
    {
        var cut = new ConnectionCutter(G9SyncProtocol.Methods.Pull, afterItems: 4);
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR,
            server: builder => builder.Services.Configure<HubOptions>(options => options.AddFilter(cut)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 600);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        var result = await client.SyncOkAsync();

        Assert.Equal(1, cut.Cuts);
        Assert.Equal(600, await client.CountAsync("Pot"));
        Assert.True(cut.ResumedWithContinuation, "The second pull must continue from the staged position, not start over.");
        Assert.Equal(601, result.Downloaded);   // sites + pots, each exactly once
    }

    [PostgresFact]
    public async Task A_push_whose_answer_is_lost_with_the_connection_is_applied_exactly_once()
    {
        var cut = new ConnectionCutter(G9SyncProtocol.Methods.Push, afterItems: 0, afterCompletion: true);
        var commits = new CommitCounter();
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR, server: builder =>
        {
            builder.Services.Configure<HubOptions>(options => options.AddFilter(cut));
            builder.Services.AddSingleton<IG9PostCommitHandler>(commits);
        });
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 10);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        var created = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'CUT-1', 0, 0)", Key(created), Key(site));
        await client.ExecAsync("UPDATE Pot SET Status = Status + 1 WHERE SiteId = $p0 AND Code LIKE 'P%'", Key(site));
        await client.SyncOkAsync();

        Assert.Equal(1, cut.Cuts);
        Assert.Equal(1, commits.PotCommits);   // the retried batch was recognised, not written again
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Code" = 'CUT-1'"""));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task A_connection_lost_between_runs_is_re_established_by_the_next_run()
    {
        var cut = new ConnectionCutter(G9SyncProtocol.Methods.Hello, afterItems: 0, afterCompletion: true);
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR,
            server: builder => builder.Services.Configure<HubOptions>(options => options.AddFilter(cut)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 5);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        await client.SyncOkAsync();                 // the first Hello's answer is lost with the connection: retried after reconnecting
        await harness.InsertPotAsync(Guid.NewGuid(), site, "LATER");
        await client.SyncOkAsync();
        Assert.Equal(1, cut.Cuts);
        Assert.Equal(6, await client.CountAsync("Pot"));
    }

    /// <summary>Counts committed pushes to the pot table.</summary>
    private sealed class CommitCounter : IG9PostCommitHandler
    {
        private int _potCommits;

        public int PotCommits => _potCommits;

        public bool AppliesTo(G9CSyncServerTable table) => table.LogicalName == "farm.Pot";

        public ValueTask HandleAsync(G9CPostCommitContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _potCommits);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Aborts the caller's connection once: after N streamed items of a streaming method, or after a unary call completed.</summary>
    private sealed class ConnectionCutter(string method, int afterItems, bool afterCompletion = false) : IHubFilter
    {
        private int _cuts;
        private int _calls;

        public int Cuts => _cuts;
        public bool ResumedWithContinuation { get; private set; }

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            if (!string.Equals(invocationContext.HubMethodName, method, StringComparison.Ordinal)) return await next(invocationContext);
            var call = Interlocked.Increment(ref _calls);
            if (call > 1 && method == G9SyncProtocol.Methods.Pull && invocationContext.HubMethodArguments.Count > 1 && invocationContext.HubMethodArguments[1] is not null)
            {
                ResumedWithContinuation = true;
            }

            var result = await next(invocationContext);
            if (_cuts > 0) return result;

            if (afterCompletion)
            {
                Interlocked.Increment(ref _cuts);
                invocationContext.Context.Abort();
                return result;
            }

            return result is IAsyncEnumerable<G9DtSyncChunk> stream ? CutAfter(stream, invocationContext.Context) : result;
        }

        private async IAsyncEnumerable<G9DtSyncChunk> CutAfter(IAsyncEnumerable<G9DtSyncChunk> source, HubCallerContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var delivered = 0;
            await foreach (var chunk in source.WithCancellation(cancellationToken))
            {
                if (delivered++ == afterItems && Interlocked.CompareExchange(ref _cuts, 1, 0) == 0)
                {
                    context.Abort();
                    yield break;
                }

                yield return chunk;
            }
        }
    }
}
