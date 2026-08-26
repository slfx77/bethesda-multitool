using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

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

    /// <summary>
    ///     Exterior cell edge length in world units, when it differs from the engine default
    ///     (<c>TerrainConstants.LandCellWorldSize</c> = 4096, used by FO3/FNV/Oblivion/Skyrim).
    ///     Morrowind exterior cells are 8192. 0 means "use the default" — the terrain builder reads
    ///     this to place + space the heightmap so it aligns with the cell's absolute-coordinate objects.
    /// </summary>
    public float CellWorldSize { get; init; }

    /// <summary>Diagnostic source for the current worldspace assignment.</summary>
    public string? WorldspaceAssignmentSource { get; init; }

    /// <summary>
    ///     Candidate worldspaces considered during fallback inference. Multiple entries
    ///     mean bounds alone were ambiguous and a stronger signal was needed.
    /// </summary>
    public IReadOnlyList<uint> CandidateWorldspaceFormIds { get; init; } = [];

    /// <summary>Cell flags from DATA subrecord.</summary>
    public byte Flags { get; init; }

    /// <summary>Whether this is an interior cell.</summary>
    public bool IsInterior => (Flags & 0x01) != 0;

    /// <summary>
    ///     Whether this interior uses exterior sky/weather semantics (CELL DATA bit 7). This is the
    ///     canonical interpretation used by rendering, image-space selection, and capture telemetry.
    /// </summary>
    public bool BehavesLikeExterior => IsInterior && (Flags & 0x80) != 0;

    /// <summary>Whether this cell has water.</summary>
    public bool HasWater => (Flags & 0x02) != 0;

    /// <summary>
    ///     Per-cell water height from XCLW. On an exterior CELL, the canonical sentinel means no
    ///     explicit per-cell override, so resolution uses the WRLD default; interiors have no WRLD fallback.
    /// </summary>
    public float? WaterHeight { get; init; }

    /// <summary>
    ///     Runtime-only water corroboration: TESObjectCELL::bAutoWaterLoaded, the engine's own
    ///     "this cell created its auto-water" bool. Null when the source is not a memory dump, or
    ///     when the byte held neither 0 nor 1 (garbage — no evidence either way). Dump water gates
    ///     use false here as a veto, because the flags byte and fWaterHeight are routinely stale
    ///     on captured cells.
    /// </summary>
    public bool? AutoWaterLoaded { get; init; }

    /// <summary>
    ///     Per-cell water type FormID (XCWT subrecord). When absent or unresolved, exterior cells
    ///     inherit the parent worldspace's NAM2 default water type.
    /// </summary>
    public uint? WaterFormId { get; init; }

    /// <summary>Encounter zone FormID (XEZN subrecord).</summary>
    public uint? EncounterZoneFormId { get; init; }

    /// <summary>Music type FormID (XCMO subrecord).</summary>
    public uint? MusicTypeFormId { get; init; }

    /// <summary>Acoustic space FormID (XCAS subrecord).</summary>
    public uint? AcousticSpaceFormId { get; init; }

    /// <summary>Image space FormID (XCIM subrecord).</summary>
    public uint? ImageSpaceFormId { get; init; }

    /// <summary>
    ///     Classic-family per-cell climate override (XCCM subrecord in Oblivion/FO3/FNV). Skyrim and
    ///     later reuse XCCM for a REGN sky/weather source, so their values are not stored here.
    /// </summary>
    public uint? ClimateFormId { get; init; }

    /// <summary>Lighting template FormID (LTMP subrecord / pLightingTemplate pointer).</summary>
    public uint? LightingTemplateFormId { get; init; }

    /// <summary>Lighting template inheritance flags (LTMP data / iLightingTemplateInheritanceFlags).</summary>
    public uint? LightingTemplateInheritanceFlags { get; init; }

    /// <summary>Direct cell lighting fields from XCLL. Used when a new DMP-only cell has no master CELL to inherit.</summary>
    public IReadOnlyDictionary<string, object?>? LightingData { get; init; }

    /// <summary>
    ///     CELL XCLR candidate-region FormIDs. The historical property name predates broader
    ///     REGN support; entries can supply weather, sounds, objects, grass, or radiation and do
    ///     not prove that the entire cell lies inside the region polygon.
    /// </summary>
    public IReadOnlyList<uint> RadiationRegionFormIds { get; init; } = [];

    /// <summary>
    ///     Nonbreaking semantic alias for <see cref="RadiationRegionFormIds" />. Consumers must
    ///     still test the camera/object position against the referenced REGN RPLI/RPLD polygons.
    /// </summary>
    public IReadOnlyList<uint> RegionFormIds => RadiationRegionFormIds;

    /// <summary>Placed objects in this cell (REFR, ACHR, ACRE records).</summary>
    public List<PlacedReference> PlacedObjects { get; init; } = [];

    /// <summary>FormIDs of cells reachable via doors in this cell.</summary>
    public List<uint> LinkedCellFormIds { get; init; } = [];

    /// <summary>
    ///     Associated LAND record heightmap (if found). Settable so the BTD terrain injector can
    ///     attach decoded heights post-parse — Fallout 76 stores heights in external .btd files rather
    ///     than in-record VHGT, and Starfield has no LAND record at all. See
    ///     <see cref="BethesdaMultitool.Core.Formats.Esm.Land.BtdTerrainInjector" />.
    /// </summary>
    public LandHeightmap? Heightmap { get; set; }

    /// <summary>
    ///     VHGT parsed directly from an authored/captured LAND record before runtime terrain
    ///     enrichment replaces <see cref="Heightmap" /> with an ExactHeights mesh projection.
    ///     Conversion planning uses this provenance-preserving copy before considering a
    ///     runtime mesh fallback.
    /// </summary>
    public LandHeightmap? CapturedLandHeightmap { get; set; }

    /// <summary>Associated LAND visual subrecords (VCLR/VTEX/BTXT/ATXT/VTXT), if found.</summary>
    // Settable (like Heightmap) so the Fallout 76 terrain injector can attach BTD-derived land-texture
    // data to an already-parsed exterior cell (FO76 keeps terrain in an external .btd, not the CELL).
    public LandVisualData? LandVisualData { get; set; }

    /// <summary>Runtime terrain mesh extracted from LoadedLandData heap pointers (if available).</summary>
    // Settable like its three terrain siblings above, so AttachTerrainData can assign in place
    // instead of cloning every gridded cell (and forking the aliased worldspace cell lists).
    public RuntimeTerrainMesh? RuntimeTerrainMesh { get; set; }

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
