using System.Collections.Concurrent;
using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Load;

/// <summary>
///     Admission (ADR-0062, LES-0074): every door that holds a database connection is bounded, so a fleet larger than the pool is
///     answered "busy" - fast, retryable, spread out by the devices' own backoff - and never by a request that waited inside the
///     connection pool until it timed out.
/// </summary>
public sealed class AdmissionTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_storm_beyond_the_connection_pool_is_refused_as_busy_and_never_fails_waiting_for_a_connection()
    {
        // A pool of 10 that gives up after 1 s, and limits that fit it (3 + 2 + 2 = 7). The subscription's scope costs 0.2 s per
        // query (one sub-select, not per row): a plan's counts then hold a connection the way a real farm-sized scope does, so
        // without admission 120 plans would queue inside the pool past its timeout.
        await using var harness = await SyncHarness.CreateAsync(output,
            model: m =>
            {
                ConfigureServer(m);
                m.Subscription("slow-pots", s => s
                    .Parameter("SiteId", G9SyncData.G9ESyncColumnType.Guid)
                    .Include("farm.Pot", "{row}.\"SiteId\" = @SiteId AND (SELECT pg_sleep(0.2)) IS NOT NULL", "NOT {row}.\"IsDeleted\"")
                    .DependsOn("sites"));
            },
            configure: o =>
            {
                o.MaxConcurrentPulls = 3;
                o.MaxConcurrentPushes = 2;
                o.MaxConcurrentPlans = 2;
                o.AdmissionWait = TimeSpan.FromMilliseconds(100);
            },
            connection: c =>
            {
                c.MaxPoolSize = 10;
                c.Timeout = 1;
            });
        // A site per device: a plan's counts are cached per scope, so devices sharing one would plan from the cache after the first.
        const int Devices = 120;
        var sites = new Guid[Devices];
        for (var i = 0; i < Devices; i++)
        {
            sites[i] = Guid.NewGuid();
            await harness.InsertSiteAsync(sites[i], "Storm " + i);
            await harness.InsertPotsAsync(sites[i], 3);
        }

        var clients = new SyncClient[Devices];
        for (var i = 0; i < Devices; i++)
        {
            clients[i] = await harness.CreateClientAsync(configure: o =>
            {
                o.MaxRetries = 1;
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(20);
            });
            await clients[i].Engine.SubscribeAsync("sites");
            await clients[i].Engine.SubscribeAsync("slow-pots", SitePots(sites[i]));
        }

        var codes = new ConcurrentDictionary<string, int>();
        await Task.WhenAll(clients.Select(client => Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var result = await client.SyncAsync();
                if (result.Outcome == G9ESyncOutcome.Succeeded) return;
                codes.AddOrUpdate(result.Error?.Code ?? result.Outcome.ToString(), 1, (_, n) => n + 1);
                await Task.Delay(Random.Shared.Next(20, 200));
            }
        })));

        output.WriteLine("failed runs by cause: " + string.Join(", ", codes.Select(kv => $"{kv.Key} x{kv.Value}")));
        Assert.True(codes.Keys.All(code => code == G9SyncErrorCodes.ServerBusy),
            "Only 'busy' may refuse a storm; anything else is a request that held on for a connection: " + string.Join(", ", codes.Keys));
        Assert.True(codes.ContainsKey(G9SyncErrorCodes.ServerBusy), "The storm never reached the limits; the test proves nothing.");
        foreach (var client in clients) Assert.Equal(3L, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task The_store_warns_at_startup_when_admission_can_exceed_the_connection_pool()
    {
        var warnings = new ConcurrentBag<string>();
        await using (var harness = await SyncHarness.CreateAsync(output,
                         configure: o => (o.MaxConcurrentPulls, o.MaxConcurrentPushes, o.MaxConcurrentPlans) = (20, 10, 10),
                         connection: c => c.MaxPoolSize = 30,
                         server: s => s.Services.AddLogging(l => l.AddProvider(new CapturingLoggerProvider(warnings)))))
        {
            Assert.Contains(warnings, w => w.Contains("Maximum Pool Size", StringComparison.Ordinal));
        }

        warnings.Clear();
        await using (var harness = await SyncHarness.CreateAsync(output,
                         configure: o => (o.MaxConcurrentPulls, o.MaxConcurrentPushes, o.MaxConcurrentPlans) = (10, 5, 5),
                         connection: c => c.MaxPoolSize = 30,
                         server: s => s.Services.AddLogging(l => l.AddProvider(new CapturingLoggerProvider(warnings)))))
        {
            Assert.DoesNotContain(warnings, w => w.Contains("Maximum Pool Size", StringComparison.Ordinal));
        }
    }

    private sealed class CapturingLoggerProvider(ConcurrentBag<string> warnings) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(warnings);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentBag<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning) warnings.Add(formatter(state, exception));
            }
        }
    }
}
