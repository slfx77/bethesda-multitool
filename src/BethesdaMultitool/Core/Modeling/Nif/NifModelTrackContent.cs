using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Compares two mapped tracks bit for bit: for a transform track its property, state, interpolation, key times and
///     values, static value, B-spline controls and parameters, TBC parameters and endpoints, and Squad policy; for an
///     Euler rotation track its order and its three axis curves. Node index, clock and source policy are not compared;
///     they belong to the binding, not to the curve content.
/// </summary>
/// <remarks>
///     RE-21 collapses repeated controlled blocks whose decoded content is bit-equal. The slice-2 mappings keep every
///     playback-relevant key field bit-exact and map each key type to its own interpolation, so equal mapped tracks mean
///     equal decoded curves. A blocked channel has no mapped track and is never equal to anything.
/// </remarks>
internal static class NifModelTrackContent
{
    /// <summary>Whether two transform tracks carry the same curve content, bit for bit.</summary>
    /// <param name="first">One mapped track.</param>
    /// <param name="second">The other mapped track.</param>
    /// <returns>True only when every compared field is bit-identical.</returns>
    public static bool Equal(SceneTransformTrack first, SceneTransformTrack second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.Property == second.Property && first.State == second.State &&
               first.Interpolation == second.Interpolation && SameBits(first.Times, second.Times) &&
               SameBits(first.Values, second.Values) && SameBits(first.StaticValue, second.StaticValue) &&
               SameSpline(first.Spline, second.Spline) && SameTbc(first.TbcParameters, second.TbcParameters) &&
               SameEndpoints(first.TbcEndpoints, second.TbcEndpoints) &&
               first.GamebryoSquadPolicy == second.GamebryoSquadPolicy;
    }

    /// <summary>Whether two Euler rotation tracks carry the same axis curves, bit for bit (slice 13, RE-21).</summary>
    /// <param name="first">One mapped track.</param>
    /// <param name="second">The other mapped track.</param>
    /// <returns>True only when the order and every axis curve are identical.</returns>
    public static bool Equal(SceneEulerRotationTrack first, SceneEulerRotationTrack second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return first.Order == second.Order && SameCurve(first.X, second.X) && SameCurve(first.Y, second.Y) &&
               SameCurve(first.Z, second.Z);
    }

    private static bool SameCurve(SceneCurve first, SceneCurve second)
    {
        return first.ComponentCount == second.ComponentCount && first.State == second.State &&
               first.Interpolation == second.Interpolation && SameBits(first.Times, second.Times) &&
               SameBits(first.Values, second.Values) && SameBits(first.StaticValue, second.StaticValue) &&
               SameSpline(first.Spline, second.Spline) && SameTbc(first.TbcParameters, second.TbcParameters) &&
               SameEndpoints(first.TbcEndpoints, second.TbcEndpoints);
    }

    private static bool SameBits(IReadOnlyList<float>? first, IReadOnlyList<float>? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (BitConverter.SingleToUInt32Bits(first[index]) != BitConverter.SingleToUInt32Bits(second[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameBits(float? first, float? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return BitConverter.SingleToUInt32Bits(first.Value) == BitConverter.SingleToUInt32Bits(second.Value);
    }

    private static bool SameSpline(SceneBSplineCurve? first, SceneBSplineCurve? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return first.Degree == second.Degree && first.ComponentCount == second.ComponentCount &&
               first.ControlPointCount == second.ControlPointCount && first.IsQuantized == second.IsQuantized &&
               SameBits(first.StartSeconds, second.StartSeconds) && SameBits(first.StopSeconds, second.StopSeconds) &&
               SameBits(first.Bias, second.Bias) && SameBits(first.Multiplier, second.Multiplier) &&
               SameBits(first.ControlPoints, second.ControlPoints) &&
               first.QuantizedControlPoints.SequenceEqual(second.QuantizedControlPoints);
    }

    private static bool SameTbc(IReadOnlyList<SceneTbcParameters>? first, IReadOnlyList<SceneTbcParameters>? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        if (first.Count != second.Count)
        {
            return false;
        }

        for (var index = 0; index < first.Count; index++)
        {
            if (!SameBits(first[index].Tension, second[index].Tension) ||
                !SameBits(first[index].Continuity, second[index].Continuity) ||
                !SameBits(first[index].Bias, second[index].Bias))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameEndpoints(SceneTbcEndpointTangents? first, SceneTbcEndpointTangents? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return SameBits(first.StartOutgoing, second.StartOutgoing) && SameBits(first.EndIncoming, second.EndIncoming);
    }
}
