namespace BethesdaMultitool.Core.Resources;

/// <summary>
///     Converts the 3D viewer's per-frame streaming allowances (decode starts, mesh/texture upload
///     counts, bytes, wall-clock upload time) from frame-based to TIME-based pacing. The shipped
///     per-frame values are calibrated for a 60fps loop; when frames arrive slower (GPU contention
///     from another app, dense scenes, occluded window), multiplying each allowance by
///     <c>frameSeconds / (1/60)</c> keeps loading THROUGHPUT constant instead of collapsing with the
///     frame rate — a 3fps viewer otherwise loads at 5% speed for no reason (the budgets exist to
///     bound per-frame cost, and a 300ms frame can absorb 20× the work of a 16ms one).
///     <para>
///         Pure + GUI-free so it is unit-testable (the renderer/caches that consume it are
///         <c>#if WINDOWS_GUI</c>).
///     </para>
/// </summary>
internal static class StreamingFrameBudgetScaler
{
    /// <summary>The frame duration the per-frame allowances were calibrated for (60fps).</summary>
    public const double ReferenceFrameSeconds = 1.0 / 60.0;

    /// <summary>
    ///     Ceiling on the catch-up multiplier. Bounds the burst after a very long frame (or a pause):
    ///     at the cap, a 2ms upload-time slice becomes 60ms — negligible inside the ≥500ms frame that
    ///     earned it, but enough that a 1fps viewer loads at half the 60fps-equivalent rate instead
    ///     of 1.6% of it.
    /// </summary>
    public const double MaxScale = 30.0;

    /// <summary>
    ///     Scale factor for a frame that took <paramref name="frameSeconds" />: 1.0 at or below the
    ///     60fps reference (allowances never shrink), rising linearly to <see cref="MaxScale" />.
    ///     Non-finite / non-positive inputs (first frame, clock anomalies) fall back to 1.0.
    /// </summary>
    public static double Scale(double frameSeconds)
    {
        if (!double.IsFinite(frameSeconds) || frameSeconds <= ReferenceFrameSeconds)
        {
            return 1.0;
        }

        return Math.Min(frameSeconds / ReferenceFrameSeconds, MaxScale);
    }

    /// <summary>Scales an integer per-frame allowance, saturating instead of overflowing (an
    /// unthrottled allowance of <see cref="int.MaxValue" /> stays unbounded).</summary>
    public static int ScaleCount(int baseCount, double scale)
    {
        if (baseCount <= 0)
        {
            return baseCount;
        }

        var scaled = baseCount * Math.Max(1.0, scale);
        return scaled >= int.MaxValue ? int.MaxValue : (int)scaled;
    }

    /// <summary>Scales a byte per-frame allowance, saturating instead of overflowing.</summary>
    public static long ScaleBytes(long baseBytes, double scale)
    {
        if (baseBytes <= 0)
        {
            return baseBytes;
        }

        var scaled = baseBytes * Math.Max(1.0, scale);
        return scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
    }
}
