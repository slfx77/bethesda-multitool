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
    ///     Ceiling on the catch-up multiplier.
    ///     <para>
    ///         This is a FEEDBACK LOOP, and the cap sets its gain. The scale is derived from the
    ///         previous frame's duration and then licenses more render-thread streaming work on the
    ///         next one — so a cap of 30 meant a single 300 ms hitch granted the following frame ~18x
    ///         its budget, which kept that frame long, which granted the next one more again. Measured
    ///         on a streaming sweep, per-frame upload time was serially correlated (lag-1 autocorrelation
    ///         +0.36) — one hitch decaying over several frames rather than a single spike, which is what
    ///         a pulsing movement speed feels like.
    ///     </para>
    ///     <para>
    ///         4 keeps the original intent — a slow viewer still loads at a useful multiple of its
    ///         frame-rate-proportional share rather than collapsing to 5% — while holding the loop gain
    ///         well under 1 so a hitch decays instead of sustaining. Pair it with
    ///         <see cref="SmoothFrameSeconds" /> so a single outlier cannot set the budget at all.
    ///     </para>
    /// </summary>
    public const double MaxScale = 4.0;

    /// <summary>
    ///     Weight of the newest frame in the smoothed frame time. Low enough that one 450 ms
    ///     outlier (measured on a streaming sweep) moves the budget by a fraction of its size.
    /// </summary>
    public const double SmoothingAlpha = 0.15;

    /// <summary>
    ///     Largest share of a frame that render-thread streaming work may consume. The scale keeps
    ///     loading THROUGHPUT constant as the frame rate falls, but without a ceiling that also means a
    ///     slow frame hands streaming an ever-larger absolute slice — so the budget is additionally
    ///     capped at a fraction of the frame it is running inside.
    /// </summary>
    public const double MaxFrameFraction = 0.15;

    /// <summary>
    ///     Exponential moving average of the frame duration that <see cref="Scale" /> should be driven
    ///     from. Feeding the RAW previous frame time makes the budget track the very noise it helps
    ///     create; smoothing first means the allowance reflects the sustained frame rate instead.
    ///     Non-finite / non-positive samples leave the average untouched.
    /// </summary>
    public static double SmoothFrameSeconds(double previousAverageSeconds, double frameSeconds)
    {
        if (!double.IsFinite(frameSeconds) || frameSeconds <= 0)
        {
            return previousAverageSeconds;
        }

        if (!double.IsFinite(previousAverageSeconds) || previousAverageSeconds <= 0)
        {
            return frameSeconds; // first sample seeds the average
        }

        return previousAverageSeconds + (frameSeconds - previousAverageSeconds) * SmoothingAlpha;
    }

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

    /// <summary>
    ///     Per-frame time allowance for streaming work: the scaled base, but never more than
    ///     <see cref="MaxFrameFraction" /> of the (smoothed) frame it runs inside, and never less than
    ///     the unscaled base so a slow scene always makes progress instead of stalling forever.
    /// </summary>
    public static double TimeBudgetMilliseconds(
        double baseMilliseconds, double scale, double smoothedFrameMilliseconds)
    {
        var scaled = baseMilliseconds * Math.Max(1.0, scale);
        if (!double.IsFinite(smoothedFrameMilliseconds) || smoothedFrameMilliseconds <= 0)
        {
            return scaled;
        }

        return Math.Max(baseMilliseconds, Math.Min(scaled, smoothedFrameMilliseconds * MaxFrameFraction));
    }

    /// <summary>
    ///     Scales an integer per-frame allowance, saturating instead of overflowing (an
    ///     unthrottled allowance of <see cref="int.MaxValue" /> stays unbounded).
    /// </summary>
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
