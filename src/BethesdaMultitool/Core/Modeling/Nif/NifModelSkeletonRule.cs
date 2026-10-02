namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The rule <see cref="NifModelSkeletonResolver" /> applied to choose a <c>.kf</c>'s skeleton (plan section 1.7, owner
///     rulings D4 and D12). The rule's recorded name is <see cref="NifModelSkeletonResolver.RuleName" />.
/// </summary>
internal enum NifModelSkeletonRule
{
    /// <summary>
    ///     The explicit skeleton path (the <c>bmt.skeleton</c> option and the CLI's <c>--skeleton</c>), which always
    ///     wins: when one is supplied the walk-up is not consulted, even when the explicit path is missing.
    /// </summary>
    Explicit,

    /// <summary>
    ///     The nearest ancestor <c>skeleton.nif</c> of the <c>.kf</c>'s virtual path, with no compatibility gate: the first
    ///     candidate that names exactly one occurrence wins without its content being read.
    /// </summary>
    NearestAncestor
}
