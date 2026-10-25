using System.Runtime.CompilerServices;
using G9SyncData.Client.SignalR;
using G9SyncData.Client.Transport;

namespace G9SyncData.Tests.Client;

/// <summary>
///     The shared connect of <see cref="G9CSignalRSyncTransport" /> never leaves an unobserved fault behind (1.5.10). Reported from
///     AgriPad on 2026-10-25: a site switch canceled the sync that was connecting, the connect then failed with nobody waiting, and the
///     fault surfaced as <c>TaskScheduler.UnobservedTaskException</c> — which the app treats as a crash.
/// </summary>
public sealed class SignalRConnectFaultTests
{
    private const string Marker = "g9-connect-fault-test";

    [Fact]
    public async Task A_connect_that_fails_after_every_waiter_was_canceled_leaves_no_unobserved_exception()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.ToString().Contains(Marker, StringComparison.Ordinal)) Interlocked.Increment(ref unobserved);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var gate = new TaskCompletionSource();
            await CancelWhileConnectingAsync(gate);

            // The connect fails only now, after its one waiter has gone.
            gate.SetResult();
            await Task.Delay(500);

            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.Equal(0, Volatile.Read(ref unobserved));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    /// <summary>Kept out of line so nothing on the test's own stack still references the transport or its connect.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CancelWhileConnectingAsync(TaskCompletionSource gate)
    {
        var transport = new G9CSignalRSyncTransport(new G9CSignalRSyncTransportOptions
        {
            Url = "http://g9-connect-fault.invalid/g9sync",
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ConfigureHttpConnection = http => http.HttpMessageHandlerFactory = _ => new FailAfterGateHandler(gate)
        });

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ((IG9SyncTransport)transport).ConnectAsync(cancel.Token));
    }

    /// <summary>The negotiate request waits for the gate, then fails the way an unreachable server does.</summary>
    private sealed class FailAfterGateHandler(TaskCompletionSource gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await gate.Task.ConfigureAwait(false);
            throw new HttpRequestException(Marker);
        }
    }
}
