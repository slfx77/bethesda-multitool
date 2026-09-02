using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Allocation-free sampler for one native-viewer clip. Construction snapshots the rest graph
///     and builds the node-index track table once; <see cref="EvaluateNodeWorlds" /> then writes a
///     complete pose into caller-owned storage on every frame.
/// </summary>
internal sealed class BethesdaViewerAnimationPoseEvaluator
{
    private readonly BethesdaViewerAnimationClip _clip;
    private readonly int[] _parentIndices;
    private readonly RestTransform[] _restTransforms;
    private readonly BethesdaViewerNodeAnimationTrack?[] _tracksByNode;

    internal BethesdaViewerAnimationPoseEvaluator(
        IReadOnlyList<Matrix4x4> restLocalTransforms,
        IReadOnlyList<int?> parentIndices,
        BethesdaViewerAnimationClip clip)
    {
        ArgumentNullException.ThrowIfNull(restLocalTransforms);
        ArgumentNullException.ThrowIfNull(parentIndices);
        ArgumentNullException.ThrowIfNull(clip);
        if (restLocalTransforms.Count != parentIndices.Count)
        {
            throw new ArgumentException("Animation rest-transform and parent collections must be parallel.");
        }
        if (!BethesdaViewerAnimationValidator.TryValidate(
                clip,
                restLocalTransforms.Count,
                int.MaxValue,
                out var validationError))
        {
            throw new InvalidDataException(
                $"Animation clip '{clip.Name}' is invalid: {validationError}.");
        }

        _clip = clip;
        _parentIndices = new int[parentIndices.Count];
        _restTransforms = new RestTransform[restLocalTransforms.Count];
        _tracksByNode = new BethesdaViewerNodeAnimationTrack?[restLocalTransforms.Count];
        for (var nodeIndex = 0; nodeIndex < restLocalTransforms.Count; nodeIndex++)
        {
            var parentIndex = parentIndices[nodeIndex] ?? -1;
            if (parentIndex >= nodeIndex || parentIndex < -1)
            {
                throw new InvalidDataException(
                    $"Animation node {nodeIndex} has non-parent-first parent index {parentIndex}.");
            }

            _parentIndices[nodeIndex] = parentIndex;
            _restTransforms[nodeIndex] = Decompose(restLocalTransforms[nodeIndex]);
        }

        foreach (var track in clip.NodeTracks)
        {
            if (track.NodeIndex < 0 || track.NodeIndex >= _tracksByNode.Length)
            {
                throw new InvalidDataException(
                    $"Animation clip '{clip.Name}' targets invalid node {track.NodeIndex}.");
            }

            if (_tracksByNode[track.NodeIndex] is not null)
            {
                throw new InvalidDataException(
                    $"Animation clip '{clip.Name}' targets node {track.NodeIndex} more than once.");
            }

            _tracksByNode[track.NodeIndex] = track;
        }
    }

    internal int NodeCount => _restTransforms.Length;

    internal BethesdaViewerAnimationClip Clip => _clip;

    internal void EvaluateNodeWorlds(float clockSeconds, Span<Matrix4x4> nodeWorlds)
    {
        if (nodeWorlds.Length < _restTransforms.Length)
        {
            throw new ArgumentException("Node-world output span is too small.", nameof(nodeWorlds));
        }

        for (var nodeIndex = 0; nodeIndex < _restTransforms.Length; nodeIndex++)
        {
            var rest = _restTransforms[nodeIndex];
            var rotation = rest.Rotation;
            var translation = rest.Translation;
            var scale = rest.Scale;
            if (_tracksByNode[nodeIndex] is { } track)
            {
                var trackTime = MapTime(
                    clockSeconds,
                    track.Frequency,
                    track.Phase,
                    _clip.StartTime,
                    _clip.EndTime,
                    _clip.Loops);
                if (track.RotationKeys.Length > 0)
                {
                    rotation = SampleRotation(
                        track.RotationKeys,
                        trackTime,
                        track.RotationInterpolation);
                }
                else if (track.HasEulerRotation)
                {
                    rotation = SampleEulerRotation(track, trackTime);
                }

                if (track.TranslationKeys.Length > 0)
                {
                    translation = SampleTranslation(
                        track.TranslationKeys,
                        trackTime,
                        track.TranslationInterpolation);
                }

                if (track.ScaleKeys.Length > 0)
                {
                    scale = new Vector3(SampleFloat(
                        track.ScaleKeys,
                        trackTime,
                        track.ScaleInterpolation));
                }
            }

            var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
            local.Translation = translation;
            var parentIndex = _parentIndices[nodeIndex];
            nodeWorlds[nodeIndex] = parentIndex >= 0
                ? local * nodeWorlds[parentIndex]
                : local;
            if (!IsFinite(nodeWorlds[nodeIndex]))
            {
                throw new InvalidDataException(
                    $"Animation node {nodeIndex} produced a non-finite world transform.");
            }
        }
    }

    internal static float MapTime(
        float clockSeconds,
        float frequency,
        float phase,
        float startTime,
        float endTime,
        bool loops)
    {
        var safeClock = float.IsFinite(clockSeconds) ? clockSeconds : 0f;
        var safeFrequency = float.IsFinite(frequency) ? frequency : 1f;
        var safePhase = float.IsFinite(phase) ? phase : 0f;
        // Finite floats can still overflow when multiplied as float (MaxValue * MaxValue). Do the
        // clock arithmetic in double so looping never reaches Infinity % duration => NaN.
        var mapped = ((double)safeClock * safeFrequency) + safePhase;
        var duration = (double)endTime - startTime;
        if (!double.IsFinite(duration) || duration <= 0d)
        {
            return startTime;
        }

        if (!loops)
        {
            return (float)Math.Clamp(mapped, startTime, endTime);
        }

        var wrapped = (mapped - startTime) % duration;
        return (float)(startTime + (wrapped < 0d ? wrapped + duration : wrapped));
    }

    private static RestTransform Decompose(Matrix4x4 transform)
    {
        if (!IsFinite(transform) ||
            !Matrix4x4.Decompose(transform, out var scale, out var rotation, out var translation) ||
            !IsFinite(scale) ||
            !IsFinite(rotation) ||
            !IsFinite(translation))
        {
            throw new InvalidDataException("Animation rest transform is non-finite or not decomposable.");
        }

        return new RestTransform(scale, Normalize(rotation), translation);
    }

    private static Quaternion SampleRotation(
        BethesdaViewerQuaternionKey[] keys,
        float time,
        BethesdaViewerKeyInterpolation interpolation)
    {
        var (lower, upper, fraction) = Bracket(keys, time);
        if (lower == upper || interpolation == BethesdaViewerKeyInterpolation.Constant)
        {
            return Normalize(keys[lower].Value);
        }

        // Quadratic and TBC tangents are not retained by the current key model. Preserve the
        // authored enum but use the same labeled slerp approximation as the placed-world sampler.
        return Normalize(Quaternion.Slerp(keys[lower].Value, keys[upper].Value, fraction));
    }

    private static Quaternion SampleEulerRotation(
        BethesdaViewerNodeAnimationTrack track,
        float time)
    {
        var x = track.EulerXKeys is { Length: > 0 } xKeys
            ? SampleFloat(xKeys, time, track.RotationInterpolation)
            : 0f;
        var y = track.EulerYKeys is { Length: > 0 } yKeys
            ? SampleFloat(yKeys, time, track.RotationInterpolation)
            : 0f;
        var z = track.EulerZKeys is { Length: > 0 } zKeys
            ? SampleFloat(zKeys, time, track.RotationInterpolation)
            : 0f;
        return Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, y) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, x));
    }

    private static Vector3 SampleTranslation(
        BethesdaViewerVector3Key[] keys,
        float time,
        BethesdaViewerKeyInterpolation interpolation)
    {
        var (lower, upper, fraction) = Bracket(keys, time);
        return lower == upper || interpolation == BethesdaViewerKeyInterpolation.Constant
            ? keys[lower].Value
            : Vector3.Lerp(keys[lower].Value, keys[upper].Value, fraction);
    }

    private static float SampleFloat(
        BethesdaViewerFloatKey[] keys,
        float time,
        BethesdaViewerKeyInterpolation interpolation)
    {
        var (lower, upper, fraction) = Bracket(keys, time);
        return lower == upper || interpolation == BethesdaViewerKeyInterpolation.Constant
            ? keys[lower].Value
            : float.Lerp(keys[lower].Value, keys[upper].Value, fraction);
    }

    private static (int Lower, int Upper, float Fraction) Bracket(
        BethesdaViewerQuaternionKey[] keys,
        float time)
    {
        if (keys.Length == 1 || time <= keys[0].Time)
        {
            return (0, 0, 0f);
        }

        if (time >= keys[^1].Time)
        {
            return (keys.Length - 1, keys.Length - 1, 0f);
        }

        var lower = 0;
        var upper = keys.Length - 1;
        while (upper - lower > 1)
        {
            var middle = (lower + upper) / 2;
            if (keys[middle].Time <= time) lower = middle;
            else upper = middle;
        }

        return Fraction(lower, upper, keys[lower].Time, keys[upper].Time, time);
    }

    private static (int Lower, int Upper, float Fraction) Bracket(
        BethesdaViewerVector3Key[] keys,
        float time)
    {
        if (keys.Length == 1 || time <= keys[0].Time)
        {
            return (0, 0, 0f);
        }

        if (time >= keys[^1].Time)
        {
            return (keys.Length - 1, keys.Length - 1, 0f);
        }

        var lower = 0;
        var upper = keys.Length - 1;
        while (upper - lower > 1)
        {
            var middle = (lower + upper) / 2;
            if (keys[middle].Time <= time) lower = middle;
            else upper = middle;
        }

        return Fraction(lower, upper, keys[lower].Time, keys[upper].Time, time);
    }

    private static (int Lower, int Upper, float Fraction) Bracket(
        BethesdaViewerFloatKey[] keys,
        float time)
    {
        if (keys.Length == 1 || time <= keys[0].Time)
        {
            return (0, 0, 0f);
        }

        if (time >= keys[^1].Time)
        {
            return (keys.Length - 1, keys.Length - 1, 0f);
        }

        var lower = 0;
        var upper = keys.Length - 1;
        while (upper - lower > 1)
        {
            var middle = (lower + upper) / 2;
            if (keys[middle].Time <= time) lower = middle;
            else upper = middle;
        }

        return Fraction(lower, upper, keys[lower].Time, keys[upper].Time, time);
    }

    private static (int Lower, int Upper, float Fraction) Fraction(
        int lower,
        int upper,
        float lowerTime,
        float upperTime,
        float time)
    {
        var span = upperTime - lowerTime;
        return (lower, upper, span <= 0f ? 0f : (time - lowerTime) / span);
    }

    private static Quaternion Normalize(Quaternion value)
    {
        return value.LengthSquared() > 1e-12f ? Quaternion.Normalize(value) : Quaternion.Identity;
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

    private static bool IsFinite(Matrix4x4 value)
    {
        return float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
               float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
               float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
               float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
               float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
               float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
               float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
               float.IsFinite(value.M43) && float.IsFinite(value.M44);
    }

    private readonly record struct RestTransform(
        Vector3 Scale,
        Quaternion Rotation,
        Vector3 Translation);
}
