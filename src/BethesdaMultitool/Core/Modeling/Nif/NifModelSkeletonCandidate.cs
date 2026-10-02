namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One skeleton candidate <see cref="NifModelSkeletonResolver" /> examined, in the order it was examined (the explicit
///     path alone, or the walk-up from the <c>.kf</c>'s own directory toward the data root, up to the one that decided).
/// </summary>
/// <param name="Path">The path handed to the lookup.</param>
/// <param name="Outcome">Found, Missing or Ambiguous.</param>
/// <param name="OccurrenceCount">How many occurrences the lookup returned for the path.</param>
internal readonly record struct NifModelSkeletonCandidate(
    string Path,
    NifModelSkeletonCandidateOutcome Outcome,
    int OccurrenceCount);
