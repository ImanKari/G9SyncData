using System.Diagnostics;
using System.Runtime.CompilerServices;
using G9SyncData.Client.SignalR;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Resilience;

/// <summary>
///     A connection that stays up while a call's answer never comes (ADR-0074). Seen 2026-10-19: a proxy corrupted the Hello, the server
///     never answered, its pings kept the connection alive, and the device waited at 89 % for ever. Every call now has a limit, every
///     stream an idle limit, and either one ending is a retryable network failure the engine recovers from.
/// </summary>
public sealed class SignalRTimeoutTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_hello_whose_answer_never_comes_times_out_and_the_retry_completes_the_sync()
    {
        var stall = new Staller(G9SyncProtocol.Methods.Hello, stallCalls: 1);
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR,
            server: builder => builder.Services.Configure<HubOptions>(options => options.AddFilter(stall)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 30);
        await using var client = await harness.CreateClientAsync(o => o.RetryBaseDelay = TimeSpan.FromMilliseconds(50),
            transportFactory: () => Transport(harness, o => o.CallTimeout = TimeSpan.FromSeconds(1)));
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        var watch = Stopwatch.StartNew();
        await client.SyncOkAsync();

        Assert.Equal(30, await client.CountAsync("Pot"));
        Assert.True(stall.Calls >= 2, "the stalled Hello must have been asked again");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"the sync waited {watch.Elapsed} - the call limit did not end the stalled Hello");
    }

    [PostgresFact]
    public async Task A_hello_that_never_answers_fails_the_run_as_a_network_failure_instead_of_waiting_for_ever()
    {
        var stall = new Staller(G9SyncProtocol.Methods.Hello, stallCalls: int.MaxValue);
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR,
            server: builder => builder.Services.Configure<HubOptions>(options => options.AddFilter(stall)));
        await using var client = await harness.CreateClientAsync(o =>
            {
                o.MaxRetries = 1;
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(50);
            },
            transportFactory: () => Transport(harness, o => o.CallTimeout = TimeSpan.FromSeconds(1)));
        await client.Engine.SubscribeAsync("sites");

        var result = await client.Engine.SyncAsync(new G9SyncData.Client.Runs.G9CSyncRequest());

        Assert.Equal(G9SyncData.Client.Runs.G9ESyncOutcome.Failed, result.Outcome);
        Assert.Equal(G9SyncErrorCodes.NetworkFailure, result.Error?.Code);
    }

    [PostgresFact]
    public async Task A_pull_stream_that_goes_silent_is_ended_by_its_idle_limit_and_resumes_from_its_continuation()
    {
        var stall = new Staller(G9SyncProtocol.Methods.Pull, stallCalls: 1, stallAfterItems: 2);
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR,
            server: builder => builder.Services.Configure<HubOptions>(options => options.AddFilter(stall)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 400);
        await using var client = await harness.CreateClientAsync(o => o.RetryBaseDelay = TimeSpan.FromMilliseconds(50),
            transportFactory: () => Transport(harness, o => o.StreamIdleTimeout = TimeSpan.FromSeconds(1)));
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        var result = await client.SyncOkAsync();

        Assert.Equal(400, await client.CountAsync("Pot"));
        Assert.Equal(401, result.Downloaded);   // sites + pots, each exactly once
        Assert.True(stall.Calls >= 2);
        Assert.True(stall.ResumedWithContinuation, "the second pull must continue from the staged position, not start over");
    }

    private static G9CSignalRSyncTransport Transport(SyncHarness harness, Action<G9CSignalRSyncTransportOptions> configure)
    {
        var options = new G9CSignalRSyncTransportOptions { Url = harness.HubUrl! };
        configure(options);
        return new G9CSignalRSyncTransport(options);
    }

    /// <summary>Holds the first <c>stallCalls</c> invocations of a method without an answer (or a stream after a few items).</summary>
    private sealed class Staller(string method, int stallCalls, int stallAfterItems = 0) : IHubFilter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public bool ResumedWithContinuation { get; private set; }

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            if (!string.Equals(invocationContext.HubMethodName, method, StringComparison.Ordinal)) return await next(invocationContext);
            var call = Interlocked.Increment(ref _calls);
            if (call > 1 && method == G9SyncProtocol.Methods.Pull && invocationContext.HubMethodArguments.Count > 1 &&
                invocationContext.HubMethodArguments[1] is not null)
            {
                ResumedWithContinuation = true;
            }

            if (call > stallCalls) return await next(invocationContext);

            if (method != G9SyncProtocol.Methods.Pull)
            {
                // The answer is lost: nothing comes back while the connection stays up and pings flow.
                await Task.Delay(TimeSpan.FromSeconds(30), invocationContext.Context.ConnectionAborted).ConfigureAwait(false);
                return await next(invocationContext);
            }

            var result = await next(invocationContext);
            return result is IAsyncEnumerable<G9DtSyncChunk> stream ? GoSilentAfter(stream) : result;
        }

        private async IAsyncEnumerable<G9DtSyncChunk> GoSilentAfter(IAsyncEnumerable<G9DtSyncChunk> source,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var delivered = 0;
            await foreach (var chunk in source.WithCancellation(cancellationToken))
            {
                if (delivered++ == stallAfterItems) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                yield return chunk;
            }
        }
    }
}
