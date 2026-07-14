using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

/// <summary>
///     Pre-computed data for the World tab, built once from RecordCollection.
/// </summary>
internal sealed class WorldViewData
{
    public WorldRenderCache RenderCache { get; } = new();

    public required List<WorldspaceRecord> Worldspaces { get; init; }
    public required List<CellRecord> InteriorCells { get; init; }
    public required Dictionary<uint, ObjectBounds> BoundsIndex { get; init; }

    /// <summary>
    ///     Base-object FormID → NIF/SPT model path (from each base record's MODL subrecord). Lets the
    ///     selected-object pane show a placed reference's model path even when the reference itself wasn't
    ///     enriched with a <see cref="PlacedReference.ModelPath" />. Not <c>required</c> so save-overlay
    ///     builders (which have no record collection) can omit it. Empty when no records were available.
    /// </summary>
    public IReadOnlyDictionary<uint, string> ModelPathIndex { get; init; } = new Dictionary<uint, string>();

    /// <summary>
    ///     Base-object FormID → its resolved <c>MODS</c> ("Alternate Textures") re-skin, or absent when
    ///     the base has none. Drives per-placement texture swapping in the 3D viewer: the render cache
    ///     keys a placement's mesh on <c>ModelPath + VariantKey</c> so, e.g., each billboard shows its
    ///     own vendor ad instead of one shared default. Empty for save-overlay builders and games
    ///     without alternate textures.
    /// </summary>
    public IReadOnlyDictionary<uint, AlternateTextureSet> AlternateTexturesByFormId { get; init; } =
        new Dictionary<uint, AlternateTextureSet>();

    /// <summary>
    ///     MSWP FormID → its normalized material-swap table (original <c>.bgsm</c> path → replacement).
    ///     FO4/FO76 per-PLACEMENT re-skins: a placement whose REFR carries <c>XMSP</c> resolves through
    ///     here at bake time and gets its own mesh-cache variant with the swapped materials' render
    ///     state applied. Empty for earlier games and save-overlay builders.
    /// </summary>
    public IReadOnlyDictionary<uint, IReadOnlyDictionary<string, string>> MaterialSwapsByFormId { get; init; } =
        new Dictionary<uint, IReadOnlyDictionary<string, string>>();

    /// <summary>
    ///     Base-object FormID → its default MSWP FormID (the FO4-family <c>MODS</c> subrecord — a
    ///     bare Material Swap FormID there). The per-BASE fallback wherever a placement carries no
    ///     REFR <c>XMSP</c> of its own; XMSP always wins. Empty for earlier games.
    /// </summary>
    public IReadOnlyDictionary<uint, uint> BaseMaterialSwapsByFormId { get; init; } =
        new Dictionary<uint, uint>();

    /// <summary>
    ///     Base-object FormID → its <c>MODC</c> "Color Remapping Index" (0–1, FO4-family). Overrides
    ///     a grayscale-to-palette material's <c>GradientMapV</c> row at decode time — the engine's
    ///     shared-mesh colorway mechanism (shipping crates). Empty for earlier games.
    /// </summary>
    public IReadOnlyDictionary<uint, float> BaseColorRemapsByFormId { get; init; } =
        new Dictionary<uint, float>();

    /// <summary>
    ///     SpeedTree archive path (<c>trees\&lt;name&gt;.spt</c>) → the leaf-atlas texture the engine
    ///     applies, taken from the TREE record's <c>ICON</c> field (NOT the `.spt`'s dev-era material,
    ///     which often never shipped). Case-insensitive keys.
    /// </summary>
    public Dictionary<string, string> SpeedTreeLeafTextures { get; init; } = [];

    /// <summary>
    ///     SpeedTree archive path (<c>trees\&lt;name&gt;.spt</c>) → the TREE record's CNAM dimming pair
    ///     (LeafDimmingValue / BranchDimmingValue), the engine's canopy-depth darkening inputs applied
    ///     per tree via <c>CSpeedTreeRT::Set{Leaf,Branch}DimmingScalar</c>. Case-insensitive keys.
    /// </summary>
    public Dictionary<string, BethesdaMultitool.Core.Formats.SpeedTree.SpeedTreeDimming> SpeedTreeDimming { get; init; } = [];

    public required Dictionary<uint, PlacedObjectCategory> CategoryIndex { get; init; }
    public required FormIdResolver Resolver { get; init; }
    public required List<PlacedReference> MapMarkers { get; init; }

    /// <summary>
    ///     World units per exterior-cell edge for this worldspace: 4096 for Fallout/Oblivion/Skyrim,
    ///     8192 for Morrowind. The spatial index, 3D camera/picking/render-distance, the 2D map's
    ///     canvas mapping, and the top-down overlay all key off this single value so 2D and 3D share
    ///     one coordinate system (otherwise Morrowind's 8192-unit geometry mis-aligns with a 4096-based
    ///     camera/index). Defaults to the Fallout-family <see cref="WorldGridConstants.CellSize" />.
    /// </summary>
    public float CellWorldSize { get; init; } = WorldGridConstants.CellSize;

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

    /// <summary>Map markers not assigned to any worldspace (common in DMP files).</summary>
    public required List<PlacedReference> UnlinkedMapMarkers { get; init; }

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

    /// <summary>
    ///     Which Bethesda game this data was loaded from (detected from the source file's structural
    ///     plugin format + master list). Drives game-specific rendering choices — currently the 3D
    ///     viewer's sky textures (FNV vs Skyrim cloud/star/sun/moon sets) and moon count (Skyrim has two).
    /// </summary>
    public BethesdaGame Game { get; init; } = BethesdaGame.Unknown;

    /// <summary>
    ///     Engine-exact apparent moon sizes for this game, read from its GMSTs at load:
    ///     <c>iMasserSize</c> / <c>iSecundaSize</c> (the ±size billboard quad half-extent) divided by
    ///     <c>fSunXExtreme</c> (the sky-dome horizontal radius), expressed as a fraction of the billboard
    ///     radius. Null when the GMSTs are absent (Morrowind TES3, save/DMP with no settings table), in
    ///     which case the 3D viewer falls back to the per-game <c>SkyMoonProfile</c> default. Mod-aware:
    ///     an ESM that overrides these GMSTs changes the rendered moon size automatically.
    /// </summary>
    public float? MoonPrimaryHalfSizeFraction { get; init; }

    /// <summary>Engine-exact Secunda size (see <see cref="MoonPrimaryHalfSizeFraction" />); only used by
    /// two-moon games.</summary>
    public float? MoonSecondaryHalfSizeFraction { get; init; }

    /// <summary>Spawn resolution index for leveled list and AI package lookups.</summary>
    public SpawnResolutionIndex? SpawnIndex { get; init; }

    /// <summary>GECK-style reverse usage index for scripts, lists, containers, and packages.</summary>
    public FormUsageIndex? UsageIndex { get; init; }

    /// <summary>Reference FormID → world position for drawing AI package overlays.</summary>
    public Dictionary<uint, (float X, float Y)>? RefPositionIndex { get; init; }

    /// <summary>Additional placed references from save file overlay (changed form positions).</summary>
    public List<PlacedReference>? SaveOverlayMarkers { get; set; }

    /// <summary>Player position from save file, if available.</summary>
    public (float X, float Y, float Z)? PlayerPosition { get; set; }

    /// <summary>
    ///     Heuristically-attributed dangling-REFR clusters loaded from the
    ///     <c>dangling_refs</c> section of <c>cell_worldspace_authority.json</c>.
    ///     Always non-null; <see cref="DanglingRefAttributions.Grid" /> is empty when
    ///     the authority JSON is missing or lacks the section.
    /// </summary>
    public DanglingRefAttributions DanglingRefs { get; init; } = new();

    /// <summary>
    ///     NAVM records keyed by parent CellFormId. Used by the Nav Mesh layer overlay.
    ///     A cell can have multiple NAVMs (e.g. small auxiliary patches alongside the main
    ///     navmesh), so values are lists. Empty when the source had no navmeshes
    ///     (e.g. save files, DMP captures lacking NAVM).
    /// </summary>
    public Dictionary<uint, List<NavMeshRecord>> NavMeshesByCell { get; init; } = new();

    /// <summary>Landscape texture records keyed by FormID. Used by the rendered-terrain layer.</summary>
    public IReadOnlyDictionary<uint, LandscapeTextureRecord> LandTexturesByFormId { get; init; } =
        new Dictionary<uint, LandscapeTextureRecord>();

    /// <summary>GRAS records keyed by FormID. LTEX GNAM entries resolve through this for terrain scatter.</summary>
    public IReadOnlyDictionary<uint, GrassRecord> GrassesByFormId { get; init; } =
        new Dictionary<uint, GrassRecord>();

    /// <summary>Texture set records keyed by FormID. Used by the rendered-terrain layer.</summary>
    public IReadOnlyDictionary<uint, TextureSetRecord> TextureSetsByFormId { get; init; } =
        new Dictionary<uint, TextureSetRecord>();

    /// <summary>
    ///     Water (WATR) records keyed by FormID. Used by the 2D map's water overlay so each
    ///     worldspace can sample its own NNAM noise tile (Potomac muddy brown vs Lake Mead
    ///     clean blue, etc.) instead of a hardcoded blue tint.
    /// </summary>
    public IReadOnlyDictionary<uint, WaterRecord> WatersByFormId { get; init; } =
        new Dictionary<uint, WaterRecord>();

    /// <summary>
    ///     Weather (WTHR) records keyed by FormID. Used by the 3D viewer's atmosphere layer to resolve
    ///     a worldspace climate's weather candidates and to drive the time-of-day/weather color blend.
    /// </summary>
    public IReadOnlyDictionary<uint, WeatherRecord> WeathersByFormId { get; init; } =
        new Dictionary<uint, WeatherRecord>();

    /// <summary>Climate (CLMT) records keyed by FormID. A worldspace's <c>ClimateFormId</c> (CNAM)
    /// resolves through here to its sunrise/sunset timing and default weather list.</summary>
    public IReadOnlyDictionary<uint, ClimateRecord> ClimatesByFormId { get; init; } =
        new Dictionary<uint, ClimateRecord>();

    /// <summary>
    ///     Image Space (IMGS) records keyed by FormID. The active cell's XCIM (interiors) or the
    ///     worldspace's INAM resolves through here to the engine HDR + cinematic parameters the
    ///     viewer's tonemap stage reproduces (FO3/FNV; defaults 0x160/0x161 when unset).
    /// </summary>
    public IReadOnlyDictionary<uint, ImageSpaceRecord> ImageSpacesByFormId { get; init; } =
        new Dictionary<uint, ImageSpaceRecord>();

    /// <summary>
    ///     Lighting Template (LGTM) records keyed by FormID. An interior cell's LTMP FormID resolves
    ///     through here so <c>AtmosphereState.ResolveInterior</c> can fall back to the template's
    ///     ambient/directional/fog when the cell's XCLL is absent or inherit-flagged.
    /// </summary>
    public IReadOnlyDictionary<uint, LightingTemplateRecord> LightingTemplatesByFormId { get; init; } =
        new Dictionary<uint, LightingTemplateRecord>();

    /// <summary>
    ///     Light (LIGH) base records keyed by FormID. The render cache resolves each placed REFR's
    ///     <c>BaseFormId</c> through this index so meshless lights become emitter-only entries, while
    ///     model-bearing lights contribute both their normal mesh and a local-light emitter.
    /// </summary>
    public IReadOnlyDictionary<uint, LightRecord> LightsByFormId { get; init; } =
        new Dictionary<uint, LightRecord>();

    /// <summary>
    ///     Placed-reference FormIDs whose XESP enable-parent CHAIN resolves to disabled in the initial
    ///     world state (parent initially-disabled, or enabled with the opposite-state flag). The
    ///     placement bake ORs this with each ref's own initially-disabled flag — the mechanism that
    ///     keeps condition-gated effect meshes (e.g. IndFXLightRays01) from always drawing. Refs whose
    ///     own flag is set are filtered regardless and need not appear here.
    /// </summary>
    public IReadOnlyCollection<uint> XespDisabledRefs { get; init; } = new HashSet<uint>();

    /// <summary>All weather records, sorted by EditorId — populates the 3D viewer's weather dropdown
    /// (the user can preview any weather, not just the current climate's candidates).</summary>
    public IReadOnlyList<WeatherRecord> AllWeathers { get; init; } = [];

    /// <summary>
    ///     Additional data-file paths (ESM/ESP/DMP) from the active Load Order. When a DMP file
    ///     is loaded as <see cref="SourceFilePath" />, it has no adjacent BSAs of its own;
    ///     <c>WorldView3DControl</c> falls back to these paths to discover texture BSAs
    ///     in their parent Data folders. Settable post-construction so
    ///     <c>WorldMapOverlayBuilder</c> can stay agnostic of session/load-order state.
    /// </summary>
    public IReadOnlyList<string> AdditionalDataPaths { get; set; } = [];

    /// <summary>
    ///     True when this view was built from a memory dump (DMP). The 3D viewer enables the
    ///     renamed-asset fuzzy mesh fallback (and loose-file overrides) only for dumps, where
    ///     prototype mesh paths were renamed before the shipped archives; ESM/ESP/save views keep
    ///     exact-only resolution. Settable post-construction (like <see cref="AdditionalDataPaths" />).
    /// </summary>
    public bool IsMemoryDump { get; set; }
}

