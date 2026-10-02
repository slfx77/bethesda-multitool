using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A sub-reader's decision about one block it visited: Typed (its content is represented in the document) or
///     NativeOnly with a reason. <see cref="NifModelCoverage.Classify" /> applies a decision before its category table,
///     because only the sub-reader knows whether a visited block actually fed typed state.
/// </summary>
/// <param name="Kind">Typed or NativeOnly (the reader emits no Dropped rows).</param>
/// <param name="Reason">The NativeOnly reason; null for Typed.</param>
internal readonly record struct NifModelBlockDisposition(ModelSourceCoverageKind Kind, string? Reason)
{
    /// <summary>The block's content is represented in the typed document.</summary>
    public static NifModelBlockDisposition Typed => new(ModelSourceCoverageKind.Typed, null);

    /// <summary>True for <see cref="ModelSourceCoverageKind.Typed" />.</summary>
    public bool IsTyped => Kind == ModelSourceCoverageKind.Typed;

    /// <summary>The block is kept as native state only, for the given reason.</summary>
    public static NifModelBlockDisposition NativeOnly(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new NifModelBlockDisposition(ModelSourceCoverageKind.NativeOnly, reason);
    }
}
