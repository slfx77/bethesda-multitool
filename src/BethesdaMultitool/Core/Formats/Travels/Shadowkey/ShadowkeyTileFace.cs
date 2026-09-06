namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     Which face of a Shadowkey zone tile is being built. The four wall values name the
///     GRID direction the wall faces outward along, not a compass bearing in the fiction: +y is
///     "south" only in the sense that grid rows run in that direction (see
///     <see cref="ShadowkeyZoneMap" />, which is row-major with x varying fastest).
/// </summary>
internal enum ShadowkeyTileFaceKind
{
    /// <summary>The tile's floor, seen from above.</summary>
    Floor,

    /// <summary>The tile's ceiling, seen from below.</summary>
    Ceiling,

    /// <summary>The wall on the tile's -y edge.</summary>
    WallNorth,

    /// <summary>The wall on the tile's +x edge.</summary>
    WallEast,

    /// <summary>The wall on the tile's +y edge.</summary>
    WallSouth,

    /// <summary>The wall on the tile's -x edge.</summary>
    WallWest
}

/// <summary>
///     Everything the zone builder knows about one face when it asks a
///     <see cref="IShadowkeyTileMaterialResolver" /> what to paint on it.
///     <para>
///         The prototype is handed over WHOLE rather than pre-reduced to a surface slot, because
///         which of <see cref="ShadowkeyCellPrototype.SurfaceSlots" /> belongs to which face is
///         unresolved — eight zones use a slot value at or above their <c>.ztx</c> texture count,
///         so the slots are not texture indices, and no <c>.zmp</c> byte lane is bounded by the
///         <c>.sur</c> count in every zone either. Pinning a face-to-slot mapping in this contract
///         would bake a guess into the seam the real resolver has to replace.
///     </para>
/// </summary>
/// <param name="X">Tile column, 0..<see cref="ShadowkeyZoneMap.Width" />-1.</param>
/// <param name="Y">Tile row, 0..<see cref="ShadowkeyZoneMap.Height" />-1.</param>
/// <param name="Kind">Which face of the tile.</param>
/// <param name="Cell">The grid cell, carrying its flags and its unresolved u16.</param>
/// <param name="Prototype">The <c>.zcp</c> record the cell indexes.</param>
internal readonly record struct ShadowkeyTileFace(
    int X,
    int Y,
    ShadowkeyTileFaceKind Kind,
    ShadowkeyMapCell Cell,
    ShadowkeyCellPrototype Prototype)
{
    /// <summary>True for the four wall kinds.</summary>
    public bool IsWall =>
        Kind is ShadowkeyTileFaceKind.WallNorth or ShadowkeyTileFaceKind.WallEast
            or ShadowkeyTileFaceKind.WallSouth or ShadowkeyTileFaceKind.WallWest;
}
