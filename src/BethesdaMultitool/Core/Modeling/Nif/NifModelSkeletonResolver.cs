using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 3: chooses the skeleton a <c>.kf</c>'s targets bind to (plan section 1.7, owner rulings D4, D12 and
///     D14). Pure and synchronous: it never reads a skeleton's bytes, so nothing about the skeleton's content can make it
///     skip one.
/// </summary>
/// <remarks>
///     <list type="number">
///         <item>
///             An explicit skeleton path (the <c>bmt.skeleton</c> option and the CLI's <c>--skeleton</c>, slice 10) always wins.
///             It is handed to the lookup unchanged and is the only candidate examined; when it names nothing the
///             resolution is <see cref="NifModelSkeletonResolutionStatus.ExplicitSkeletonMissing" /> and the walk-up is
///             not tried in its place.
///         </item>
///         <item>
///             Otherwise the <c>.kf</c>'s virtual path is normalized (<see cref="AssetPath.Normalize" />) and walked up
///             from its own directory to the data root, the same upward enumeration as the renderer's
///             <c>NifModelFamilyAnimationResolver.EnumerateAncestorDirectories</c>. The NEAREST <c>skeleton.nif</c> that
///             names exactly one occurrence wins, with NO compatibility gate: the renderer's rule (skip a skeleton unless
///             every skin-bone name is present, names compared case-insensitively) is not applied, because the '##'
///             attachment names of every weapon <c>.kf</c> would reject its skeleton. Unbound targets are reported per
///             name by <see cref="NifModelTargetNames" /> instead.
///         </item>
///         <item>
///             A candidate that names more than one occurrence stops the walk as
///             <see cref="NifModelSkeletonResolutionStatus.AmbiguousSkeleton" />; no candidate at all gives
///             <see cref="NifModelSkeletonResolutionStatus.NoSkeleton" /> ('no skeleton found; pass --skeleton', D4).
///         </item>
///     </list>
///     <para>
///         Path spelling (case, separators, archive override order) is the lookup's business: BMT's lookup is the
///         layered data-root VFS, whose loose files shadow the archives in the engine's order. The case-sensitive rule
///         of D12 applies to target names, not to file paths.
///     </para>
/// </remarks>
internal static class NifModelSkeletonResolver
{
    /// <summary>The canonical skeleton file name each ancestor directory is probed for.</summary>
    public const string SkeletonFileName = "skeleton.nif";

    /// <summary>The longest <c>.kf</c> path accepted (the renderer's resolver uses the same bound).</summary>
    public const int MaximumPathLength = 4096;

    /// <summary>The recorded name of <see cref="NifModelSkeletonRule.Explicit" />.</summary>
    public const string ExplicitRuleName = "explicit";

    /// <summary>The recorded name of <see cref="NifModelSkeletonRule.NearestAncestor" />.</summary>
    public const string NearestAncestorRuleName = "nearest-ancestor";

    /// <summary>The reason for <see cref="NifModelSkeletonResolutionStatus.NoSkeleton" /> (owner ruling D4).</summary>
    public const string NoSkeletonReason = "no skeleton found; pass --skeleton";

    /// <summary>The reason for <see cref="NifModelSkeletonResolutionStatus.ExplicitSkeletonMissing" />.</summary>
    public const string ExplicitSkeletonMissingReason = "the explicit skeleton was not found";

    /// <summary>The reason for <see cref="NifModelSkeletonResolutionStatus.AmbiguousSkeleton" />.</summary>
    public const string AmbiguousSkeletonReason = "the nearest skeleton candidate names more than one source occurrence";

    /// <summary>The reason for <see cref="NifModelSkeletonResolutionStatus.InvalidAnimationPath" />.</summary>
    public const string InvalidAnimationPathReason = "the animation path is not a bounded relative virtual path";

    /// <summary>Chooses the skeleton for one <c>.kf</c>.</summary>
    /// <param name="animationPath">The <c>.kf</c>'s virtual path within the data root (either separator).</param>
    /// <param name="explicitSkeletonPath">The explicit skeleton path, which always wins; null to walk up.</param>
    /// <param name="lookup">The lookup over the data-root VFS.</param>
    /// <returns>The resolution, with every candidate examined.</returns>
    /// <exception cref="ArgumentException">The explicit skeleton path is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The lookup returned null.</exception>
    public static NifModelSkeletonResolution Resolve(
        string animationPath,
        string? explicitSkeletonPath,
        NifModelSkeletonLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(animationPath);
        ArgumentNullException.ThrowIfNull(lookup);
        if (explicitSkeletonPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(explicitSkeletonPath);
            var candidate = Examine(explicitSkeletonPath, lookup, out var chosen);
            var status = candidate.Outcome switch
            {
                NifModelSkeletonCandidateOutcome.Found => NifModelSkeletonResolutionStatus.Resolved,
                NifModelSkeletonCandidateOutcome.Missing => NifModelSkeletonResolutionStatus.ExplicitSkeletonMissing,
                _ => NifModelSkeletonResolutionStatus.AmbiguousSkeleton
            };
            return new NifModelSkeletonResolution(status, NifModelSkeletonRule.Explicit, animationPath,
                explicitSkeletonPath, chosen, chosen is null ? null : explicitSkeletonPath, [candidate]);
        }

        if (!TryGetCandidatePaths(animationPath, out var paths))
        {
            return new NifModelSkeletonResolution(NifModelSkeletonResolutionStatus.InvalidAnimationPath,
                NifModelSkeletonRule.NearestAncestor, animationPath, null, null, null, []);
        }

        var examined = new List<NifModelSkeletonCandidate>(paths.Count);
        foreach (var path in paths)
        {
            var candidate = Examine(path, lookup, out var chosen);
            examined.Add(candidate);
            if (candidate.Outcome == NifModelSkeletonCandidateOutcome.Found)
            {
                return new NifModelSkeletonResolution(NifModelSkeletonResolutionStatus.Resolved,
                    NifModelSkeletonRule.NearestAncestor, animationPath, null, chosen, path, examined.AsReadOnly());
            }

            if (candidate.Outcome == NifModelSkeletonCandidateOutcome.Ambiguous)
            {
                return new NifModelSkeletonResolution(NifModelSkeletonResolutionStatus.AmbiguousSkeleton,
                    NifModelSkeletonRule.NearestAncestor, animationPath, null, null, null, examined.AsReadOnly());
            }
        }

        return new NifModelSkeletonResolution(NifModelSkeletonResolutionStatus.NoSkeleton,
            NifModelSkeletonRule.NearestAncestor, animationPath, null, null, null, examined.AsReadOnly());
    }

    /// <summary>
    ///     The walk-up candidates for a <c>.kf</c>, nearest first: <c>skeleton.nif</c> in the <c>.kf</c>'s own directory,
    ///     then in each ancestor directory, ending with the data root's own <c>skeleton.nif</c>. Directory spelling is kept
    ///     as given; separators become '/'.
    /// </summary>
    /// <param name="animationPath">The <c>.kf</c>'s virtual path.</param>
    /// <param name="candidates">The candidate paths; empty when the path is refused.</param>
    /// <returns>False when the path is empty, longer than <see cref="MaximumPathLength" />, rooted or traversing.</returns>
    public static bool TryGetCandidatePaths(string animationPath, out IReadOnlyList<string> candidates)
    {
        candidates = [];
        if (string.IsNullOrWhiteSpace(animationPath) || animationPath.Length > MaximumPathLength)
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = AssetPath.Normalize(animationPath);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (normalized.Length == 0)
        {
            return false;
        }

        var paths = new List<string>();
        var separator = normalized.LastIndexOf('/');
        while (separator > 0)
        {
            paths.Add(string.Concat(normalized.AsSpan(0, separator + 1), SkeletonFileName));
            separator = normalized.LastIndexOf('/', separator - 1);
        }

        paths.Add(SkeletonFileName);
        candidates = paths.AsReadOnly();
        return true;
    }

    /// <summary>
    ///     A lookup over a <see cref="ModelCompanionResolver" /> (the read context's resolver; BMT's is
    ///     <see cref="BethesdaTextureCompanions.ResolveAsync" /> over the data-root VFS), called synchronously the way the
    ///     texture source calls it.
    /// </summary>
    /// <param name="resolver">The companion resolver.</param>
    /// <param name="owner">The <c>.kf</c> occurrence asking for its skeleton.</param>
    /// <param name="cancellationToken">Passed to every resolver call.</param>
    /// <returns>The lookup.</returns>
    public static NifModelSkeletonLookup FromCompanionResolver(
        ModelCompanionResolver resolver,
        ModelSourceItem owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(owner);
        return path => resolver(owner, path, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The recorded name of a rule.</summary>
    /// <param name="rule">The rule.</param>
    /// <returns><see cref="ExplicitRuleName" /> or <see cref="NearestAncestorRuleName" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The rule is not defined.</exception>
    public static string RuleName(NifModelSkeletonRule rule)
    {
        return rule switch
        {
            NifModelSkeletonRule.Explicit => ExplicitRuleName,
            NifModelSkeletonRule.NearestAncestor => NearestAncestorRuleName,
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown skeleton rule.")
        };
    }

    /// <summary>The reason the reader reports for a status; null for Resolved.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The reason text, or null.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The status is not defined.</exception>
    public static string? Reason(NifModelSkeletonResolutionStatus status)
    {
        return status switch
        {
            NifModelSkeletonResolutionStatus.Resolved => null,
            NifModelSkeletonResolutionStatus.NoSkeleton => NoSkeletonReason,
            NifModelSkeletonResolutionStatus.ExplicitSkeletonMissing => ExplicitSkeletonMissingReason,
            NifModelSkeletonResolutionStatus.AmbiguousSkeleton => AmbiguousSkeletonReason,
            NifModelSkeletonResolutionStatus.InvalidAnimationPath => InvalidAnimationPathReason,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown skeleton resolution status.")
        };
    }

    /// <summary>Asks the lookup about one path and classifies the answer.</summary>
    private static NifModelSkeletonCandidate Examine(string path, NifModelSkeletonLookup lookup,
        out ModelSourceItem? chosen)
    {
        var occurrences = lookup(path) ??
                          throw new InvalidOperationException($"The skeleton lookup returned null for '{path}'.");
        chosen = null;
        if (occurrences.Count == 0)
        {
            return new NifModelSkeletonCandidate(path, NifModelSkeletonCandidateOutcome.Missing, 0);
        }

        if (occurrences.Count > 1)
        {
            return new NifModelSkeletonCandidate(path, NifModelSkeletonCandidateOutcome.Ambiguous, occurrences.Count);
        }

        chosen = occurrences[0];
        return new NifModelSkeletonCandidate(path, NifModelSkeletonCandidateOutcome.Found, 1);
    }
}
