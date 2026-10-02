using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of mapping a NIF clock (<see cref="NifModelClockMapping" />): a Shared clock, a blocked reason, or
///     neither (a sequence-driven controller, or a curve-less controller with the double sentinel clock).
/// </summary>
/// <param name="Clock">The mapped clock; null when there is none or the source is blocked.</param>
/// <param name="Block">Why the clock's track cannot be typed; <see cref="NifModelCurveBlock.None" /> otherwise.</param>
internal readonly record struct NifModelClockResult(SceneAnimationClock? Clock, NifModelCurveBlock Block)
{
    /// <summary>No clock and nothing blocked: the track (if any) takes the clip's time unchanged.</summary>
    public static NifModelClockResult NoClock => default;

    /// <summary>True when the source's track cannot be typed.</summary>
    public bool IsBlocked => Block != NifModelCurveBlock.None;

    /// <summary>A mapped clock.</summary>
    /// <param name="clock">The clock.</param>
    /// <returns>The result carrying the clock.</returns>
    public static NifModelClockResult Mapped(SceneAnimationClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new NifModelClockResult(clock, NifModelCurveBlock.None);
    }

    /// <summary>A source whose track cannot be typed.</summary>
    /// <param name="block">The reason; never <see cref="NifModelCurveBlock.None" />.</param>
    /// <returns>The blocked result.</returns>
    public static NifModelClockResult Blocked(NifModelCurveBlock block)
    {
        if (block == NifModelCurveBlock.None)
        {
            throw new ArgumentOutOfRangeException(nameof(block), block, "A blocked result needs a reason.");
        }

        return new NifModelClockResult(null, block);
    }
}
