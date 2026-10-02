using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One mapped transform channel of a NIF transform interpolator (<see cref="NifModelTransformChannelMapping" />): its
///     Shared state and static value (plan section 1.3) and, when keyed, its key curve or its B-spline curve. An
///     XYZ-Euler rotation is not a transform channel; it maps to <see cref="NifModelEulerRotation" /> instead.
/// </summary>
/// <param name="Property">The channel.</param>
/// <param name="State">Keyed, Constant or NotDriven.</param>
/// <param name="StaticValue">The static value in Shared's layout, or null (see <see cref="NifModelChannelState" />).</param>
/// <param name="Curve">A keyed channel's key curve; null otherwise.</param>
/// <param name="Spline">A keyed B-spline channel's curve; null otherwise.</param>
internal sealed record NifModelTransformChannel(
    SceneTransformProperty Property,
    SceneAnimationChannelState State,
    float[]? StaticValue,
    NifModelCurve? Curve,
    SceneBSplineCurve? Spline)
{
    /// <summary>
    ///     The Shared track for this channel on one exact node: a keyed curve keeps its times, values, interpolation, TBC
    ///     parameters and endpoints, and for a Squad rotation its platform policy (slice 16b); a keyed B-spline keeps its
    ///     controls with empty key arrays; Constant and NotDriven carry no keys. The clock and source policy are supplied
    ///     by the caller (<see cref="NifModelClockMapping" />, <see cref="NifModelSourcePolicyMapping" />).
    /// </summary>
    /// <param name="nodeIndex">The exact target node.</param>
    /// <param name="clock">The track clock, or null (a sequence-driven or clock-less controller).</param>
    /// <param name="sourcePolicy">The controlled block's priority, or null.</param>
    /// <returns>The track; Shared validates it with its document.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The node index is negative.</exception>
    /// <exception cref="InvalidOperationException">A keyed channel carries neither a curve nor a spline.</exception>
    public SceneTransformTrack ToTrack(
        int nodeIndex, SceneAnimationClock? clock = null, SceneAnimationTrackSourcePolicy? sourcePolicy = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeIndex);
        if (State != SceneAnimationChannelState.Keyed)
        {
            return new SceneTransformTrack(nodeIndex, Property, [], [], SceneInterpolation.Linear, clock, State,
                StaticValue, sourcePolicy: sourcePolicy);
        }

        if (Spline is not null)
        {
            return new SceneTransformTrack(nodeIndex, Property, [], [], SceneInterpolation.BSpline, clock, State,
                StaticValue, Spline, sourcePolicy: sourcePolicy);
        }

        if (Curve is null)
        {
            throw new InvalidOperationException("A keyed channel carries a key curve or a B-spline curve.");
        }

        return new SceneTransformTrack(nodeIndex, Property, Curve.Times, Curve.Values, Curve.Interpolation, clock,
            State, StaticValue, null, Curve.TbcParameters, Curve.TbcEndpoints, sourcePolicy, Curve.SquadPolicy);
    }
}
