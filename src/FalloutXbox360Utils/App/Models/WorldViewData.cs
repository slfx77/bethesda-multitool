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
    public required Dictionary<uint, string> FormIdToEditorId { get; init; }
    public required Dictionary<uint, string> FormIdToDisplayName { get; init; }
    public required List<PlacedReference> MapMarkers { get; init; }

    /// <summary>Map markers grouped by worldspace FormID for per-worldspace filtering.</summary>
    public required Dictionary<uint, List<PlacedReference>> MarkersByWorldspace { get; init; }

    /// <summary>All cells (exterior + interior) for cell browser mode.</summary>
    public required List<CellRecord> AllCells { get; init; }

    /// <summary>Exterior cells with grid coordinates that aren't linked to any worldspace (common in DMP files).</summary>
    public required List<CellRecord> UnlinkedExteriorCells { get; init; }

    /// <summary>Default water height for the first worldspace (WRLD DNAM fallback).</summary>
    public float? DefaultWaterHeight { get; init; }

    /// <summary>Pre-computed RGBA pixel data for the default worldspace heightmap.</summary>
    public byte[]? HeightmapPixels { get; init; }

    /// <summary>Width of the pre-computed heightmap bitmap in pixels.</summary>
    public int HeightmapPixelWidth { get; init; }

    /// <summary>Height of the pre-computed heightmap bitmap in pixels.</summary>
    public int HeightmapPixelHeight { get; init; }

    /// <summary>Minimum cell grid X for positioning the heightmap.</summary>
    public int HeightmapMinCellX { get; init; }

    /// <summary>Maximum cell grid Y for positioning the heightmap.</summary>
    public int HeightmapMaxCellY { get; init; }
}
