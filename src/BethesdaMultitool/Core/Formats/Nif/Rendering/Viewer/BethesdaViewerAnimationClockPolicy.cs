namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Maps a clip's per-track Gamebryo clocks onto one viewer timeline without wrapping the raw
///     clock before individual tracks have applied their authored frequency and phase.
/// </summary>
internal static class BethesdaViewerAnimationClockPolicy
{
    internal readonly record struct Window(
        float RawOriginSeconds,
        float PresentationDurationSeconds,
        float ClampEndClockSeconds);

    internal static Window Resolve(BethesdaViewerAnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);

        var localSpan = (double)clip.EndTime - clip.StartTime;
        if (!double.IsFinite(localSpan) || localSpan <= 0d)
        {
            return default;
        }

        var hasNodeTrack = false;
        var hasDynamicClock = false;
        var earliestEntry = double.PositiveInfinity;
        var latestExit = double.NegativeInfinity;
        var longestPeriod = 0d;
        foreach (var track in clip.NodeTracks)
        {
            hasNodeTrack = true;
            var frequency = (double)track.Frequency;
            var phase = (double)track.Phase;
            if (!double.IsFinite(frequency) || !double.IsFinite(phase) || frequency == 0d)
            {
                continue;
            }

            // A positive controller enters at Start and exits at End. A negative controller runs
            // the same authored window backwards, so its entry and terminal boundaries swap.
            var entry = frequency > 0d
                ? ((double)clip.StartTime - phase) / frequency
                : ((double)clip.EndTime - phase) / frequency;
            var exit = frequency > 0d
                ? ((double)clip.EndTime - phase) / frequency
                : ((double)clip.StartTime - phase) / frequency;
            var period = localSpan / Math.Abs(frequency);
            if (!double.IsFinite(entry) || !double.IsFinite(exit) ||
                !double.IsFinite(period) || exit < entry || period <= 0d)
            {
                continue;
            }

            hasDynamicClock = true;
            earliestEntry = Math.Min(earliestEntry, entry);
            latestExit = Math.Max(latestExit, exit);
            longestPeriod = Math.Max(longestPeriod, period);
        }

        if (!hasNodeTrack)
        {
            // A morph-only/text-only clip is retained for future evaluators. Preserve its authored
            // window in the UI even though the current node-pose streamer will decline playback.
            return new Window(clip.StartTime, (float)localSpan, clip.EndTime);
        }

        if (!hasDynamicClock)
        {
            // Zero-frequency node controllers are dormant/static. They must not request an endless
            // render loop merely because the source still carries keys.
            return new Window(clip.StartTime, 0f, clip.StartTime);
        }

        var duration = clip.Loops ? longestPeriod : latestExit - earliestEntry;
        return new Window(
            ToFiniteFloat(earliestEntry),
            ToFiniteNonNegativeFloat(duration),
            ToFiniteFloat(latestExit));
    }

    internal static float GetDisplayTime(
        BethesdaViewerAnimationClip clip,
        in Window window,
        float rawClockSeconds)
    {
        var duration = (double)window.PresentationDurationSeconds;
        if (!double.IsFinite(duration) || duration <= 0d)
        {
            return 0f;
        }

        var elapsed = (double)rawClockSeconds - window.RawOriginSeconds;
        if (!double.IsFinite(elapsed))
        {
            return 0f;
        }

        if (!clip.Loops)
        {
            return (float)Math.Clamp(elapsed, 0d, duration);
        }

        // This is a display/seek horizon, not a claim that mixed or incommensurate track rates
        // share a joint period. The raw evaluation clock is intentionally never wrapped here.
        var wrapped = elapsed % duration;
        return (float)(wrapped < 0d ? wrapped + duration : wrapped);
    }

    internal static float GetSeekClock(in Window window, float localTimeSeconds)
    {
        var duration = MathF.Max(window.PresentationDurationSeconds, 0f);
        var local = float.IsFinite(localTimeSeconds)
            ? Math.Clamp(localTimeSeconds, 0f, duration)
            : 0f;
        return ToFiniteFloat((double)window.RawOriginSeconds + local);
    }

    private static float ToFiniteNonNegativeFloat(double value)
    {
        if (double.IsNaN(value) || value <= 0d)
        {
            return 0f;
        }

        return value >= float.MaxValue ? float.MaxValue : (float)value;
    }

    private static float ToFiniteFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return 0f;
        }

        return value switch
        {
            >= float.MaxValue => float.MaxValue,
            <= -float.MaxValue => -float.MaxValue,
            _ => (float)value
        };
    }
}
