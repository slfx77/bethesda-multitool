namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Extracted LAND record with heightmap and texture data.
///     Enables terrain visualization from memory dumps.
/// </summary>
public record ExtractedLandRecord
{
    /// <summary>Parent main record information.</summary>
    public required DetectedMainRecord Header { get; init; }

    /// <summary>VHGT heightmap data - 33×33 grid of height deltas.</summary>
    public LandHeightmap? Heightmap { get; init; }

    /// <summary>Cell X coordinate (from parent CELL or inferred).</summary>
    public int? CellX { get; init; }

    /// <summary>Cell Y coordinate (from parent CELL or inferred).</summary>
    public int? CellY { get; init; }

    /// <summary>Texture layers (ATXT/BTXT).</summary>
    public List<LandTextureLayer> TextureLayers { get; init; } = [];

    /// <summary>Cell X coordinate from runtime LoadedLandData (more reliable).</summary>
    public int? RuntimeCellX { get; init; }

    /// <summary>Cell Y coordinate from runtime LoadedLandData (more reliable).</summary>
    public int? RuntimeCellY { get; init; }

    /// <summary>Base height from runtime LoadedLandData.</summary>
    public float? RuntimeBaseHeight { get; init; }

    /// <summary>
    ///     Get the best available cell X coordinate, preferring runtime data.
    /// </summary>
    public int? BestCellX => RuntimeCellX ?? CellX;

    /// <summary>
    ///     Get the best available cell Y coordinate, preferring runtime data.
    /// </summary>
    public int? BestCellY => RuntimeCellY ?? CellY;
}
