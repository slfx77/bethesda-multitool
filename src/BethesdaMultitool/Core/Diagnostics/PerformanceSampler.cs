namespace BethesdaMultitool.Core.Diagnostics;

/// <summary>
///     Pacing statistics for a window of frame durations: not just how fast the renderer runs on
///     average, but how evenly. A 40 ms median with a 166 ms worst frame is a very different thing
///     to look at than a steady 40 ms, and an average alone cannot tell the two apart.
///     <para>
///         <see cref="OnePercentLowFps" /> is the figure that exposes hitching. It is the average
///         frame rate of the slowest 1% of frames — the common gaming definition — chosen over the
///         other convention (the 1st-percentile single frame) because averaging the tail is stable
///         at the small sample counts a 2-second profile window produces.
///     </para>
/// </summary>
internal readonly record struct PerformanceSnapshot(
    int SampleCount,
    double AverageMilliseconds,
    double MedianMilliseconds,
    double Percentile99Milliseconds,
    double MaxMilliseconds,
    double AverageFps,
    double MedianFps,
    double OnePercentLowFps)
{
    /// <summary>The empty window: every figure zero, so callers need no null branch.</summary>
    public static PerformanceSnapshot Empty => default;
}

/// <summary>
///     Collects frame durations and turns them into a <see cref="PerformanceSnapshot" />. Pure and
///     GUI-free so the live HUD, the headless profiler and the tests all read one definition of
///     "p99" rather than three — the renderer previously computed a median inline inside
///     <c>App/</c>, where the test project cannot reach it.
///     <para>
///         Not thread-safe: feed it from the render thread, the way frame timing is already
///         collected.
///     </para>
/// </summary>
internal sealed class PerformanceSampler
{
    /// <summary>
    ///     Frames retained. At 60 FPS this is ~17 s of history — long enough that a hitch stays
    ///     visible in the 1% low for a few seconds after it happens, rather than vanishing before it
    ///     can be read off the screen.
    /// </summary>
    public const int DefaultCapacity = 1024;

    private readonly double[] _samples;
    private readonly double[] _sorted;
    private int _count;
    private int _next;

    public PerformanceSampler(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Must be > 0.");
        }

        _samples = new double[capacity];
        _sorted = new double[capacity];
    }

    /// <summary>Frames currently retained (saturates at the capacity).</summary>
    public int Count => _count;

    /// <summary>
    ///     Records one frame's wall-clock duration. Non-finite and non-positive values are ignored:
    ///     a zero would read as infinite FPS and a NaN would poison every percentile in the window.
    /// </summary>
    public void Add(double frameMilliseconds)
    {
        if (!double.IsFinite(frameMilliseconds) || frameMilliseconds <= 0)
        {
            return;
        }

        _samples[_next] = frameMilliseconds;
        _next = (_next + 1) % _samples.Length;
        if (_count < _samples.Length)
        {
            _count++;
        }
    }

    public void Reset()
    {
        _count = 0;
        _next = 0;
    }

    /// <summary>Computes the current window's statistics. O(n log n); call once per display update.</summary>
    public PerformanceSnapshot Snapshot()
    {
        if (_count == 0)
        {
            return PerformanceSnapshot.Empty;
        }

        Array.Copy(_samples, _sorted, _count);
        var window = _sorted.AsSpan(0, _count);
        window.Sort();

        double total = 0;
        foreach (var value in window)
        {
            total += value;
        }

        var average = total / _count;
        var median = Median(window);
        var p99 = NearestRank(window, 0.99);
        var max = window[^1];

        // Slowest 1% of frames, at least one, averaged then converted — see the type docs for why
        // this convention rather than the single 1st-percentile frame.
        var tailCount = Math.Max(1, (int)Math.Ceiling(_count * 0.01));
        double tailTotal = 0;
        for (var i = _count - tailCount; i < _count; i++)
        {
            tailTotal += window[i];
        }

        var tailAverage = tailTotal / tailCount;

        return new PerformanceSnapshot(
            _count,
            average,
            median,
            p99,
            max,
            ToFps(average),
            ToFps(median),
            ToFps(tailAverage));
    }

    /// <summary>Frame duration in milliseconds → frames per second, guarding a zero divisor.</summary>
    public static double ToFps(double milliseconds)
    {
        return milliseconds > 0 ? 1000.0 / milliseconds : 0;
    }

    private static double Median(ReadOnlySpan<double> sorted)
    {
        var mid = sorted.Length / 2;
        return (sorted.Length & 1) != 0
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) * 0.5;
    }

    /// <summary>
    ///     Nearest-rank percentile: the smallest value at or below which <paramref name="fraction" />
    ///     of the window falls. Chosen over interpolation so the reported figure is always a frame
    ///     that actually occurred.
    /// </summary>
    private static double NearestRank(ReadOnlySpan<double> sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
