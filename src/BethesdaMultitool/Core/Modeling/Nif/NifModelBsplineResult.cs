using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of mapping one B-spline channel (<see cref="NifModelBsplineMapping" />): a Shared curve, a blocked
///     reason, or neither when the channel's handle is 0xFFFF (no curve).
/// </summary>
/// <param name="Spline">The mapped curve; null when the channel has no curve or is blocked.</param>
/// <param name="Block">Why the channel cannot be mapped; <see cref="NifModelCurveBlock.None" /> otherwise.</param>
internal readonly record struct NifModelBsplineResult(SceneBSplineCurve? Spline, NifModelCurveBlock Block)
{
    /// <summary>The result for a channel whose handle is 0xFFFF: no curve and nothing blocked.</summary>
    public static NifModelBsplineResult NoCurve => default;

    /// <summary>True when the channel has no curve (its state then follows its static value).</summary>
    public bool IsEmpty => Spline is null && Block == NifModelCurveBlock.None;

    /// <summary>True when the channel has a curve that cannot be mapped.</summary>
    public bool IsBlocked => Block != NifModelCurveBlock.None;

    /// <summary>A mapped curve.</summary>
    /// <param name="spline">The curve.</param>
    /// <returns>The result carrying the curve.</returns>
    public static NifModelBsplineResult Mapped(SceneBSplineCurve spline)
    {
        ArgumentNullException.ThrowIfNull(spline);
        return new NifModelBsplineResult(spline, NifModelCurveBlock.None);
    }

    /// <summary>A channel with a curve the mapping refuses.</summary>
    /// <param name="block">The reason; never <see cref="NifModelCurveBlock.None" />.</param>
    /// <returns>The blocked result.</returns>
    public static NifModelBsplineResult Blocked(NifModelCurveBlock block)
    {
        if (block == NifModelCurveBlock.None)
        {
            throw new ArgumentOutOfRangeException(nameof(block), block, "A blocked result needs a reason.");
        }

        return new NifModelBsplineResult(null, block);
    }
}
