namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of <see cref="NifModelSkeletonResolver.Resolve" />. Every status but <see cref="Resolved" /> is fail
///     closed: no skeleton is chosen, and <see cref="NifModelSkeletonResolver.Reason" /> gives the text the reader reports.
/// </summary>
internal enum NifModelSkeletonResolutionStatus
{
    /// <summary>A skeleton was chosen.</summary>
    Resolved,

    /// <summary>
    ///     No ancestor directory of the <c>.kf</c> holds a <c>skeleton.nif</c> (owner ruling D4: Unsupported, 'no skeleton
    ///     found; pass --skeleton', until SA8).
    /// </summary>
    NoSkeleton,

    /// <summary>The explicit skeleton path names no occurrence. The walk-up is not tried in its place.</summary>
    ExplicitSkeletonMissing,

    /// <summary>
    ///     The first candidate that exists names more than one source occurrence. The walk stops there, since choosing one
    ///     would be arbitrary and continuing upward would pass over the nearest skeleton.
    /// </summary>
    AmbiguousSkeleton,

    /// <summary>The <c>.kf</c> path cannot form a bounded relative virtual path, so there is nothing to walk up from.</summary>
    InvalidAnimationPath
}
