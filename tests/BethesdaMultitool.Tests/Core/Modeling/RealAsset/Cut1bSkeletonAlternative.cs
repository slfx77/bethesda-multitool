namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Another copy of a provisional skeleton pin's path in the same (game, platform) namespace (see
///     <see cref="Cut1bSkeletonCompanion" />): pinned by SHA-256 as a manifest file that lists the <c>.kf</c> in
///     <c>skeletonCandidateFor</c>.
/// </summary>
/// <param name="Sha256">The copy's pinned lowercase SHA-256.</param>
/// <param name="Source">The archive the census found this copy in.</param>
/// <param name="Entry">The copy's path inside that archive.</param>
/// <param name="GamePlatform">The namespace, the same as the pinned skeleton's.</param>
internal sealed record Cut1bSkeletonAlternative(string Sha256, string Source, string Entry, string GamePlatform);
