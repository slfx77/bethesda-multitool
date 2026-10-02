using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Looks up one skeleton path for <see cref="NifModelSkeletonResolver" /> (cut-1b slice 3): every exact source
///     occurrence the path names, as the <see cref="ModelCompanionResolver" /> contract returns them (empty means
///     missing; more than one needs reader policy). The production lookup wraps the read context's companion resolver
///     over the data-root VFS (<see cref="NifModelSkeletonResolver.FromCompanionResolver" />, which BMT serves with
///     <see cref="BethesdaTextureCompanions" />); tests inject an in-memory lookup, so no archive is needed.
/// </summary>
/// <param name="path">
///     A walk-up candidate (a normalized, slash-separated virtual path ending in <c>skeleton.nif</c>) or the explicit
///     skeleton path exactly as the caller supplied it.
/// </param>
/// <returns>The occurrences the path names, in the lookup's order; never null.</returns>
internal delegate IReadOnlyList<ModelSourceItem> NifModelSkeletonLookup(string path);
