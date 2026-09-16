using System.Diagnostics;

namespace G9SyncData.Client.Progress;

/// <summary>
///     Turns phase transitions and row/byte counts into monotonic progress (AiGuides/09-Progress-Model.md).
/// </summary>
/// <remarks>
///     <para>
///         The run is planned as a list of phases with EXPECTED durations (learned per device from past runs, or defaults).
///         Entering a phase allocates it a span of the REMAINING bar proportional to its expected share of the remaining
///         work; the part of the bar already covered is frozen. Monotonicity therefore holds by construction, even when
///         expectations change mid-run (e.g. once the pull plan reveals the row count).
///     </para>
///     <para>
///         Inside a phase, real counts drive the fraction; a phase with no signal yet (connect, plan) creeps toward 90% of its
///         span along an exponential curve scaled by its expected duration. A phase never reports its span complete until the
///         next phase starts, so 100% means finished.
///     </para>
/// </remarks>
internal sealed class G9CSyncProgressTracker : IDisposable
{
    private readonly object _lock = new();
    private readonly Guid _runId;
    private readonly Action<G9DtSyncProgress> _publish;
    private readonly Func<int> _queuedBehind;
    private readonly long _minIntervalTicks;
    private readonly List<PlanEntry> _plan = [];
    private readonly Dictionary<string, double> _durations = new(StringComparer.Ordinal);
    private readonly Stopwatch _runClock = Stopwatch.StartNew();
    private readonly Stopwatch _phaseClock = new();
    private readonly Timer _timer;

    private G9ESyncPhase _phase = G9ESyncPhase.Queued;
    private string? _postStep;
    private string? _table;
    private string? _subscription;
    private double _base;
    private double _span;
    private double _overall;
    private double _fraction;
    private double _phaseExpectedMs;
    private long _rowsDone;
    private long _rowsTotal;
    private long _bytesDone;
    private long _bytesTotal;
    private long _lastPublish;
    private bool _realSignal;
    private bool _terminal;
    private bool _cancelPending;
    private bool _disposed;

    private readonly Lock _publishLock = new();
    private G9DtSyncProgress? _lastPublished;

    public G9CSyncProgressTracker(Guid runId, Action<G9DtSyncProgress> publish, Func<int> queuedBehind, double maxHz)
    {
        _runId = runId;
        _publish = publish;
        _queuedBehind = queuedBehind;
        _minIntervalTicks = maxHz <= 0 ? 0 : (long)(Stopwatch.Frequency / maxHz);
        _timer = new Timer(_ => Creep(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public double Overall
    {
        get
        {
            lock (_lock) return _overall;
        }
    }

    /// <summary>Measured milliseconds per phase (post steps keyed <c>PostStep:name</c>).</summary>
    public IReadOnlyDictionary<string, double> Durations
    {
        get
        {
            lock (_lock) return new Dictionary<string, double>(_durations, StringComparer.Ordinal);
        }
    }

    /// <summary>Appends a phase to the plan with its expected duration.</summary>
    public void Plan(G9ESyncPhase phase, double expectedMs, string? name = null)
    {
        lock (_lock)
        {
            if (!_terminal) _plan.Add(new PlanEntry(phase, name, Math.Max(0, expectedMs)));
        }
    }

    /// <summary>Updates the expected duration of a planned phase that has not started yet.</summary>
    public void Expect(G9ESyncPhase phase, double expectedMs, string? name = null)
    {
        lock (_lock)
        {
            var entry = _plan.Find(e => e.Phase == phase && string.Equals(e.Name, name, StringComparison.Ordinal));
            if (entry is not null) entry.ExpectedMs = Math.Max(0, expectedMs);
        }
    }

    public void Enter(G9ESyncPhase phase, long rowsTotal = 0, long bytesTotal = 0, string? name = null)
    {
        G9DtSyncProgress? sample;
        lock (_lock)
        {
            if (_terminal || _disposed) return;
            CloseLocked();

            var index = _plan.FindIndex(e => e.Phase == phase && string.Equals(e.Name, name, StringComparison.Ordinal));
            var expected = 500d;
            if (index >= 0)
            {
                expected = _plan[index].ExpectedMs;
                _plan.RemoveRange(0, index + 1);
            }

            expected = Math.Max(1, expected);
            var remaining = _plan.Sum(e => e.ExpectedMs);
            _base = _overall;
            _span = (1 - _base) * expected / (expected + remaining);
            _phase = phase;
            _postStep = name;
            _rowsDone = 0;
            _rowsTotal = Math.Max(0, rowsTotal);
            _bytesDone = 0;
            _bytesTotal = Math.Max(0, bytesTotal);
            _fraction = 0;
            _realSignal = false;
            _phaseExpectedMs = expected;
            _phaseClock.Restart();
            _timer.Change(200, 200);
            sample = SampleLocked(force: true);
        }

        Emit(sample);
    }

    /// <summary>Reports absolute counts for the current phase.</summary>
    public void Report(long rowsDone, long bytesDone = -1, string? table = null, string? subscriptionKey = null)
    {
        G9DtSyncProgress? sample;
        lock (_lock)
        {
            if (_terminal || _disposed) return;
            _rowsDone = Math.Max(0, rowsDone);
            if (bytesDone >= 0) _bytesDone = bytesDone;
            if (table is not null) _table = table;
            if (subscriptionKey is not null) _subscription = subscriptionKey;
            var fraction = _rowsTotal > 0 ? (double)_rowsDone / _rowsTotal
                : _bytesTotal > 0 ? (double)_bytesDone / _bytesTotal
                : _fraction;
            _realSignal = _rowsTotal > 0 || _bytesTotal > 0;
            AdvanceLocked(fraction);
            sample = SampleLocked(force: false);
        }

        Emit(sample);
    }

    /// <summary>Reports a 0..1 fraction of the current phase (post steps).</summary>
    public void ReportFraction(double fraction)
    {
        G9DtSyncProgress? sample;
        lock (_lock)
        {
            if (_terminal || _disposed) return;
            _realSignal = true;
            AdvanceLocked(fraction);
            sample = SampleLocked(force: false);
        }

        Emit(sample);
    }

    public void SetCancelPending()
    {
        G9DtSyncProgress? sample;
        lock (_lock)
        {
            if (_terminal || _disposed) return;
            _cancelPending = true;
            sample = SampleLocked(force: true);
        }

        Emit(sample);
    }

    public void Complete(G9ESyncPhase terminal)
    {
        G9DtSyncProgress? sample;
        lock (_lock)
        {
            if (_terminal || _disposed) return;
            CloseLocked();
            _terminal = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            if (terminal == G9ESyncPhase.Completed)
            {
                _overall = 1;
                _fraction = 1;
            }

            _phase = terminal;
            sample = SampleLocked(force: true);
        }

        Emit(sample);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _timer.Dispose();
    }

    private void Creep()
    {
        G9DtSyncProgress? sample;
        lock (_lock)
        {
            if (_terminal || _disposed || _realSignal || _phase == G9ESyncPhase.Queued) return;
            var elapsed = _phaseClock.Elapsed.TotalMilliseconds;
            AdvanceLocked(0.9 * (1 - Math.Exp(-elapsed / Math.Max(100, _phaseExpectedMs))));
            sample = SampleLocked(force: false);
        }

        Emit(sample);
    }

    private void AdvanceLocked(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 0.995);
        if (fraction > _fraction) _fraction = fraction;
        var overall = Math.Min(0.999, _base + (_span * _fraction));
        if (overall > _overall) _overall = overall;
    }

    private void CloseLocked()
    {
        if (_phase is G9ESyncPhase.Queued or G9ESyncPhase.Completed or G9ESyncPhase.Canceled or G9ESyncPhase.Failed) return;
        var key = _phase == G9ESyncPhase.PostStep ? "PostStep:" + _postStep : _phase.ToString();
        _durations[key] = _durations.GetValueOrDefault(key) + _phaseClock.Elapsed.TotalMilliseconds;
        _overall = Math.Max(_overall, Math.Min(0.999, _base + _span));
    }

    private G9DtSyncProgress? SampleLocked(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && now - _lastPublish < _minIntervalTicks) return null;
        _lastPublish = now;

        var phaseSeconds = Math.Max(0.001, _phaseClock.Elapsed.TotalSeconds);
        var rowsPerSecond = _rowsDone / phaseSeconds;
        TimeSpan? eta = null;
        if (_terminal)
        {
            eta = TimeSpan.Zero;
        }
        else if (_overall > 0.02)
        {
            eta = TimeSpan.FromMilliseconds(_runClock.Elapsed.TotalMilliseconds * (1 - _overall) / _overall);
        }

        return new G9DtSyncProgress(_runId, _phase, _overall, _fraction, _rowsDone, _rowsTotal, _bytesDone, _bytesTotal, _subscription, _table,
            rowsPerSecond, eta, SafeQueued(), _postStep, _cancelPending);
    }

    private int SafeQueued()
    {
        try
        {
            return _queuedBehind();
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>
    ///     Samples are taken under the state lock but published outside it, so the creep timer and the run thread can race.
    ///     Publishing is serialised and a sample older than the last published one (lower overall, or an earlier phase) is
    ///     dropped, so consumers always see a monotonic stream.
    /// </summary>
    private void Emit(G9DtSyncProgress? sample)
    {
        if (sample is null) return;
        lock (_publishLock)
        {
            if (_lastPublished is { } last && (sample.Overall < last.Overall || sample.Phase < last.Phase)) return;
            _lastPublished = sample;
            try
            {
                _publish(sample);
            }
            catch (Exception)
            {
                // A progress consumer must never break the sync.
            }
        }
    }

    private sealed class PlanEntry(G9ESyncPhase phase, string? name, double expectedMs)
    {
        public G9ESyncPhase Phase { get; } = phase;
        public string? Name { get; } = name;
        public double ExpectedMs { get; set; } = expectedMs;
    }
}
