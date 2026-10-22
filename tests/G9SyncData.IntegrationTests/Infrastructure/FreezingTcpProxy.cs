using System.Net;
using System.Net.Sockets;

namespace G9SyncData.IntegrationTests.Infrastructure;

/// <summary>
///     A TCP relay in front of a server that can make every connection open at that moment HALF-OPEN: still connected on both ends, but
///     nothing it carries arrives any more and no reset is ever sent - what a network partition, a dropped NAT entry or a failover that
///     never told the client looks like. Connections opened afterwards relay normally.
/// </summary>
internal sealed class FreezingTcpProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Link> _links = [];
    private readonly Task _accept;

    public FreezingTcpProxy(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Every connection open now stops carrying data, for good, without being closed.</summary>
    public int FreezeOpenConnections()
    {
        lock (_links)
        {
            foreach (var link in _links) link.Frozen = true;
            return _links.Count;
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _ = RelayAsync(client);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using var upstream = new TcpClient();
        var link = new Link();
        try
        {
            await upstream.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            lock (_links) _links.Add(link);
            await Task.WhenAny(PumpAsync(client.GetStream(), upstream.GetStream(), link), PumpAsync(upstream.GetStream(), client.GetStream(), link));
        }
        catch (Exception)
        {
            // a side went away
        }
        finally
        {
            // A frozen link is never closed: the other ends must not learn it is gone. It is dropped only when the proxy stops.
            if (!link.Frozen) client.Dispose();
        }
    }

    private async Task PumpAsync(Stream from, Stream to, Link link)
    {
        var buffer = new byte[16 * 1024];
        while (!_stop.IsCancellationRequested)
        {
            var read = await from.ReadAsync(buffer, _stop.Token);
            if (read == 0) return;
            if (link.Frozen)
            {
                // Swallow everything from now on and keep both sockets open.
                await Task.Delay(Timeout.Infinite, _stop.Token);
            }

            await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accept;
        }
        catch (Exception)
        {
            // stopping
        }

        _stop.Dispose();
    }

    private sealed class Link
    {
        public volatile bool Frozen;
    }
}
