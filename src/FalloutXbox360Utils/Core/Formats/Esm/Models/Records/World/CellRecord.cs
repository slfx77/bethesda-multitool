using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Parsed Cell record with placed objects.
///     Aggregates data from CELL main record header and associated REFR/ACHR/ACRE records.
/// </summary>
public record CellRecord
{
    /// <summary>FormID of the CELL record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Cell X coordinate in the grid (from XCLC, null for interior cells).</summary>
    public int? GridX { get; init; }

    /// <summary>Cell Y coordinate in the grid (from XCLC, null for interior cells).</summary>
    public int? GridY { get; init; }

    /// <summary>Parent worldspace FormID (null for interior cells).</summary>
    public uint? WorldspaceFormId { get; init; }

    /// <summary>Cell flags from DATA subrecord.</summary>
    public byte Flags { get; init; }

    /// <summary>Whether this is an interior cell.</summary>
    public bool IsInterior => (Flags & 0x01) != 0;

    /// <summary>Whether this cell has water.</summary>
    public bool HasWater => (Flags & 0x02) != 0;

    /// <summary>Water height (XCLW subrecord, interior cells).</summary>
    public float? WaterHeight { get; init; }

    /// <summary>Encounter zone FormID (XEZN subrecord).</summary>
    public uint? EncounterZoneFormId { get; init; }

    /// <summary>Music type FormID (XCMO subrecord).</summary>
    public uint? MusicTypeFormId { get; init; }

    /// <summary>Acoustic space FormID (XCAS subrecord).</summary>
    public uint? AcousticSpaceFormId { get; init; }

    /// <summary>Image space FormID (XCIM subrecord).</summary>
    public uint? ImageSpaceFormId { get; init; }

    /// <summary>Lighting template FormID (LTMP subrecord / pLightingTemplate pointer).</summary>
    public uint? LightingTemplateFormId { get; init; }

    /// <summary>Lighting template inheritance flags (LTMP data / iLightingTemplateInheritanceFlags).</summary>
    public uint? LightingTemplateInheritanceFlags { get; init; }

    /// <summary>Placed objects in this cell (REFR, ACHR, ACRE records).</summary>
    public List<PlacedReference> PlacedObjects { get; init; } = [];

    /// <summary>FormIDs of cells reachable via doors in this cell.</summary>
    public List<uint> LinkedCellFormIds { get; init; } = [];

    /// <summary>Associated LAND record heightmap (if found).</summary>
    public LandHeightmap? Heightmap { get; init; }

    /// <summary>Runtime terrain mesh extracted from LoadedLandData heap pointers (if available).</summary>
    public RuntimeTerrainMesh? RuntimeTerrainMesh { get; init; }

    /// <summary>
    ///     True when this cell contains persistent references whose world positions may be
    ///     far from the cell's own grid coordinates.  The worldspace persistent cell typically
    ///     has grid (0,0) but holds objects scattered across the entire map.  Rendering code
    ///     must not cull this cell by grid bounds — it should use per-object IsPointInView instead.
    /// </summary>
    public bool HasPersistentObjects { get; init; }

    /// <summary>True for synthetic cells created to hold orphan references in DMP mode.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    ///     True when this CellRecord represents the worldspace's persistent cell container
    ///     (the logical owner of refs flagged with the persistent flag 0x0400). Persistent
    ///     cells have no grid coordinate of their own — refs they own are redistributed to
    ///     real exterior tiles by world position. Renderers must not draw this cell at any
    ///     grid tile, and reports should label it "Persistent" instead of "[gx,gy]".
    /// </summary>
    public bool IsPersistentCell { get; init; }

    /// <summary>
    ///     True when this is a synthetic catch-all bucket for orphan refs whose owning
    ///     cell could not be resolved (no parent cell pointer and no plausible grid match
    ///     against any worldspace's known bounds). GridX/GridY are null. Renderers should
    ///     surface these in a side panel rather than placing them on a tile.
    /// </summary>
    public bool IsUnresolvedBucket { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
