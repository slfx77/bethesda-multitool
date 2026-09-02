using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>Structural and finite-value admission gate shared by clip producers and samplers.</summary>
internal static class BethesdaViewerAnimationValidator
{
    internal static bool TryValidate(
        BethesdaViewerAnimationClip clip,
        int nodeCount,
        int meshPartCount,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var duration = clip.EndTime - clip.StartTime;
        if (string.IsNullOrWhiteSpace(clip.Name) ||
            !float.IsFinite(clip.StartTime) ||
            !float.IsFinite(clip.EndTime) ||
            !float.IsFinite(duration) ||
            duration <= 0f)
        {
            error = "clip name or play window is invalid";
            return false;
        }

        var claimedNodes = new HashSet<int>();
        foreach (var track in clip.NodeTracks)
        {
            if ((uint)track.NodeIndex >= (uint)nodeCount || !claimedNodes.Add(track.NodeIndex))
            {
                error = $"node target {track.NodeIndex} is invalid or duplicated";
                return false;
            }

            if (!float.IsFinite(track.Frequency) ||
                !float.IsFinite(track.Phase) ||
                !Enum.IsDefined(track.RotationInterpolation) ||
                !Enum.IsDefined(track.TranslationInterpolation) ||
                !Enum.IsDefined(track.ScaleInterpolation) ||
                (track.RotationInterpolation == BethesdaViewerKeyInterpolation.XyzEuler) !=
                track.HasEulerRotation ||
                track.TranslationInterpolation == BethesdaViewerKeyInterpolation.XyzEuler ||
                track.ScaleInterpolation == BethesdaViewerKeyInterpolation.XyzEuler ||
                (track.RotationKeys.Length > 0 && track.HasEulerRotation) ||
                !Valid(track.RotationKeys) ||
                !Valid(track.TranslationKeys) ||
                !Valid(track.ScaleKeys) ||
                !Valid(track.EulerXKeys) ||
                !Valid(track.EulerYKeys) ||
                !Valid(track.EulerZKeys) ||
                (track.RotationKeys.Length == 0 &&
                 track.TranslationKeys.Length == 0 &&
                 track.ScaleKeys.Length == 0 &&
                 !track.HasEulerRotation))
            {
                error = $"node target {track.NodeIndex} contains malformed key data";
                return false;
            }
        }

        var claimedMorphParts = new HashSet<int>();
        foreach (var track in clip.MorphWeightTracks)
        {
            if ((uint)track.MeshPartIndex >= (uint)meshPartCount ||
                !claimedMorphParts.Add(track.MeshPartIndex) ||
                !float.IsFinite(track.Frequency) ||
                !float.IsFinite(track.Phase) ||
                !Enum.IsDefined(track.Interpolation) ||
                track.Interpolation == BethesdaViewerKeyInterpolation.XyzEuler ||
                track.TargetNames.Length == 0 ||
                track.TargetNames.Any(string.IsNullOrWhiteSpace) ||
                track.Keys.Length == 0 ||
                !TimesAscending(track.Keys.Select(static key => key.Time)) ||
                track.Keys.Any(key =>
                    !float.IsFinite(key.Time) ||
                    key.Weights.Length != track.TargetNames.Length ||
                    key.Weights.Any(static weight => !float.IsFinite(weight))))
            {
                error = $"morph target {track.MeshPartIndex} contains malformed key data";
                return false;
            }
        }

        if (clip.TextKeys.Any(static key =>
                !float.IsFinite(key.Time) || string.IsNullOrWhiteSpace(key.Label)) ||
            !TimesAscending(clip.TextKeys.Select(static key => key.Time)))
        {
            error = "text keys are malformed or out of order";
            return false;
        }

        error = null;
        return true;
    }

    private static bool Valid(BethesdaViewerQuaternionKey[] keys)
    {
        return TimesAscending(keys.Select(static key => key.Time)) &&
               keys.All(static key =>
               {
                   var lengthSquared = key.Value.LengthSquared();
                   return float.IsFinite(key.Time) &&
                          IsFinite(key.Value) &&
                          float.IsFinite(lengthSquared) &&
                          lengthSquared > 1e-12f;
               });
    }

    private static bool Valid(BethesdaViewerVector3Key[] keys)
    {
        return TimesAscending(keys.Select(static key => key.Time)) &&
               keys.All(static key => float.IsFinite(key.Time) && IsFinite(key.Value));
    }

    private static bool Valid(BethesdaViewerFloatKey[]? keys)
    {
        return keys is null ||
               (TimesAscending(keys.Select(static key => key.Time)) &&
                keys.All(static key => float.IsFinite(key.Time) && float.IsFinite(key.Value)));
    }

    private static bool TimesAscending(IEnumerable<float> times)
    {
        var hasPrevious = false;
        var previous = 0f;
        foreach (var time in times)
        {
            if (hasPrevious && time < previous)
            {
                return false;
            }

            previous = time;
            hasPrevious = true;
        }

        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}
