namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

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

    /// <summary>File offset of the TESObjectLAND runtime struct.</summary>
    public long LandOffset { get; init; }

    /// <summary>File offset of the LoadedLandData struct.</summary>
    public long LoadedDataOffset { get; init; }
}
