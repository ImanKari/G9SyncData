using System.Globalization;
using System.Reflection;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Conflicts;

/// <summary>
///     LES-0075: last-writer-wins compares a device's edit time with the time the DATABASE stamped a server change with. The device
///     maps its clock onto the server's through Hello, so Hello must report the database's clock - not the app server's, which on
///     another machine disagrees with it by whatever the two machines' clocks disagree by.
/// </summary>
public sealed class ClockTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Hello_reports_the_database_clock()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var before = await DatabaseNowAsync(harness);
        var hello = await harness.Engine.HelloAsync(G9CSyncCaller.Anonymous, new G9DtHelloRequest { ClientId = Guid.NewGuid() });
        var after = await DatabaseNowAsync(harness);

        output.WriteLine($"database {before:HH:mm:ss.fff} .. {after:HH:mm:ss.fff}, hello {hello.ServerTimeUtc:HH:mm:ss.fff}, app server {DateTime.UtcNow:HH:mm:ss.fff}");
        Assert.Equal(DateTimeKind.Utc, hello.ServerTimeUtc.Kind);
        Assert.InRange(hello.ServerTimeUtc, before, after);
    }

    [PostgresFact]
    public async Task A_device_keeps_the_offset_of_the_database_clock_measured_at_the_middle_of_its_round_trip()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateSubscribedClientAsync(site);

        // The device's view of the server clock, against the database clock read here, bracketed by the host clock.
        var offset = long.Parse((string)(await client.ScalarAsync("SELECT value FROM g9_meta WHERE key = 'server_offset_ms'"))!, CultureInfo.InvariantCulture);
        var hostBefore = DateTime.UtcNow;
        var database = await DatabaseNowAsync(harness);
        var hostAfter = DateTime.UtcNow;
        var databaseMinusHost = (database - (hostBefore + ((hostAfter - hostBefore) / 2))).TotalMilliseconds;
        output.WriteLine($"device offset {offset} ms, database - host {databaseMinusHost:0} ms (±{(hostAfter - hostBefore).TotalMilliseconds / 2:0})");

        // Both measure the same thing - how far the database clock is from this machine's - and must agree within a round trip. The
        // margin is generous on purpose: under a loaded test run a Hello's legs are not symmetric, and a flaky test proves nothing.
        // That the engine reports the store's clock at all is pinned exactly by Hello_takes_its_time_from_the_stores_clock_not_the_app_servers.
        Assert.InRange(offset, databaseMinusHost - 200, databaseMinusHost + 200);
    }

    /// <summary>
    ///     The engine must take Hello's time from its store's clock, whatever this machine's says: a store whose clock is two hours
    ///     ahead (a database on another machine with its clock wrong) is what Hello reports.
    /// </summary>
    [PostgresFact]
    public async Task Hello_takes_its_time_from_the_stores_clock_not_the_app_servers()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s => s.Services.AddSingleton<IG9SyncServerStore>(sp =>
            ClockShift.Wrap(sp.GetRequiredService<G9SyncData.Server.PostgreSql.G9CPostgresSyncStore>(), TimeSpan.FromHours(2))));
        var hello = await harness.Engine.HelloAsync(G9CSyncCaller.Anonymous, new G9DtHelloRequest { ClientId = Guid.NewGuid() });
        Assert.InRange(hello.ServerTimeUtc - DateTime.UtcNow, TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1), TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));
    }

    /// <summary>A store whose clock reads <c>shift</c> ahead; everything else is the real store.</summary>
    public class ClockShift : DispatchProxy
    {
        private IG9SyncServerStore _inner = null!;
        private TimeSpan _shift;

        public static IG9SyncServerStore Wrap(IG9SyncServerStore inner, TimeSpan shift)
        {
            var proxy = Create<IG9SyncServerStore, ClockShift>();
            ((ClockShift)(object)proxy)._inner = inner;
            ((ClockShift)(object)proxy)._shift = shift;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IG9SyncServerStore.GetClockAsync))
            {
                return ShiftAsync((Task<DateTime>)targetMethod.Invoke(_inner, args)!);
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }
        }

        private async Task<DateTime> ShiftAsync(Task<DateTime> clock) => await clock + _shift;
    }

    private static async Task<DateTime> DatabaseNowAsync(SyncHarness harness) =>
        (await harness.ScalarServerAsync<DateTime>("SELECT clock_timestamp()")).ToUniversalTime();
}
