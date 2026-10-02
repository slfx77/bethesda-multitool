namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The skeleton a cut-1b manifest <c>.kf</c> resolves its targets against, as <c>nif_cover.py --scope cut1b</c>
///     pinned it: the nearest ancestor <c>skeleton.nif</c> of the <c>.kf</c>'s path in its primary source's
///     (game, platform) namespace, with no compatibility gate (<see cref="WalkUpRule" />), or, for an FNV <c>.kf</c>
///     whose own namespace has none, the same walk-up through Fallout 3's archives (<see cref="Fo3FallbackRule" />,
///     what the reader's <c>--skeleton</c> option supplies). The skeleton is itself a manifest file whose
///     <c>skeletonFor</c> lists the <c>.kf</c>.
/// </summary>
/// <remarks>
///     When the namespace holds more than one distinct copy of the skeleton's path (measured 2026-09-25:
///     <c>hologram/skeleton.nif</c> in Dead Money and Old World Blues, <c>nvmantis/skeleton.nif</c> in
///     <c>Fallout - Meshes.bsa</c> and Old World Blues), the pin is the first copy in census order, which is
///     archive-name order and not established as the engine's archive override order. Such a pin is
///     <see cref="IsProvisional" />: <see cref="Provisional" /> says why, and every other copy is pinned under
///     <see cref="Alternatives" /> as a manifest file listing the <c>.kf</c> in <c>skeletonCandidateFor</c>, until plan
///     slice 3 settles the override order.
/// </remarks>
/// <param name="Sha256">The skeleton's pinned lowercase SHA-256.</param>
/// <param name="Source">The archive the walk-up found it in (the first archive in name order holding the path).</param>
/// <param name="Entry">The skeleton's path inside that archive.</param>
/// <param name="GamePlatform">The namespace it came from, for example <c>FNV/PC</c> or <c>FO3/PC</c>.</param>
/// <param name="Rule"><see cref="WalkUpRule" /> or <see cref="Fo3FallbackRule" />.</param>
/// <param name="Provisional">Why the pin is provisional; null when the path has one copy in the namespace.</param>
/// <param name="Alternatives">Every other copy of the path in the namespace (empty unless provisional).</param>
internal sealed record Cut1bSkeletonCompanion(
    string Sha256,
    string Source,
    string Entry,
    string GamePlatform,
    string Rule,
    string? Provisional,
    IReadOnlyList<Cut1bSkeletonAlternative> Alternatives)
{
    /// <summary>The nearest ancestor <c>skeleton.nif</c> in the <c>.kf</c>'s own namespace.</summary>
    public const string WalkUpRule = "walk-up";

    /// <summary>None in the FNV namespace: the nearest ancestor <c>skeleton.nif</c> in Fallout 3's archives.</summary>
    public const string Fo3FallbackRule = "fo3-fallback";

    /// <summary>True when the namespace holds other copies of the path (see the remarks).</summary>
    public bool IsProvisional => Provisional is not null;
}
