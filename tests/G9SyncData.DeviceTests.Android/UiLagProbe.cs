using Android.Views;

namespace G9SyncData.DeviceTests;

/// <summary>
///     What a user would feel while a sync runs: a Choreographer frame callback on the UI thread records the gap between consecutive
///     frames. On an idle screen the gap is one vsync; anything the sync does to the UI thread - posted continuations, stop-the-world
///     GC pauses, lock contention - shows up as long gaps. <see cref="Window" /> gives the statistics since the last call.
/// </summary>
public sealed class UiLagProbe : Java.Lang.Object, Choreographer.IFrameCallback
{
    private readonly Lock _lock = new();
    private long _lastFrameNanos;
    private int _frames;
    private double _maxGapMs;
    private int _over50;
    private int _over100;
    private int _over700;
    private double _sumGapMs;

    private UiLagProbe()
    {
    }

    public static UiLagProbe Start()
    {
        var probe = new UiLagProbe();
        Choreographer.Instance?.PostFrameCallback(probe);
        return probe;
    }

    public void DoFrame(long frameTimeNanos)
    {
        lock (_lock)
        {
            if (_lastFrameNanos != 0)
            {
                var gap = (frameTimeNanos - _lastFrameNanos) / 1_000_000d;
                _frames++;
                _sumGapMs += gap;
                if (gap > _maxGapMs) _maxGapMs = gap;
                if (gap > 50) _over50++;
                if (gap > 100) _over100++;
                if (gap > 700) _over700++;
            }

            _lastFrameNanos = frameTimeNanos;
        }

        Choreographer.Instance?.PostFrameCallback(this);
    }

    /// <summary>Frames, mean and max gap, and gaps over 50 / 100 / 700 ms since the previous call; resets the window.</summary>
    public (int Frames, double MeanGapMs, double MaxGapMs, int Over50, int Over100, int Over700) Window()
    {
        lock (_lock)
        {
            var result = (_frames, _frames == 0 ? 0 : _sumGapMs / _frames, _maxGapMs, _over50, _over100, _over700);
            _frames = 0;
            _sumGapMs = 0;
            _maxGapMs = 0;
            _over50 = _over100 = _over700 = 0;
            return result;
        }
    }
}
