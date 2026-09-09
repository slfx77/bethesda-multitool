using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

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
            duration <= 0f ||
            (clip.PingPongs && !clip.Loops))
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

            var hasKeyData = track.RotationKeys.Length > 0 ||
                             track.TranslationKeys.Length > 0 ||
                             track.ScaleKeys.Length > 0 ||
                             track.HasEulerRotation;
            if (!float.IsFinite(track.Frequency) ||
                !float.IsFinite(track.Phase) ||
                !Enum.IsDefined(track.RotationInterpolation) ||
                !Enum.IsDefined(track.TranslationInterpolation) ||
                !Enum.IsDefined(track.ScaleInterpolation) ||
                track.RotationInterpolation == BethesdaViewerKeyInterpolation.XyzEuler !=
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
                (!hasKeyData && track.BsplineTransform is null) ||
                (hasKeyData && track.BsplineTransform is not null) ||
                (track.BsplineTransform is { } bspline && !Valid(bspline)))
            {
                error = $"node target {track.NodeIndex} contains malformed key data";
                return false;
            }
        }

        if (!BethesdaViewerGeometryMorphPolicy.IsValid(clip, meshPartCount))
        {
            error = "authored geometry morph contains an unsupported or malformed binding";
            return false;
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
               keys.All(static key => float.IsFinite(key.Time) && IsFinite(key.Value) &&
                                      (!key.HasQuadraticTangents ||
                                       (NifQuadraticVectorCurve.IsFiniteAuthored(key.Value) &&
                                        NifQuadraticVectorCurve.IsFiniteAuthored(key.Forward) &&
                                        NifQuadraticVectorCurve.IsFiniteAuthored(key.Backward))));
    }

    private static bool Valid(BethesdaViewerFloatKey[]? keys)
    {
        return keys is null ||
               (TimesAscending(keys.Select(static key => key.Time)) &&
                keys.All(static key => float.IsFinite(key.Time) && float.IsFinite(key.Value)));
    }

    private static bool Valid(NifBsplineTransformData transform)
    {
        var duration = transform.StopTime - transform.StartTime;
        if (!float.IsFinite(transform.StartTime) ||
            !float.IsFinite(transform.StopTime) ||
            !float.IsFinite(duration) ||
            duration <= 0f ||
            !transform.HasAnyValue ||
            (transform.DefaultTranslation is { } translation && !IsFiniteAuthored(translation)) ||
            (transform.DefaultRotation is { } rotation && !ValidRotation(rotation)) ||
            (transform.DefaultScale is { } scale && !IsFiniteAuthored(scale)))
        {
            return false;
        }

        var expectedCount = 0;
        if (!ValidControlPoints(transform.TranslationControlPoints, ref expectedCount) ||
            !ValidControlPoints(transform.RotationControlPoints, ref expectedCount) ||
            !ValidControlPoints(transform.ScaleControlPoints, ref expectedCount))
        {
            return false;
        }

        return true;
    }

    private static bool ValidControlPoints(Vector3[]? values, ref int expectedCount)
    {
        return values is null ||
               (ValidControlPointCount(values.Length, ref expectedCount) &&
                values.All(IsFiniteAuthored));
    }

    private static bool ValidControlPoints(Quaternion[]? values, ref int expectedCount)
    {
        return values is null ||
               (ValidControlPointCount(values.Length, ref expectedCount) &&
                values.All(ValidRotation));
    }

    private static bool ValidControlPoints(float[]? values, ref int expectedCount)
    {
        return values is null ||
               (ValidControlPointCount(values.Length, ref expectedCount) &&
                values.All(IsFiniteAuthored));
    }

    private static bool ValidControlPointCount(int count, ref int expectedCount)
    {
        if (count is < NifOpenUniformCubicBspline.MinimumControlPointCount or
            > NifBsplineTransformReader.MaximumControlPointCount)
        {
            return false;
        }

        if (expectedCount == 0)
        {
            expectedCount = count;
            return true;
        }

        return count == expectedCount;
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

    private static bool IsFiniteAuthored(float value)
    {
        return float.IsFinite(value) && MathF.Abs(value) < 1e30f;
    }

    private static bool IsFiniteAuthored(Vector3 value)
    {
        return IsFiniteAuthored(value.X) && IsFiniteAuthored(value.Y) && IsFiniteAuthored(value.Z);
    }

    private static bool IsFiniteAuthored(Quaternion value)
    {
        return IsFiniteAuthored(value.X) && IsFiniteAuthored(value.Y) &&
               IsFiniteAuthored(value.Z) && IsFiniteAuthored(value.W);
    }

    private static bool ValidRotation(Quaternion value)
    {
        var lengthSquared = value.LengthSquared();
        return IsFiniteAuthored(value) &&
               float.IsFinite(lengthSquared) &&
               lengthSquared > 1e-12f;
    }
}
