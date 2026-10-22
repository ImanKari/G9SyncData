using G9SyncData.Client.Runs;
using G9SyncData.Client.SignalR;
using G9SyncData.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Resilience;

/// <summary>
///     How the real transport says what went wrong. A device decides from the code alone whether to try again, so each failure must
///     carry the right one: a server that is not there is worth retrying, a refused credential is not (retrying it only hammers the
///     server until the user signs in again), and a server's internal failure is retryable but must not carry its internals.
/// </summary>
public sealed class SignalRErrorPathTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_server_that_is_not_there_is_a_retryable_network_failure()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        await using var client = await harness.CreateClientAsync(
            configure: o => (o.MaxRetries, o.RetryBaseDelay) = (1, TimeSpan.FromMilliseconds(20)),
            transportFactory: () => new G9CSignalRSyncTransport(new G9CSignalRSyncTransportOptions
            {
                Url = "http://127.0.0.1:9/g9sync",          // discard port: nothing listens
                ConnectTimeout = TimeSpan.FromSeconds(3)
            }, NullLogger<G9CSignalRSyncTransport>.Instance));
        await client.Engine.SubscribeAsync("sites");

        var result = await client.SyncAsync();

        Assert.Equal(G9ESyncOutcome.Failed, result.Outcome);
        Assert.Equal(G9SyncErrorCodes.NetworkFailure, result.Error?.Code);
        Assert.True(result.Error!.Retryable);
    }

    [PostgresFact]
    public async Task A_refused_credential_is_unauthorized_and_not_retried()
    {
        var refused = 0;
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR, pipeline: app => app.Use(async (context, next) =>
        {
            var token = context.Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(token)) token = "Bearer " + context.Request.Query["access_token"];
            if (token != "Bearer good")
            {
                Interlocked.Increment(ref refused);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        }));

        string? credential = "bad";
        await using var client = await harness.CreateClientAsync(
            configure: o => (o.MaxRetries, o.RetryBaseDelay) = (3, TimeSpan.FromMilliseconds(20)),
            transportFactory: () => new G9CSignalRSyncTransport(new G9CSignalRSyncTransportOptions
            {
                Url = harness.HubUrl!,
                AccessTokenProvider = () => Task.FromResult<string?>(credential)
            }, NullLogger<G9CSignalRSyncTransport>.Instance));
        await client.Engine.SubscribeAsync("sites");

        var result = await client.SyncAsync();
        Assert.Equal(G9ESyncOutcome.Failed, result.Outcome);
        Assert.Equal(G9SyncErrorCodes.Unauthorized, result.Error?.Code);
        Assert.False(result.Error!.Retryable);
        Assert.Equal(1, refused);   // asked once: three retries of a refused credential would only hammer the server

        // Signed in again: the same transport goes through.
        credential = "good";
        await client.SyncOkAsync();
    }

    [PostgresFact]
    public async Task An_unexpected_server_failure_is_a_retryable_server_error_that_carries_none_of_its_internals()
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR, server: s => s.AddValidator<ThrowingPotValidator>());
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateClientAsync(configure: o => (o.MaxRetries, o.RetryBaseDelay) = (1, TimeSpan.FromMilliseconds(20)));
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'X', 0, 0)", Key(Guid.NewGuid()), Key(site));

        var result = await client.SyncAsync();

        Assert.Equal(G9ESyncOutcome.Failed, result.Outcome);
        Assert.Equal(G9SyncErrorCodes.ServerError, result.Error?.Code);
        Assert.True(result.Error!.Retryable);
        Assert.DoesNotContain(ThrowingPotValidator.Message, result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), result.Error.ToString(), StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task A_change_handler_that_throws_on_the_device_does_not_break_the_connection()
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        var pokes = 0;
        client.Transport.ChangesAvailable += _ =>
        {
            Interlocked.Increment(ref pokes);
            throw new InvalidOperationException("an app's handler failed");
        };

        await harness.InsertPotAsync(Guid.NewGuid(), site, "POKED");
        for (var i = 0; i < 100 && Volatile.Read(ref pokes) == 0; i++) await Task.Delay(100);

        Assert.True(pokes > 0, "No poke arrived; the test proves nothing.");
        Assert.True(client.Transport.IsConnected);
        await client.SyncOkAsync();
        Assert.Equal(1L, await client.CountAsync("Pot"));
    }
}
