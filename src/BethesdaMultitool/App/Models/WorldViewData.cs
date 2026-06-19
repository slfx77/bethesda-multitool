using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
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
    ///     SpeedTree archive path (<c>trees\&lt;name&gt;.spt</c>) → recorded tree height (TREE OBND
    ///     Z-extent). Lets the procedural <c>.spt</c> geometry generator size each tree from the ESM
    ///     data instead of a constant. Case-insensitive keys.
    /// </summary>
    public Dictionary<string, float> SpeedTreeHeights { get; init; } = [];

    /// <summary>
    ///     SpeedTree archive path (<c>trees\&lt;name&gt;.spt</c>) → the leaf-atlas texture the engine
    ///     applies, taken from the TREE record's <c>ICON</c> field (NOT the `.spt`'s dev-era material,
    ///     which often never shipped). Case-insensitive keys.
    /// </summary>
    public Dictionary<string, string> SpeedTreeLeafTextures { get; init; } = [];

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
}
