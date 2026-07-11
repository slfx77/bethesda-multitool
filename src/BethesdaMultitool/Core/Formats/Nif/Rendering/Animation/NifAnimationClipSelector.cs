namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Resolves the play window for an ambient animated static from its text-key markers.
///     <para>
///         CLIP POLICY STAND-IN (labeled): the engine's group choice for always-animated statics
///         hasn't been decompiled. Morrowind banners author Idle/Idle2/Idle3 of increasing motion
///         and an explicit "Idle3: Loop Start/Stop" window — we play the LAST Idle&lt;N&gt; group's
///         loop window (else its start/stop). No text keys → the union of the tracks' key ranges.
///         Verify sway choice against OpenMW as a black-box oracle; the policy is one method to
///         change if it disagrees.
///     </para>
/// </summary>
internal static class NifAnimationClipSelector
{
    internal readonly record struct NifAnimClip(float Start, float Stop, bool Loops);

    internal static NifAnimClip? SelectClip(NifAnimTextKey[] textKeys, IReadOnlyList<NifNodeTrack?> tracks)
    {
        var fromTextKeys = SelectFromTextKeys(textKeys);
        if (fromTextKeys is { } clip)
        {
            return clip;
        }

        return SelectFromKeyRanges(tracks);
    }

    private static NifAnimClip? SelectFromTextKeys(NifAnimTextKey[] textKeys)
    {
        // Group markers by clip name: "<name>: Start" / "Stop" / "Loop Start" / "Loop Stop".
        Dictionary<string, (float? Start, float? Stop, float? LoopStart, float? LoopStop)> groups =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (var key in textKeys)
        {
            var split = key.Label.IndexOf(':');
            if (split <= 0)
            {
                continue;
            }

            var name = key.Label[..split].Trim();
            var marker = key.Label[(split + 1)..].Trim();
            var group = groups.TryGetValue(name, out var g) ? g : default;
            switch (marker.ToLowerInvariant())
            {
                case "start": group.Start ??= key.Time; break;
                case "stop": group.Stop ??= key.Time; break;
                case "loop start": group.LoopStart ??= key.Time; break;
                case "loop stop": group.LoopStop ??= key.Time; break;
            }

            groups[name] = group;
        }

        // Prefer the highest-numbered Idle<N> group ("Idle" counts as 1).
        string? bestName = null;
        var bestRank = -1;
        foreach (var name in groups.Keys)
        {
            var rank = IdleRank(name);
            if (rank > bestRank)
            {
                bestRank = rank;
                bestName = name;
            }
        }

        if (bestName is null)
        {
            return null;
        }

        var best = groups[bestName];
        if (best is { LoopStart: { } loopStart, LoopStop: { } loopStop } && loopStop > loopStart)
        {
            return new NifAnimClip(loopStart, loopStop, Loops: true);
        }

        if (best is { Start: { } start, Stop: { } stop } && stop > start)
        {
            return new NifAnimClip(start, stop, Loops: true);
        }

        return null; // degenerate window (e.g. "Idle: Start/Stop" both at 0) → not animated
    }

    /// <summary>-1 = not an idle group; "Idle" = 1; "Idle<N>" = N.</summary>
    private static int IdleRank(string name)
    {
        if (!name.StartsWith("Idle", StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var suffix = name[4..].Trim();
        if (suffix.Length == 0)
        {
            return 1;
        }

        return int.TryParse(suffix, out var n) ? n : -1;
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
