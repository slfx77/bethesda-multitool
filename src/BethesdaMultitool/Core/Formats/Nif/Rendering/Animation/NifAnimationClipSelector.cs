namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Resolves the play window for an ambient animated static (Morrowind banners, hanging cloth).
///     <para>
///         CLIP POLICY (labeled stand-in). Passive always-animated decor in Morrowind is driven by
///         auto-playing controllers (NiBSAnimationNode-era): the engine loops the WHOLE authored
///         controller range and does not use the NiTextKeyExtraData group markers for selection —
///         those "Idle/Idle2/Idle3" groups are the actor-animation PlayGroup vocabulary, which does
///         not apply to a placed prop. So we loop the full key-time span, which returns the rig to its
///         rest (hang) pose at the wrap instead of parking it in the middle of one sub-window.
///     </para>
///     <para>
///         This deliberately REPLACES the earlier "loop the last Idle&lt;N&gt; group's Loop window"
///         heuristic: for <c>furn_banner_tavern_01</c> that isolated Idle3's window (2.333–3.667),
///         whose keys swing the root −46°…−85° about X — a hanging banner stuck ~65° off vertical
///         (near horizontal) every frame. Full-range playback instead plays the gentle Idle2 build-up
///         and the stronger Idle3 gust and settles back to the hang each 4 s cycle. Whether the engine
///         instead sustains only Idle3's marked loop (a stronger, constant billow) is the open
///         question for the OpenMW black-box oracle; this is the one method to change if it disagrees.
///     </para>
/// </summary>
internal static class NifAnimationClipSelector
{
    internal readonly record struct NifAnimClip(float Start, float Stop, bool Loops);

    internal static NifAnimClip? SelectClip(NifAnimTextKey[] textKeys, IReadOnlyList<NifNodeTrack?> tracks)
    {
        _ = textKeys; // retained on NifMeshAnimation for diagnostics; not used for passive-decor selection
        return SelectFromKeyRanges(tracks);
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
