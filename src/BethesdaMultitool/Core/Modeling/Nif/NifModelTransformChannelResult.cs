namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of mapping one transform channel: the channel, an XYZ-Euler rotation (slice 13, the rotation channel
///     only), or why it stays native.
/// </summary>
/// <param name="Channel">The mapped quaternion, translation or scale channel; null when blocked or Euler.</param>
/// <param name="Block">Why the channel cannot be mapped; <see cref="NifModelCurveBlock.None" /> otherwise.</param>
/// <param name="Euler">The mapped Euler rotation; null when the channel is a transform channel or blocked.</param>
internal readonly record struct NifModelTransformChannelResult(
    NifModelTransformChannel? Channel,
    NifModelCurveBlock Block,
    NifModelEulerRotation? Euler = null)
{
    /// <summary>True when the channel cannot be mapped.</summary>
    public bool IsBlocked => Block != NifModelCurveBlock.None;

    /// <summary>True when the rotation channel mapped to an Euler rotation (no quaternion track is emitted for it).</summary>
    public bool IsEuler => Euler is not null;

    /// <summary>A mapped channel.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The result carrying the channel.</returns>
    public static NifModelTransformChannelResult Mapped(NifModelTransformChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new NifModelTransformChannelResult(channel, NifModelCurveBlock.None);
    }

    /// <summary>A rotation channel mapped to an Euler rotation.</summary>
    /// <param name="euler">The axes.</param>
    /// <returns>The result carrying the Euler rotation.</returns>
    public static NifModelTransformChannelResult EulerMapped(NifModelEulerRotation euler)
    {
        ArgumentNullException.ThrowIfNull(euler);
        return new NifModelTransformChannelResult(null, NifModelCurveBlock.None, euler);
    }

    /// <summary>A channel the mapping refuses.</summary>
    /// <param name="block">The reason; never <see cref="NifModelCurveBlock.None" />.</param>
    /// <returns>The blocked result.</returns>
    public static NifModelTransformChannelResult Blocked(NifModelCurveBlock block)
    {
        if (block == NifModelCurveBlock.None)
        {
            throw new ArgumentOutOfRangeException(nameof(block), block, "A blocked result needs a reason.");
        }

        return new NifModelTransformChannelResult(null, block);
    }
}
