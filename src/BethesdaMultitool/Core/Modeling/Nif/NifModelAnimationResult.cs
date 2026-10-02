using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     What <see cref="NifModelAnimationReader" /> produces for one file: the ordered clips and every per-block decision,
///     nothing else (no native-state rows, no document; slices 8 and 10 add those).
/// </summary>
internal sealed class NifModelAnimationResult
{
    /// <summary>Records the clips and decisions and merges the decisions per block.</summary>
    /// <param name="clips">The clips in document order.</param>
    /// <param name="decisions">Every decision, in the order the reader made them.</param>
    public NifModelAnimationResult(IReadOnlyList<SceneAnimation> clips, IReadOnlyList<NifModelAnimationDisposition> decisions)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(decisions);
        Clips = clips;
        Decisions = decisions;
        var merged = new Dictionary<int, NifModelBlockDisposition>();
        foreach (var decision in decisions)
        {
            if (!merged.TryGetValue(decision.Block, out var existing) || (!existing.IsTyped && decision.IsTyped))
            {
                merged[decision.Block] = decision.Disposition;
            }
        }

        Dispositions = merged;
    }

    /// <summary>
    ///     The clips in document order: for a <c>.nif</c>, each NiControllerManager's sequence list in block order of the
    ///     managers, then the <c>(controllers)</c> clip when one qualifies; for a <c>.kf</c>, the footer order.
    /// </summary>
    public IReadOnlyList<SceneAnimation> Clips { get; }

    /// <summary>Every decision, in the order the reader made them (a block may appear several times).</summary>
    public IReadOnlyList<NifModelAnimationDisposition> Decisions { get; }

    /// <summary>
    ///     One disposition per decided block, merged the way NifModelReader merges sub-reader decisions: Typed when any
    ///     decision about the block is Typed (a data block feeding one typed channel is typed), otherwise the first
    ///     NativeOnly reason. The channel-level reasons stay in <see cref="Decisions" />.
    /// </summary>
    public IReadOnlyDictionary<int, NifModelBlockDisposition> Dispositions { get; }
}
