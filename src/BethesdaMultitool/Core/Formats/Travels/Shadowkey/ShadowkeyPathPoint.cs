namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One waypoint of a <c>.pth</c> path: a pair of unsigned 24.8 fixed-point tile coordinates
///     (raw / 256 = tiles). Points are EIGHT bytes each — two u32 — which the per-file tiling
///     settles across all 21 zones; there is no third component hiding in the record. All 86
///     retail points land inside their zone's <c>.zmp</c> map.
/// </summary>
/// <param name="RawX">Raw 24.8 X.</param>
/// <param name="RawY">Raw 24.8 Y.</param>
internal readonly record struct ShadowkeyPathPoint(uint RawX, uint RawY)
{
    /// <summary>X in whole-and-fractional map tiles.</summary>
    public float TileX => RawX / ShadowkeyZoneFiles.FixedPointScale;

    /// <summary>Y in whole-and-fractional map tiles.</summary>
    public float TileY => RawY / ShadowkeyZoneFiles.FixedPointScale;
}
