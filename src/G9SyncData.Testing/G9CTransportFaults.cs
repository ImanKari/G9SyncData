namespace G9SyncData.Testing;

/// <summary>The protocol operations of <see cref="G9CInMemorySyncTransport" />.</summary>
public enum G9ETransportOperation : byte
{
    Connect = 0,
    Hello = 1,
    Push = 2,
    PlanPull = 3,
    Pull = 4,
    Reconcile = 5,
    FetchRows = 6
}

/// <summary>
///     Scripted transport failures. Every fault is consumed once per affected call, so a test can say "the first push
///     loses its acknowledgement" and assert that the retry is idempotent. Thread-safe.
/// </summary>
public sealed class G9CTransportFaults
{
    private readonly Lock _lock = new();
    private readonly Dictionary<G9ETransportOperation, int> _failBefore = [];
    private readonly Dictionary<G9ETransportOperation, int> _failAfter = [];
    private readonly Dictionary<G9ETransportOperation, Queue<int>> _breakAfter = [];

    /// <summary>Delay added before every call and every streamed chunk.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>The next <paramref name="times" /> calls fail before reaching the server (nothing happened server-side).</summary>
    public G9CTransportFaults FailBefore(G9ETransportOperation operation, int times = 1)
    {
        lock (_lock) _failBefore[operation] = _failBefore.GetValueOrDefault(operation) + times;
        return this;
    }

    /// <summary>
    ///     The next <paramref name="times" /> unary calls reach the server and complete there, then the response is lost.
    ///     For a push this is "applied but not acknowledged".
    /// </summary>
    public G9CTransportFaults FailAfter(G9ETransportOperation operation, int times = 1)
    {
        lock (_lock) _failAfter[operation] = _failAfter.GetValueOrDefault(operation) + times;
        return this;
    }

    /// <summary>The next <paramref name="times" /> streams of the operation break after delivering <paramref name="chunks" /> chunks.</summary>
    public G9CTransportFaults BreakStreamAfter(G9ETransportOperation operation, int chunks, int times = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunks);
        lock (_lock)
        {
            if (!_breakAfter.TryGetValue(operation, out var queue)) _breakAfter[operation] = queue = new Queue<int>();
            for (var i = 0; i < times; i++) queue.Enqueue(chunks);
        }

        return this;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _failBefore.Clear();
            _failAfter.Clear();
            _breakAfter.Clear();
        }
    }

    internal bool TakeFailBefore(G9ETransportOperation operation) => Take(_failBefore, operation);

    internal bool TakeFailAfter(G9ETransportOperation operation) => Take(_failAfter, operation);

    /// <summary>Chunks to deliver before breaking, or -1 for no break.</summary>
    internal int TakeStreamBreak(G9ETransportOperation operation)
    {
        lock (_lock) return _breakAfter.TryGetValue(operation, out var queue) && queue.Count > 0 ? queue.Dequeue() : -1;
    }

    private bool Take(Dictionary<G9ETransportOperation, int> faults, G9ETransportOperation operation)
    {
        lock (_lock)
        {
            var remaining = faults.GetValueOrDefault(operation);
            if (remaining <= 0) return false;
            faults[operation] = remaining - 1;
            return true;
        }
    }
}
