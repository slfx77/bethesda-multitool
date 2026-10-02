namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of mapping one key group (<see cref="NifModelCurveMapping" />): a curve, a blocked reason, or neither
///     when the group stores no keys.
/// </summary>
/// <param name="Curve">The mapped curve; null when the group is empty or blocked.</param>
/// <param name="Block">Why the group cannot be mapped; <see cref="NifModelCurveBlock.None" /> otherwise.</param>
internal readonly record struct NifModelCurveResult(NifModelCurve? Curve, NifModelCurveBlock Block)
{
    /// <summary>The result for a group that stores no keys: no curve and nothing blocked.</summary>
    public static NifModelCurveResult NoKeys => default;

    /// <summary>True when the group stores no keys (the channel's state then follows its static value).</summary>
    public bool IsEmpty => Curve is null && Block == NifModelCurveBlock.None;

    /// <summary>True when the group stores keys that cannot be mapped.</summary>
    public bool IsBlocked => Block != NifModelCurveBlock.None;

    /// <summary>A mapped curve.</summary>
    /// <param name="curve">The curve.</param>
    /// <returns>The result carrying the curve.</returns>
    public static NifModelCurveResult Keyed(NifModelCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        return new NifModelCurveResult(curve, NifModelCurveBlock.None);
    }

    /// <summary>A group that stores keys the mapping refuses.</summary>
    /// <param name="block">The reason; never <see cref="NifModelCurveBlock.None" />.</param>
    /// <returns>The blocked result.</returns>
    public static NifModelCurveResult Blocked(NifModelCurveBlock block)
    {
        if (block == NifModelCurveBlock.None)
        {
            throw new ArgumentOutOfRangeException(nameof(block), block, "A blocked result needs a reason.");
        }

        return new NifModelCurveResult(null, block);
    }
}
