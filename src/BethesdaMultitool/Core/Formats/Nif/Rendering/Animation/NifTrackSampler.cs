using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Deterministic, allocation-free time-domain sampling of keyframe tracks.
///     <para>
///         Quadratic Vector3 channels use retained authored Hermite tangents. Quaternion/scalar
///         Quadratic and all TBC channels retain their labeled slerp/lerp approximations; keys
///         constructed without tangent payloads also preserve that explicit legacy behavior.
///         Constant keys hold the earlier value until the next key, matching the recovered
///         NiStepFloatKey/NiStepPosKey/NiStepRotKey consumers rather than interpolating a transition.
///     </para>
/// </summary>
internal static class NifTrackSampler
{
    /// <summary>
    ///     Maps wall-clock seconds into controller-local key time:
    ///     <c>u = t×frequency + phase</c>, looped or clamped into [start, stop].
    /// </summary>
    internal static float MapTime(float t, float frequency, float phase, float start, float stop, bool loop)
    {
        var u = t * frequency + phase;
        var length = stop - start;
        if (length <= 0f)
        {
            return start;
        }

        if (!loop)
        {
            return Math.Clamp(u, start, stop);
        }

        var wrapped = (u - start) % length;
        if (wrapped < 0f)
        {
            wrapped += length;
        }

        return start + wrapped;
    }

    internal static Quaternion SampleRotation(
        NifQuatKey[] keys, float time, NifKeyInterpolation interpolation = NifKeyInterpolation.Linear)
    {
        var (lo, hi, frac) = Bracket(keys.Length, i => keys[i].Time, time);
        if (lo == hi || interpolation == NifKeyInterpolation.Constant)
        {
            return Quaternion.Normalize(keys[lo].Value);
        }

        return Quaternion.Normalize(Quaternion.Slerp(keys[lo].Value, keys[hi].Value, frac));
    }

    internal static Vector3 SampleTranslation(
        NifVec3Key[] keys, float time, NifKeyInterpolation interpolation = NifKeyInterpolation.Linear)
    {
        var (lo, hi, frac) = Bracket(keys.Length, i => keys[i].Time, time);
        if (lo == hi || interpolation == NifKeyInterpolation.Constant)
        {
            return keys[lo].Value;
        }

        return interpolation == NifKeyInterpolation.Quadratic &&
               keys[lo].HasQuadraticTangents && keys[hi].HasQuadraticTangents
            ? NifQuadraticVectorCurve.Sample(
                keys[lo].Value, keys[hi].Value, keys[lo].Backward, keys[hi].Forward, frac)
            : Vector3.Lerp(keys[lo].Value, keys[hi].Value, frac);
    }

    internal static float SampleScale(
        NifFloatKey[] keys, float time, NifKeyInterpolation interpolation = NifKeyInterpolation.Linear)
    {
        var (lo, hi, frac) = Bracket(keys.Length, i => keys[i].Time, time);
        return lo == hi || interpolation == NifKeyInterpolation.Constant
            ? keys[lo].Value
            : float.Lerp(keys[lo].Value, keys[hi].Value, frac);
    }

    /// <summary>
    ///     Samples an XYZ-Euler rotation track: each axis's angle KeyGroup is lerped independently
    ///     (an axis without keys holds angle 0 — the authored Euler channels ARE the rotation, per
    ///     the same replaces-rest channel rule the evaluator applies) and composed in Z→Y→X order
    ///     under the row-vector convention used throughout (v × Rz × Ry × Rx). Order verified
    ///     empirically on nv_gs-saloon-sign.nif: the clip's t=0 pose reproduces the authored node
    ///     rest rotation only in this composition (the X→Y→Z alternative leaves a ~180° residual —
    ///     see FnvRigidNodeAnimationRetailTests).
    /// </summary>
    internal static Quaternion SampleEulerRotation(NifNodeTrack track, float time)
    {
        var x = track.EulerXKeys is { Length: > 0 } xKeys ? SampleScale(xKeys, time) : 0f;
        var y = track.EulerYKeys is { Length: > 0 } yKeys ? SampleScale(yKeys, time) : 0f;
        var z = track.EulerZKeys is { Length: > 0 } zKeys ? SampleScale(zKeys, time) : 0f;
        return Quaternion.Normalize(
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, z) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, y) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, x));
    }

    /// <summary>
    ///     Binary-searches the bracketing key pair for <paramref name="time" />. Clamps before the
    ///     first / after the last key (lo == hi). Assumes at least one key and ascending times.
    /// </summary>
    private static (int Lo, int Hi, float Frac) Bracket(int count, Func<int, float> timeAt, float time)
    {
        if (count == 1 || time <= timeAt(0))
        {
            return (0, 0, 0f);
        }

        if (time >= timeAt(count - 1))
        {
            return (count - 1, count - 1, 0f);
        }

        var lo = 0;
        var hi = count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (timeAt(mid) <= time)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var t0 = timeAt(lo);
        var t1 = timeAt(hi);
        var span = t1 - t0;
        return (lo, hi, span <= 0f ? 0f : (time - t0) / span);
    }
}
