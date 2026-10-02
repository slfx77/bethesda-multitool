using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of <see cref="NifModelSkeletonResolver.Resolve" />: the status, the rule applied, the chosen skeleton
///     occurrence and path when one was chosen, and every candidate examined, in order.
/// </summary>
internal sealed class NifModelSkeletonResolution
{
    /// <summary>Creates a resolution.</summary>
    /// <param name="status">The outcome.</param>
    /// <param name="rule">The rule that was applied.</param>
    /// <param name="animationPath">The <c>.kf</c>'s virtual path exactly as the caller supplied it.</param>
    /// <param name="explicitPath">The explicit skeleton path, or null when the walk-up was used.</param>
    /// <param name="skeleton">The chosen occurrence; required exactly when the status is Resolved.</param>
    /// <param name="skeletonPath">The candidate path that chose it; required exactly when the status is Resolved.</param>
    /// <param name="candidates">Every candidate examined, in order.</param>
    /// <exception cref="ArgumentException">A chosen skeleton is present without Resolved, or missing with it.</exception>
    public NifModelSkeletonResolution(
        NifModelSkeletonResolutionStatus status,
        NifModelSkeletonRule rule,
        string animationPath,
        string? explicitPath,
        ModelSourceItem? skeleton,
        string? skeletonPath,
        IReadOnlyList<NifModelSkeletonCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(animationPath);
        ArgumentNullException.ThrowIfNull(candidates);
        var resolved = status == NifModelSkeletonResolutionStatus.Resolved;
        if (resolved != (skeleton is not null) || resolved != (skeletonPath is not null))
        {
            throw new ArgumentException("A chosen skeleton and its path are present exactly when the status is Resolved.",
                nameof(skeleton));
        }

        Status = status;
        Rule = rule;
        AnimationPath = animationPath;
        ExplicitPath = explicitPath;
        Skeleton = skeleton;
        SkeletonPath = skeletonPath;
        Candidates = candidates;
    }

    /// <summary>The outcome.</summary>
    public NifModelSkeletonResolutionStatus Status { get; }

    /// <summary>The rule that was applied (Explicit when an explicit path was supplied, else NearestAncestor).</summary>
    public NifModelSkeletonRule Rule { get; }

    /// <summary>The recorded name of <see cref="Rule" /> (<see cref="NifModelSkeletonResolver.RuleName" />).</summary>
    public string RuleName => NifModelSkeletonResolver.RuleName(Rule);

    /// <summary>The <c>.kf</c>'s virtual path exactly as the caller supplied it.</summary>
    public string AnimationPath { get; }

    /// <summary>The explicit skeleton path, or null when the walk-up was used.</summary>
    public string? ExplicitPath { get; }

    /// <summary>The chosen occurrence, or null when none was chosen.</summary>
    public ModelSourceItem? Skeleton { get; }

    /// <summary>The candidate path that chose <see cref="Skeleton" />, or null when none was chosen.</summary>
    public string? SkeletonPath { get; }

    /// <summary>Every candidate examined, in order; a candidate beyond the deciding one is never examined.</summary>
    public IReadOnlyList<NifModelSkeletonCandidate> Candidates { get; }

    /// <summary>True when a skeleton was chosen.</summary>
    public bool IsResolved => Status == NifModelSkeletonResolutionStatus.Resolved;

    /// <summary>The reason the reader reports when no skeleton was chosen; null when one was.</summary>
    public string? FailureReason => NifModelSkeletonResolver.Reason(Status);
}
