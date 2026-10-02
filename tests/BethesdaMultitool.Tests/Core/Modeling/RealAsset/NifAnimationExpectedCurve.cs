using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     What one typed channel or curve should hold, derived from the probe's record through the declared conversions
///     only (hop A1-anim): the state, interpolation, key times and values, static value, B-spline, TBC parameters and
///     endpoints and Squad policy, every float as its IEEE-754 binary32 bits. <see cref="Refusal" /> is set instead when
///     the declared conversions cannot represent the source (the reader must then keep it native).
/// </summary>
/// <param name="ComponentCount">The Shared width of one value.</param>
/// <param name="State">Keyed, Constant or NotDriven.</param>
/// <param name="Interpolation">The Shared interpolation (Linear for a non-keyed channel).</param>
/// <param name="Times">The key times' bits (empty for non-keyed channels and B-splines).</param>
/// <param name="Values">The key-major values' bits (triples for Hermite and Squad).</param>
/// <param name="StaticValue">The static value's bits, or null.</param>
/// <param name="Spline">The B-spline of a keyed B-spline channel, or null.</param>
/// <param name="TbcParameters">Three bits per key (tension, continuity, bias) for a TBC curve, or null.</param>
/// <param name="StartOutgoing">RE-19's start tangent bits for a multi-key TBC curve, or null.</param>
/// <param name="EndIncoming">RE-19's end tangent bits for a multi-key TBC curve, or null.</param>
/// <param name="SquadPolicy">The Squad policy of a Squad rotation, or null.</param>
/// <param name="Origin">Where the expectation came from (interpolator and data block), for diagnostics.</param>
/// <param name="Refusal">Why the declared conversions give no curve; null when they do.</param>
internal sealed record NifAnimationExpectedCurve(
    int ComponentCount,
    SceneAnimationChannelState State,
    SceneInterpolation Interpolation,
    uint[] Times,
    uint[] Values,
    uint[]? StaticValue,
    NifAnimationExpectedSpline? Spline,
    uint[]? TbcParameters,
    uint[]? StartOutgoing,
    uint[]? EndIncoming,
    SceneGamebryoSquadPolicy? SquadPolicy,
    string Origin,
    string? Refusal = null)
{
    /// <summary>True when the conversions refuse the source.</summary>
    public bool IsRefused => Refusal is not null;

    /// <summary>A refused expectation.</summary>
    /// <param name="origin">Where it came from.</param>
    /// <param name="refusal">Why.</param>
    /// <returns>The expectation.</returns>
    public static NifAnimationExpectedCurve Refused(string origin, string refusal)
    {
        return new NifAnimationExpectedCurve(0, SceneAnimationChannelState.NotDriven, SceneInterpolation.Linear, [], [],
            null, null, null, null, null, null, origin, refusal);
    }

    /// <summary>A channel with no keys: NotDriven (no static) or Constant (the static).</summary>
    /// <param name="width">The Shared width.</param>
    /// <param name="staticValue">The static bits, or null for NotDriven.</param>
    /// <param name="origin">Where it came from.</param>
    /// <returns>The expectation.</returns>
    public static NifAnimationExpectedCurve Unkeyed(int width, uint[]? staticValue, string origin)
    {
        return new NifAnimationExpectedCurve(width,
            staticValue is null ? SceneAnimationChannelState.NotDriven : SceneAnimationChannelState.Constant,
            SceneInterpolation.Linear, [], [], staticValue, null, null, null, null, null, origin);
    }
}
