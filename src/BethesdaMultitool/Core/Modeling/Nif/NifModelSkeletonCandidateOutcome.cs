namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>What the lookup returned for one skeleton candidate (<see cref="NifModelSkeletonCandidate" />).</summary>
internal enum NifModelSkeletonCandidateOutcome
{
    /// <summary>Exactly one occurrence: the candidate is chosen.</summary>
    Found,

    /// <summary>No occurrence: the walk-up moves one directory up.</summary>
    Missing,

    /// <summary>More than one occurrence: the resolution stops as ambiguous.</summary>
    Ambiguous
}
