using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Pre-computed data for the World tab, built once from RecordCollection.
/// </summary>
internal sealed class WorldViewData
{
    public required List<WorldspaceRecord> Worldspaces { get; init; }
    public required List<CellRecord> InteriorCells { get; init; }
    public required Dictionary<uint, ObjectBounds> BoundsIndex { get; init; }
    public required Dictionary<uint, PlacedObjectCategory> CategoryIndex { get; init; }
    public required FormIdResolver Resolver { get; init; }
    public required List<PlacedReference> MapMarkers { get; init; }

    /// <summary>Map markers grouped by worldspace FormID for per-worldspace filtering.</summary>
    public required Dictionary<uint, List<PlacedReference>> MarkersByWorldspace { get; init; }

    /// <summary>All cells (exterior + interior) for cell browser mode.</summary>
    public required List<CellRecord> AllCells { get; init; }

    /// <summary>Lookup from FormID to CellRecord for navigation.</summary>
    public required Dictionary<uint, CellRecord> CellByFormId { get; init; }

    /// <summary>Reverse lookup: placed reference FormID → parent CellRecord.</summary>
    public required Dictionary<uint, CellRecord> RefrToCellIndex { get; init; }

    /// <summary>Exterior cells with grid coordinates that aren't linked to any worldspace (common in DMP files).</summary>
    public required List<CellRecord> UnlinkedExteriorCells { get; init; }

    /// <summary>Default water height for the first worldspace (WRLD DNAM fallback).</summary>
    public float? DefaultWaterHeight { get; init; }

    /// <summary>Pre-computed grayscale heightmap (1 byte per pixel, normalized 0-255).</summary>
    public byte[]? HeightmapGrayscale { get; init; }

    /// <summary>Pre-computed water mask (0 = no water, 180 = underwater).</summary>
    public byte[]? HeightmapWaterMask { get; init; }

    /// <summary>Width of the pre-computed heightmap bitmap in pixels.</summary>
    public int HeightmapPixelWidth { get; init; }

    /// <summary>Height of the pre-computed heightmap bitmap in pixels.</summary>
    public int HeightmapPixelHeight { get; init; }

    /// <summary>Minimum cell grid X for positioning the heightmap.</summary>
    public int HeightmapMinCellX { get; init; }

    /// <summary>Maximum cell grid Y for positioning the heightmap.</summary>
    public int HeightmapMaxCellY { get; init; }

    /// <summary>Source file path, used for default color scheme selection.</summary>
    public string? SourceFilePath { get; init; }

    /// <summary>Spawn resolution index for leveled list and AI package lookups.</summary>
    public SpawnResolutionIndex? SpawnIndex { get; init; }

    /// <summary>Reference FormID → world position for drawing AI package overlays.</summary>
    public Dictionary<uint, (float X, float Y)>? RefPositionIndex { get; init; }
}
