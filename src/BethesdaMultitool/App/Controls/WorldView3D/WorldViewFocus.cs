using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>
///     A snapshot of "where you are + what you've selected" in a world viewer, shared between the 2D
///     map (<see cref="WorldMapControl" />) and the 3D view (<see cref="WorldView3DControl" />) so that
///     switching between them keeps you in the same area with the same selection.
///     <para>
///         For an EXTERIOR the location is a single world XY in the shared frame that
///         <c>PlacedReference.X/Y</c> (and the 3D camera) live in — the 2D map is the same frame with Y
///         negated, so a round-trip only flips the sign. INTERIORS have no shared world frame, so they
///         fall back to the cell. <see cref="Selected" /> is the same <see cref="PlacedReference" />
///         instance on both sides (both views load one <c>WorldViewData</c>), so it transfers directly.
///         <see cref="WorldspaceComboIndex" /> is interchangeable between the two combos (both index the
///         same <c>WorldViewData.Worldspaces</c> list + an unlinked-exterior tail entry).
///     </para>
/// </summary>
internal readonly record struct WorldViewFocus(
    int WorldspaceComboIndex,
    bool IsInterior,
    CellRecord? InteriorCell,
    float WorldX,
    float WorldY,
    PlacedReference? Selected,
    CellSortMode SortMode);
