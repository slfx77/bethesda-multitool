using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.World;

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

    /// <summary>
    ///     VHGT heightmap parsed directly from the LAND record before any runtime mesh replacement.
    ///     This remains useful for content-based reverse lookups from standalone VHGT chunks.
    /// </summary>
    public LandHeightmap? ParsedHeightmap { get; init; }

    /// <summary>Cell X coordinate (from parent CELL or inferred).</summary>
    public int? CellX { get; init; }

    /// <summary>Cell Y coordinate (from parent CELL or inferred).</summary>
    public int? CellY { get; init; }

    /// <summary>Parent CELL FormID when the LAND record was recovered from a CELL children group.</summary>
    public uint? ParentCellFormId { get; init; }

    /// <summary>Parent worldspace FormID when known.</summary>
    public uint? WorldspaceFormId { get; init; }

    /// <summary>Texture layers (ATXT/BTXT).</summary>
    public List<LandTextureLayer> TextureLayers { get; init; } = [];

    /// <summary>Structured visual LAND data (VCLR/VTEX/BTXT/ATXT/VTXT).</summary>
    public LandVisualData? VisualData { get; init; }

    /// <summary>Number of VCLR bytes present in this LAND record.</summary>
    public int VclrByteCount { get; init; }

    /// <summary>Number of VTEX texture indices present in this LAND record.</summary>
    public int VtexCount { get; init; }

    /// <summary>Number of BTXT texture base-layer subrecords present in this LAND record.</summary>
    public int BtxtCount { get; init; }

    /// <summary>Number of ATXT texture alpha-layer subrecords present in this LAND record.</summary>
    public int AtxtCount { get; init; }

    /// <summary>Number of VTXT subrecords present in this LAND record.</summary>
    public int VtxtCount { get; init; }

    /// <summary>Number of bytes across all VTXT subrecords present in this LAND record.</summary>
    public int VtxtByteCount { get; init; }

    /// <summary>Cell X coordinate from runtime LoadedLandData (more reliable).</summary>
    public int? RuntimeCellX { get; init; }

    /// <summary>Cell Y coordinate from runtime LoadedLandData (more reliable).</summary>
    public int? RuntimeCellY { get; init; }

    /// <summary>Base height from runtime LoadedLandData.</summary>
    public float? RuntimeBaseHeight { get; init; }

    /// <summary>File offset of the runtime TESObjectLAND struct.</summary>
    public long? RuntimeLandOffset { get; init; }

    /// <summary>File offset of the runtime LoadedLandData struct.</summary>
    public long? RuntimeLoadedDataOffset { get; init; }

    /// <summary>Terrain mesh extracted from runtime heap pointers.</summary>
    public RuntimeTerrainMesh? RuntimeTerrainMesh { get; init; }

    /// <summary>PDB-derived LoadedLandData pointer diagnostics captured from runtime memory.</summary>
    public RuntimeLoadedLandDiagnostics? RuntimeLandDiagnostics { get; init; }

    /// <summary>Runtime TESLandTexture records referenced by reconstructed LAND texture layers.</summary>
    public IReadOnlyList<LandscapeTextureRecord> RuntimeLandTextures { get; init; } = [];

    /// <summary>Quality diagnostic captured BEFORE sanitization, reflecting true data quality.</summary>
    public TerrainMeshDiagnostic? PreSanitizationDiagnostic { get; init; }

    /// <summary>
    ///     Get the best available cell X coordinate. Authored parent CELL placement wins when present;
    ///     runtime LoadedLandData coordinates are a fallback for runtime-only captures.
    /// </summary>
    public int? BestCellX => CellX ?? RuntimeCellX;

    /// <summary>
    ///     Get the best available cell Y coordinate. Authored parent CELL placement wins when present;
    ///     runtime LoadedLandData coordinates are a fallback for runtime-only captures.
    /// </summary>
    public int? BestCellY => CellY ?? RuntimeCellY;
}
