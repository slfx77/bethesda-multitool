namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Resolves the ambient play window for an animated NIF (Morrowind banners, creatures).
///     <para>
///         CLIP POLICY (labeled stand-in), two tiers:
///     </para>
///     <para>
///         1. The plain "Idle" text-key GROUP, when it has a non-degenerate window (its Loop
///         Start/Stop, else Start/Stop). Creature files concatenate EVERY animation group on one
///         timeline (r\Guar.NIF: Idle 0→3.27 s, then Idle2–6, Walk, Run, Attack1–3, Death,
///         Knockout, Turns, Hit out to 27.3 s) — the ambient behavior is the standing idle, not a
///         tour of the whole repertoire. Only the EXACT name "Idle" qualifies: the numbered
///         variants are the AI's occasional fidgets ("Idle3" on the tavern banner is the violent
///         gust that parked it near-horizontal when a previous policy picked highest-N).
///     </para>
///     <para>
///         2. Otherwise the FULL key-time span, looped. Animated decor authors a DEGENERATE plain
///         Idle marker (the banner's "Idle: Start/Stop" both at 0) — full-range playback gives the
///         gentle build-up + gust cycle and returns the rig to its rest pose at each wrap. Whether
///         the engine instead sustains one marked sub-loop is the open OpenMW black-box question;
///         this selector is the one place to change if it disagrees.
///     </para>
/// </summary>
internal static class NifAnimationClipSelector
{
    internal readonly record struct NifAnimClip(float Start, float Stop, bool Loops);

    internal static NifAnimClip? SelectClip(NifAnimTextKey[] textKeys, IReadOnlyList<NifNodeTrack?> tracks)
    {
        return SelectPlainIdleGroup(textKeys) ?? SelectFromKeyRanges(tracks);
    }

    /// <summary>The plain "Idle" group's window (Loop Start/Stop preferred, else Start/Stop), or
    /// null when absent or degenerate. Numbered variants ("Idle2") never match.</summary>
    private static NifAnimClip? SelectPlainIdleGroup(NifAnimTextKey[] textKeys)
    {
        float? start = null, stop = null, loopStart = null, loopStop = null;
        foreach (var key in textKeys)
        {
            var split = key.Label.IndexOf(':');
            if (split <= 0 ||
                !key.Label.AsSpan(0, split).Trim().Equals("Idle", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (key.Label[(split + 1)..].Trim().ToLowerInvariant())
            {
                case "start": start ??= key.Time; break;
                case "stop": stop ??= key.Time; break;
                case "loop start": loopStart ??= key.Time; break;
                case "loop stop": loopStop ??= key.Time; break;
            }
        }

        if (loopStart is { } ls && loopStop is { } le && le > ls + 1e-4f)
        {
            return new NifAnimClip(ls, le, Loops: true);
        }

        if (start is { } s && stop is { } e && e > s + 1e-4f)
        {
            return new NifAnimClip(s, e, Loops: true);
        }

        return null;
    }

    private static NifAnimClip? SelectFromKeyRanges(IReadOnlyList<NifNodeTrack?> tracks)
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var track in tracks)
        {
            if (track is null || !track.HasMotion)
            {
                continue;
            }

            Expand(track.RotationKeys.Length, i => track.RotationKeys[i].Time, ref min, ref max);
            Expand(track.TranslationKeys.Length, i => track.TranslationKeys[i].Time, ref min, ref max);
            Expand(track.ScaleKeys.Length, i => track.ScaleKeys[i].Time, ref min, ref max);
        }

        return max > min ? new NifAnimClip(min, max, Loops: true) : null;
    }

    private static void Expand(int count, Func<int, float> timeAt, ref float min, ref float max)
    {
        if (count == 0)
        {
            return;
        }

        min = MathF.Min(min, timeAt(0));
        max = MathF.Max(max, timeAt(count - 1));
    }
}
