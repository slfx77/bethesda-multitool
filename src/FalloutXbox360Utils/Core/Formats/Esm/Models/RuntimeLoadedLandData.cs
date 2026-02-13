namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Runtime LoadedLandData structure containing cell coordinates for heightmap stitching.
///     Read from TESObjectLAND.pLoadedData pointer at runtime offset +56.
/// </summary>
public record RuntimeLoadedLandData
{
    /// <summary>FormID of the parent LAND record.</summary>
    public uint FormId { get; init; }

    /// <summary>Cell grid X coordinate.</summary>
    public int CellX { get; init; }

    /// <summary>Cell grid Y coordinate.</summary>
    public int CellY { get; init; }

    /// <summary>Base elevation for the cell.</summary>
    public float BaseHeight { get; init; }

    /// <summary>Minimum terrain height in this cell (from HeightExtents at offset +24).</summary>
    public float? MinHeight { get; init; }

    /// <summary>Maximum terrain height in this cell (from HeightExtents at offset +28).</summary>
    public float? MaxHeight { get; init; }

    /// <summary>File offset of the TESObjectLAND runtime struct.</summary>
    public long LandOffset { get; init; }

    /// <summary>File offset of the LoadedLandData struct.</summary>
    public long LoadedDataOffset { get; init; }

    /// <summary>Terrain mesh extracted from heap pointers (ppVertices, ppNormals, ppColorsA).</summary>
    public RuntimeTerrainMesh? TerrainMesh { get; init; }
}
