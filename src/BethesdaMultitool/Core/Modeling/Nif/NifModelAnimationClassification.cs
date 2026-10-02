using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One animation-related block's coverage classification (<see cref="NifModelAnimationCoverage" />, cut-1b slice 8):
///     Typed, or NativeOnly with the reason and the stable code of the plan 2.1 row (or of the reader decision) that
///     produced it.
/// </summary>
/// <param name="Block">The block.</param>
/// <param name="Kind">Typed or NativeOnly (the classifier emits no Dropped rows).</param>
/// <param name="Reason">The NativeOnly reason; null for Typed.</param>
/// <param name="Code">The stable machine code (<see cref="NifModelAnimationReasons.TypedCode" /> for Typed).</param>
internal readonly record struct NifModelAnimationClassification(
    int Block,
    ModelSourceCoverageKind Kind,
    string? Reason,
    string Code)
{
    /// <summary>True for <see cref="ModelSourceCoverageKind.Typed" />.</summary>
    public bool IsTyped => Kind == ModelSourceCoverageKind.Typed;

    /// <summary>The disposition in the form the sub-readers use (<see cref="NifModelCoverage.Classify" /> takes it).</summary>
    public NifModelBlockDisposition Disposition =>
        IsTyped ? NifModelBlockDisposition.Typed : NifModelBlockDisposition.NativeOnly(Reason!);

    /// <summary>The Shared classification row for the block's census identity.</summary>
    /// <returns>The row.</returns>
    public ModelSourceClassification ToClassification()
    {
        return new ModelSourceClassification(NifModelCoverage.Identity(Block), Kind, Reason);
    }
}
