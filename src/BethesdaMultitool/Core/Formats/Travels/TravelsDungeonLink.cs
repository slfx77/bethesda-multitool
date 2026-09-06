namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     One row of <c>geomin.dat</c>: how a dungeon connects to its neighbours. All six values are
///     signed bytes and −1 means "none".
/// </summary>
/// <param name="Id">1-based dungeon id, which is the row ordinal plus one. Row 0 is the camp.</param>
/// <param name="North">Dungeon id reached by leaving the map northward (y &lt; 0).</param>
/// <param name="East">Dungeon id reached by leaving eastward (x past the map width).</param>
/// <param name="South">Dungeon id reached by leaving southward.</param>
/// <param name="West">Dungeon id reached by leaving westward (x &lt; 0).</param>
/// <param name="ExitDirection">
///     Direction code of the onward doorway — 1 N, 2 E, 3 S, 4 W, −1 none — which fixes where on
///     the 35 x 35 map the door stands.
/// </param>
/// <param name="ReturnDirection">Direction code of the doorway leading back toward the camp.</param>
internal sealed record TravelsDungeonLink(
    int Id,
    sbyte North,
    sbyte East,
    sbyte South,
    sbyte West,
    sbyte ExitDirection,
    sbyte ReturnDirection);
