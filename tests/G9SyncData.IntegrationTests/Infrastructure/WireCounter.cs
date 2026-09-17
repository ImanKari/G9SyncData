using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;

namespace G9SyncData.IntegrationTests.Infrastructure;

/// <summary>
///     Bytes that crossed the harness server's sockets, counted below HTTP and WebSocket framing (a Kestrel connection middleware wraps
///     the transport pipe): what a device would pay for on its data plan.
/// </summary>
public sealed class WireCounter
{
    private long _received;
    private long _sent;

    public long Received => Interlocked.Read(ref _received);

    public long Sent => Interlocked.Read(ref _sent);

    public void Reset()
    {
        Interlocked.Exchange(ref _received, 0);
        Interlocked.Exchange(ref _sent, 0);
    }

    internal Func<ConnectionDelegate, ConnectionDelegate> Middleware => next => async context =>
    {
        var original = context.Transport;
        context.Transport = new CountingPipe(original, this);
        try
        {
            await next(context);
        }
        finally
        {
            context.Transport = original;
        }
    };

    private sealed class CountingPipe(IDuplexPipe inner, WireCounter counter) : IDuplexPipe
    {
        public PipeReader Input { get; } = new CountingReader(inner.Input, counter);

        public PipeWriter Output { get; } = new CountingWriter(inner.Output, counter);
    }

    private sealed class CountingReader(PipeReader inner, WireCounter counter) : PipeReader
    {
        private ReadOnlySequence<byte> _last;

        public override void AdvanceTo(SequencePosition consumed)
        {
            Interlocked.Add(ref counter._received, _last.Slice(0, consumed).Length);
            inner.AdvanceTo(consumed);
        }

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            Interlocked.Add(ref counter._received, _last.Slice(0, consumed).Length);
            inner.AdvanceTo(consumed, examined);
        }

        public override void CancelPendingRead() => inner.CancelPendingRead();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadAsync(cancellationToken);
            _last = result.Buffer;
            return result;
        }

        public override bool TryRead(out ReadResult result)
        {
            if (!inner.TryRead(out result)) return false;
            _last = result.Buffer;
            return true;
        }
    }

    private sealed class CountingWriter(PipeWriter inner, WireCounter counter) : PipeWriter
    {
        public override void Advance(int bytes)
        {
            Interlocked.Add(ref counter._sent, bytes);
            inner.Advance(bytes);
        }

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    }
}
