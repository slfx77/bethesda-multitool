namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>One row of the asset-type census: how many leaves of one kind a source tree lists.</summary>
/// <param name="Kind">A filterable (non-folder) kind.</param>
/// <param name="Count">The number of leaves of that kind anywhere under the census root.</param>
internal readonly record struct AssetKindCount(AssetNodeKind Kind, int Count);
