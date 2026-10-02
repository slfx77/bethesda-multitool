namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of mapping an XYZ-Euler rotation part (<see cref="NifModelCurveMapping.MapEulerRotation" />): the three
///     axes, or the reason the rotation stays native.
/// </summary>
/// <param name="Euler">The mapped axes; null when blocked.</param>
/// <param name="Block">Why the rotation cannot be mapped; <see cref="NifModelCurveBlock.None" /> otherwise.</param>
internal readonly record struct NifModelEulerResult(NifModelEulerRotation? Euler, NifModelCurveBlock Block)
{
    /// <summary>True when the rotation cannot be mapped.</summary>
    public bool IsBlocked => Block != NifModelCurveBlock.None;

    /// <summary>A mapped rotation.</summary>
    /// <param name="euler">The axes.</param>
    /// <returns>The result carrying the axes.</returns>
    public static NifModelEulerResult Mapped(NifModelEulerRotation euler)
    {
        ArgumentNullException.ThrowIfNull(euler);
        return new NifModelEulerResult(euler, NifModelCurveBlock.None);
    }

    /// <summary>A rotation the mapping refuses.</summary>
    /// <param name="block">The reason; never <see cref="NifModelCurveBlock.None" />.</param>
    /// <returns>The blocked result.</returns>
    public static NifModelEulerResult Blocked(NifModelCurveBlock block)
    {
        if (block == NifModelCurveBlock.None)
        {
            throw new ArgumentOutOfRangeException(nameof(block), block, "A blocked result needs a reason.");
        }

        return new NifModelEulerResult(null, block);
    }
}
