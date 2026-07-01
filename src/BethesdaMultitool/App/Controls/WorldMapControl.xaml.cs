using System.Collections.Concurrent;
using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>Win2D-based 2D world map: renders terrain layers, placed-object markers, and overlays with zoom/pan navigation.</summary>
public sealed partial class WorldMapControl : UserControl, IDisposable
{
    private const int ExportLongEdge = 4096;

    // Cached (CA1869): the tile-manifest export reuses one options instance instead of allocating per write.
    private static readonly System.Text.Json.JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    // --- Legend / Browser ---
    private readonly HashSet<PlacedObjectCategory> _hiddenCategories = [];
    private readonly WorldMapStateController _state = new();

    // --- Cell browser ---
    // The browser list (items, search, filters, sort) lives inside the shared CellListControl (CellList).
    private byte[]? _cachedGrayscale;
    private int _cachedHmWidth, _cachedHmHeight;
    private byte[]? _cachedWaterMask;

    // --- Cell grid lookup ---
    private Dictionary<(int x, int y), CellRecord>? _cellGridLookup;
    private CanvasBitmap? _cellHeightmapBitmap;
    // Standalone water layer for the single-cell detail view (built water-free + this). Drawn over the
    // cell bitmap; suppressed when the rendered-models overlay is active. Toggling water is a redraw.
    private CanvasBitmap? _cellWaterBitmap;
    private WorldSpatialIndex? _spatialIndex;

    // --- Heightmap tinting ---
    private HeightmapColorScheme _currentColorScheme = HeightmapColorScheme.Amber;

    // --- Layer selection ---
    private WorldMapLayer _currentLayer = WorldMapLayer.Heightmap;
    private string? _lastTerrainDrawLog; // change-detection for the gated TerrainTextures draw-decision trace

    // --- Terrain-textures shading (independent VCLR + hillshade modulation of the textured layer) ---
    private bool _shadeVertexColors = true; // default ON (engine-accurate tint, preserves prior look)
    private bool _shadeHillshade;           // default OFF

    // --- Overlay toggles ---
    private bool _showNavMesh;

    // --- Cursor ---
    private InputSystemCursorShape _currentCursorShape = InputSystemCursorShape.Arrow;
    private float? _currentDefaultWaterHeight;
    private WaterColorPalette? _currentWaterPalette;
    private WorldViewData? _data;

    // Active worldspace's cell-edge size in world units (4096 Fallout-family, 8192 Morrowind), set from
    // WorldViewData.CellWorldSize on load. Keeps the 2D map's cell↔canvas↔screen mapping in the same
    // coordinate system as the 3D viewer + the top-down "Rendered models" overlay.
    private float _cellSize = WorldGridConstants.CellSize;

    // --- Markers ---
    private bool _hideDisabledActors = true;

    // --- Dangling REFR overlay ---
    private DanglingRefThreshold _danglingThreshold = DanglingRefThreshold.None;

    // --- Hover / Selection ---
    private PlacedReference? _hoveredObject;
    private bool _isPanning;
    private bool _legendExpanded = true;

    // --- Marker icons (per game) ---
    // The active game's marker icon set (embedded tinted for FO3/FNV, atlas for others, glyph-only when
    // no art is wired). Keyed by raw TNAM value via MapMarkerCatalog; rebuilt on a game change.
    private IMapMarkerIconSet? _markerIconSet;
    private Core.Games.BethesdaGame _markerIconSetGame = Core.Games.BethesdaGame.Unknown;

    // Pre-tinted marker icons (embedded set only), rebuilt only when the color scheme changes. The
    // overview used to build a ColorMatrixEffect PER MARKER PER FRAME (DrawTintedIcon) — hundreds of
    // Direct2D effect graphs every frame at zoomed-out overview where every worldspace marker is in view —
    // which tanked frame time (hiding markers was a huge speedup). Tinting each icon once and blitting the
    // cached bitmap collapses that to a handful of one-time tint passes. Keyed by the scheme's packed ARGB.
    private Dictionary<int, CanvasBitmap>? _tintedMarkerIconBitmaps;
    private uint _tintedMarkerColorArgb;

    // --- State ---
    private Vector2 _panOffset;

    /// <summary>
    ///     Smoothed pan-direction vector in screen-space units (EMA of frame-to-frame pan
    ///     delta). Used by <see cref="BuildTerrainTextureViewportRequest" /> to bias the
    ///     viewport margin asymmetrically — cells in the direction of motion get preloaded
    ///     so they're already cached when the user pans into them. Cesium's "Preload" tier.
    /// </summary>
    private Vector2 _panVelocity;
    private Vector2 _panOffsetAtStart;
    private Vector2 _panStartScreen;

    // --- WASD keyboard panning ---
    // Held W/A/S/D keys; the viewport timer integrates a steady screen-space nudge each tick while
    // any are down (and is kept alive until they're released). Screen-space px/tick so the pan rate
    // is steady on screen regardless of zoom (matches the pointer-drag pan, which also moves _panOffset
    // in screen pixels).
    private readonly HashSet<Windows.System.VirtualKey> _panKeysDown = new();
    private const float PanPixelsPerTick = 22f;

    // --- Click detection ---
    private Vector2 _pointerDownScreen;
    private bool _pointerWasDragged;
    private bool _showWater = true;
    private bool _showCellGrid = true;
    private bool _suppressNavEvents;
    // True only during InitializeComponent — the filter toggles now live in toolbar flyouts that set
    // IsChecked in XAML, firing their handlers during load before LegendPanel/MapCanvas exist. The
    // handlers no-op until the constructor clears this; backing fields default to the checked values.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
        "S3604:Member initializer values should not be redundant",
        Justification = "InitializeComponent() fires the toggle handlers during construction; they read " +
                        "_initializing before the constructor sets it false, so the initializer is load-bearing.")]
    private bool _initializing = true;

    // --- Heightmap bitmaps ---
    private CanvasBitmap? _worldHeightmapBitmap;
    private bool _worldHeightmapDirty = true;
    private int _worldHeightmapBuildVersion;
    private int _worldHmMinX, _worldHmMaxY;
    private int _worldHmPixelWidth, _worldHmPixelHeight;

    // --- TerrainTextures aggregate LOD ---
    // When zoomed out far enough that per-cell streaming would request the whole worldspace at once
    // (the 13k-cell burst), show ONE downscaled whole-worldspace texture bitmap instead. Switches back
    // to per-cell high-res streaming when zoomed in. The aggregate is built once per worldspace,
    // cached, and composited via the single-bitmap path (it's 33 px/cell like the heightmap aggregate).
    private bool _terrainTexturesAggregateActive;
    private CanvasBitmap? _terrainAggregateBitmap;
    private int _terrainAggMinX, _terrainAggMaxY, _terrainAggPixelWidth, _terrainAggPixelHeight;
    private int _terrainAggPixelsPerCell = WorldMapLayerRenderer.HeightmapPixelsPerCell;
    private uint? _terrainAggregateWorldspaceFormId;
    private bool _terrainAggregateUnavailable; // worldspace too large for one bitmap → keep per-cell
    private int _terrainAggregateVersion;
    private bool _terrainAggregateBuilding;

    // --- Standalone world water layer (zoomed-out / secondary layers) ---
    // One premult-alpha water bitmap (33 px/cell, transparent where dry), built once per worldspace from
    // the shared heightmap water mask. Drawn over WHATEVER non-per-cell terrain layer is active
    // (heightmap/vertex/slope/regions/terrain-aggregate) so those render water-free and the water toggle
    // is a draw-time redraw — the zoomed-out counterpart of the per-cell _layerWaterCellBitmaps. Suppressed
    // when the rendered-models overlay is active (that path supplies height-correct water).
    private CanvasBitmap? _worldWaterBitmap;
    private int _worldWaterMinX, _worldWaterMaxY, _worldWaterPixelWidth, _worldWaterPixelHeight;
    private uint? _worldWaterWorldspaceFormId;
    private bool _worldWaterBuilding;
    private int _worldWaterVersion;

    /// <summary>Screen px/cell below which TerrainTextures uses the aggregate LOD (a cell smaller than
    /// this can't show full per-cell texture detail anyway, and per-cell streaming would flood).</summary>
    private const float TerrainAggregateScreenPxThreshold = 24f;

    /// <summary>
    ///     Cancellation source for the in-flight TerrainTextures streaming build. Replaced
    ///     and canceled on every viewport change so abandoned cells don't keep decoding on
    ///     background workers — mirrors Mapbox GL Native's "abandon outdated tile work" policy.
    /// </summary>
    private CancellationTokenSource? _terrainStreamCts;

    /// <summary>
    ///     Completed terrain-texture cells waiting to become device-bound
    ///     <see cref="CanvasBitmap" />s on the UI thread. Background stream workers enqueue here
    ///     (cheap, lock-free); the throttle timer drains at most
    ///     <see cref="MaxCellAppliesPerTick" /> per tick. This caps the per-frame GPU-upload cost
    ///     so a burst of thousands of completed cells (zoomed-out pan) can't starve pointer input
    ///     — the prior design enqueued one <c>DispatcherQueue.TryEnqueue</c> per cell with no
    ///     ceiling. Each item carries the cache generation captured when its stream started; stale
    ///     items are dropped by the gen check in <see cref="ApplyOneTerrainTextureCell" />.
    /// </summary>
    private readonly ConcurrentQueue<(int cacheGen, WorldMapLayerRenderer.TerrainTextureCellResult cell)>
        _pendingCellApplies = new();

    /// <summary>
    ///     Keys (gx, gy, pixelsPerCell) that are currently queued in <see cref="_pendingCellApplies" />
    ///     or being decoded by an in-flight stream. A rebuild excludes these from its build set so
    ///     overlapping rebuilds during a pan don't re-request the same cells over and over (the cause
    ///     of the "constant refresh" churn: 49k re-decodes/uploads for a 16k-cell viewport). Written
    ///     by background workers on enqueue, removed on the UI thread at drain — hence concurrent.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int gx, int gy, int pixelsPerCell), byte>
        _inFlightCellKeys = new();

    /// <summary>Max cell bitmaps uploaded per timer tick. ~48 × 30 ticks/s ≈ 1440 uploads/s.</summary>
    private const int MaxCellAppliesPerTick = 48;

    /// <summary>
    ///     Byte ceiling on GPU uploads per drain tick. The count cap alone assumes uniformly-small cells;
    ///     at the 1056 px/cell tier a cell is ~4.5 MB, so 48 of them would dump &gt;200 MB of synchronous
    ///     GPU upload in one UI tick and freeze the app on a zoom transition. Stopping a tick at this byte
    ///     budget bounds the per-tick upload time regardless of tier (≈5 big cells or 48 small ones).
    /// </summary>
    private const long MaxCellApplyBytesPerTick = 24L * 1024 * 1024;

    /// <summary>
    ///     Soft GPU-memory ceiling for the per-cell bitmap cache. The LRU cap is a COUNT; at 4.5 MB per
    ///     1056 px/cell tile a 256-entry cap would retain &gt;1 GB of GPU textures while panning zoomed in,
    ///     risking device removal. The effective count cap is clamped so count × current-tier-cell-bytes
    ///     stays under this (e.g. ~85 cells at 1056, the full count at the small tiers).
    /// </summary>
    private const long MaxLayerCellResidentBytes = 384L * 1024 * 1024;

    /// <summary>
    ///     In-flight TerrainTextures streams (Interlocked). Keeps the throttle timer ticking while
    ///     workers are still producing cells, even when <see cref="_pendingCellApplies" /> momentarily
    ///     drains empty between producer batches — otherwise the timer's idle self-stop could fire
    ///     mid-stream and later-produced cells would never be drained.
    /// </summary>
    private int _activeTerrainStreams;

    /// <summary>
    ///     Coalesces the expensive TerrainTextures viewport rebuild (spatial query + cache diff +
    ///     sort + stream dispatch) off the per-frame draw callback. The draw path only does a cheap
    ///     viewport-key compare and flags <see cref="_viewportRebuildPending" />; this timer runs the
    ///     heavy work at most ~30×/s and drains <see cref="_pendingCellApplies" />. Lazily created,
    ///     self-stops when idle. Only used for the live TerrainTextures overview layer.
    /// </summary>
    private DispatcherTimer? _viewportRebuildTimer;

    private bool _viewportRebuildPending;

    /// <summary>~2 frames @60fps: low enough that cells still pop in during a continuous pan, high
    ///     enough that a fast pan crossing many cell boundaries doesn't rebuild 60×/s.</summary>
    private const int ViewportRebuildThrottleMs = 33;

    /// <summary>
    ///     Countdown that suppresses the viewport rebuild while a zoom gesture is in progress. Each
    ///     wheel notch re-arms it; the rebuild timer decrements it per tick and only re-streams once it
    ///     hits zero. During the gesture the draw callback composites the existing bitmaps at the live
    ///     transform (Win2D upscales), so the zoom stays smooth and just sharpens on settle — instead
    ///     of re-streaming the whole viewport at every intermediate zoom level / pixels-per-cell tier.
    ///     Pan does not arm this (only <see cref="MapCanvas_PointerWheelChanged" />), so pan keeps its
    ///     progressive pop-in.
    /// </summary>
    private int _zoomSettleTicks;

    private const int ZoomSettleTicks = 4; // ~132 ms at the 33 ms timer cadence

    /// <summary>
    ///     Per-cell GPU bitmaps for overview layers that would exceed D3D's max texture size
    ///     as one large bitmap. Composited cell-by-cell in <see cref="WorldMapOverviewRenderer" />.
    ///     Key is <c>(gridX, gridY, pixelsPerCell)</c> so multiple resolutions of the same cell
    ///     can coexist — when the user zooms across a resolution threshold the previous tier
    ///     remains as a visual stand-in (Cesium's "Ancestor Meets SSE" pattern) until the
    ///     target-resolution bitmap finishes building.
    ///     <para>
    ///         <see cref="OrderedDictionary{TKey,TValue}" /> (not <c>Dictionary</c>) because LRU
    ///         eviction depends on iteration-order = insertion-order. <c>Dictionary</c>'s
    ///         internal free-list reuses removed slots in LIFO order, so after a mass-eviction
    ///         (e.g. zoom-in shrinks cap from 18k → 256) every new add lands in a low-index
    ///         slot and is then re-evicted by the next <c>Keys.First()</c> call — the cells
    ///         appear in the cache for one frame, then vanish. The 2026-06-05 profiler trace
    ///         captured this as add → evict for the same key, repeatedly. Switching to
    ///         <c>OrderedDictionary</c> gives strict insertion order: <see cref="OrderedDictionary{TKey,TValue}.GetAt(int)" />
    ///         at index 0 is genuinely the oldest entry.
    ///     </para>
    /// </summary>
    private OrderedDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? _layerCellBitmaps;

    /// <summary>
    ///     Standalone water tiles for the TerrainTextures overview, co-streamed with
    ///     <see cref="_layerCellBitmaps" /> and kept in lockstep with it (same keys; a subset — only
    ///     wet cells have an entry). Drawn AFTER the placed-object overlay so models are occluded by
    ///     water (2D-5), and as its own layer so toggling water is a pure redraw with no re-stream
    ///     (2D-6). Every add/replace/evict/clear on <see cref="_layerCellBitmaps" /> mirrors here.
    /// </summary>
    private OrderedDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? _layerWaterCellBitmaps;

    /// <summary>
    ///     Coarse multi-cell tiles for an oversized-worldspace overview (FO76 APPALACHIA). Built in one
    ///     pass (not streamed) for the terrain-derived layers above the single-bitmap size ceiling: a
    ///     bounded set of mid-resolution bitmaps each covering <see cref="_coarseTileCellSpan" />² cells,
    ///     instead of one multi-GB bitmap (freeze) or thousands of per-cell tiles. Mutually exclusive
    ///     with <see cref="_worldHeightmapBitmap" /> + <see cref="_layerCellBitmaps" />.
    /// </summary>
    private Dictionary<(int tileGx, int tileGy), CanvasBitmap>? _coarseTileBitmaps;

    /// <summary>Cells per edge covered by each entry in <see cref="_coarseTileBitmaps" />.</summary>
    private int _coarseTileCellSpan;

    /// <summary>Px/cell each coarse tile was rendered at (drives the draw interpolation choice).</summary>
    private int _coarseTilePixelsPerCell;

    /// <summary>
    ///     The layer <see cref="_coarseTileBitmaps" /> was built for. Coarse tiles are a heightmap-family
    ///     representation (oversized worldspaces); a layer switch keeps them around for a smooth
    ///     transition (<see cref="InvalidateWorldBitmap" /> with keepCurrentBitmap), so the draw must skip
    ///     them when the current layer differs — otherwise the stale heightmap coarse tiles win the
    ///     exclusive background draw and hide the TerrainTextures tiles. Mirrors <c>_layerCellBitmapsLayer</c>.
    /// </summary>
    private WorldMapLayer? _coarseTileBitmapsLayer;

    /// <summary>Which layer the cached cell bitmaps belong to. Used to detect layer switches.</summary>
    private WorldMapLayer? _layerCellBitmapsLayer;

    /// <summary>
    ///     Bumped only when <see cref="_layerCellBitmaps" /> is actually replaced (set to null,
    ///     reinitialized in a non-incremental rebuild, or its <see cref="_layerCellBitmapsLayer" />
    ///     identity changes). Used by <see cref="ApplyOneTerrainTextureCell" /> as the drop check
    ///     instead of <see cref="_worldHeightmapBuildVersion" />. The per-stream version is too
    ///     aggressive: during a fast pan, every viewport rebuild bumps it, and tile applies
    ///     enqueued by an older stream get dropped before they can land in the dict — the user
    ///     sees produced cells never appear on screen. Cache-generation only changes when the
    ///     APPLY would actually corrupt the cache (different layer / cleared dict / etc.), so
    ///     late applies within the same generation are accepted.
    /// </summary>
    private int _layerCellBitmapsCacheGen;

    /// <summary>
    ///     Soft cap on total entries (cell × resolution). Bounds memory across long pan sessions
    ///     where the user crosses multiple zoom thresholds. The cap below is a floor —
    ///     <see cref="_layerCellBitmapCap" /> grows per-request to fit the current viewport
    ///     (zoomed-out WastelandNV is ~6000 visible cells), otherwise streamed cells would be
    ///     evicted by later-streamed cells in the SAME stream before the user sees them.
    ///     LRU within the cap is implicit via viewport-driven evict in
    ///     <see cref="EnsureHeightmapBitmap" />; this cap is the safety net for resolutions
    ///     retained across zoom thresholds and bounds long-session memory growth.
    /// </summary>
    private const int MinLayerCellBitmapCap = 256;

    /// <summary>
    ///     Effective cap on <see cref="_layerCellBitmaps" />. Scales with the size of the
    ///     current viewport request so cells streamed early in a large fan-out aren't evicted
    ///     by cells streamed later (the user's "view doesn't fill" symptom). Reset each
    ///     viewport rebuild in <see cref="EnsureHeightmapBitmap" />.
    /// </summary>
    private int _layerCellBitmapCap = MinLayerCellBitmapCap;

    /// <summary>
    ///     Hysteresis for <see cref="_layerCellBitmapCap" />: grows instantly with the viewport,
    ///     but holds the high-water cap (then decays by halving) when the request transiently
    ///     collapses — e.g. a pan sweeping off the populated cell region — so the pan-back
    ///     history isn't mass-evicted at every sweep boundary.
    /// </summary>
    private readonly LayerCellBitmapCapPolicy _layerCellBitmapCapPolicy = new(minCap: MinLayerCellBitmapCap);

    private TerrainTextureViewportKey? _terrainTextureViewportKey;

    // --- Pan/Zoom ---
    private float _zoom = 0.05f;

    // --- Rendered-models overlay (top-down 3D render composited over the terrain layer) ---
    // The 3D control implements ITopDownSceneRenderer; the host tab wires it in after both controls
    // load the same WorldViewData. When the toggle is on (exterior views only) we request a top-down
    // render of the visible world rect, upload the BGRA readback as a CanvasBitmap, and composite it
    // in place of the static dot/box markers. Requests fire on viewport-settle (not mid pan/zoom) and
    // repeat while the render reports incomplete (terrain/mesh/textures still streaming in).
    private ITopDownSceneRenderer? _topDownProvider;
    private bool _showRenderedObjects;
    private CanvasBitmap? _topDownOverlay;

    /// <summary>Last logged UI-thread-fault signature, so a deterministic per-frame draw exception
    /// logs once instead of flooding the log (see <see cref="LogUiThreadFault" />).</summary>
    private string? _lastUiFaultSignature;
    private float _topDownWorldMinX, _topDownWorldMaxX, _topDownWorldMinY, _topDownWorldMaxY;
    private bool _topDownRequestPending;
    private bool _topDownIncomplete;
    private bool _topDownInFlight;
    private int _topDownGen;
    private CancellationTokenSource? _topDownCts;
    private (ViewMode Mode, uint Cell, uint Ws, bool HideDisabled, bool Eligible)? _topDownContext;
    private (int TlX, int TlY, int BrX, int BrY, int ZoomBucket)? _topDownBoundsKey;

    /// <summary>Minimum interval between top-down overlay render requests. The render is a full
    /// offscreen scene render + GPU readback + CanvasBitmap upload; without this gate the ~30 Hz
    /// <see cref="ViewportRebuildTimer_Tick" /> re-rendered the whole scene every tick while the
    /// scene streamed (<see cref="_topDownIncomplete" />), starving pan/zoom. ~3 requests/sec while
    /// streaming; 0 once complete + static.</summary>
    private const int TopDownMinIntervalMs = 300;

    /// <summary><see cref="Environment.TickCount64" /> of the last issued top-down request. Reset to
    /// 0 on cancel/enable so the first request after enabling the overlay fires immediately.</summary>
    private long _topDownLastRequestTick;

    /// <summary>Fraction of the viewport added as a margin on each side of the top-down render, so a
    /// small pan reveals already-covered area instead of a blank edge before the next refresh.</summary>
    private const float TopDownMarginFraction = 0.25f;

    private readonly bool _dumpTopDown =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Map2D.TopDownDump);
    private bool _topDownDumpWritten;

    /// <summary>
    ///     The 3D control's top-down render provider, set by the host tab after both controls have
    ///     loaded the same data. Setting it re-evaluates whether the "Rendered models" toggle is
    ///     available (the 3D backend + reference pipeline must be up).
    /// </summary>
    internal ITopDownSceneRenderer? TopDownProvider
    {
        get => _topDownProvider;
        set
        {
            _topDownProvider = value;
            UpdateRenderedObjectsAvailability();
        }
    }

    public WorldMapControl()
    {
        InitializeComponent();
        // Hillshade lighting defaults OFF (fixed NW look). Set the shared panel's toggle to match while
        // _initializing is still true, so its Toggled handler early-returns (no spurious rebuild).
        LightingPanel.LightingEnabled = false;
        // Terrain-textures shading checkboxes: VCLR on, hillshade off (handlers early-return while
        // _initializing, so this just syncs the visual state to the backing fields).
        ShadeVertexColorsCheckBox.IsChecked = _shadeVertexColors;
        ShadeHillshadeCheckBox.IsChecked = _shadeHillshade;
        _initializing = false; // XAML load done — filter toggle handlers may now run for real.
        // The cell browser is the shared CellListControl; in 2D selecting a cell inspects it.
        CellList.Activation = CellListControl.ActivationMode.SelectionChanged;
        CellList.CellActivated += (_, cell) => InspectCell?.Invoke(this, cell);
        // Cancel any in-flight TerrainTextures stream when the control unloads (tab switch
        // or app shutdown). Without this, the async loop in StreamAndApplyTerrainTexturesAsync
        // keeps producing cells, tries to enqueue onto a now-null DispatcherQueue, and throws
        // NullReferenceException — visible as "TerrainTextures streaming failed" in the log.
        Unloaded += (_, _) => CancelTerrainStream();
        Unloaded += (_, _) => CancelTopDownOverlay();
    }

    // --- Navigation ---
    internal event Action? BeforeNavigate;

    // --- Events ---
    /// <summary>Raised when the user requests inspection of a placed object (opens its detail panel).</summary>
    public event EventHandler<PlacedReference>? InspectObject;

    /// <summary>Raised when the user requests inspection of a cell.</summary>
    public event EventHandler<CellRecord>? InspectCell;

    internal void LoadData(WorldViewData data)
    {
        // A second LoadData means a new scene. Drop streams, device bitmaps, and any top-down
        // provider from the previous scene before publishing the new data to draw handlers.
        TopDownProvider = null;
        CancelTopDownOverlay();
        DisposeWorldBitmaps();
        DisposeCellDetailBitmaps();
        _spatialIndex = null;
        _cellGridLookup = null;

        _data = data;
        _cellSize = data.CellWorldSize;
        // The top-down "Rendered models" overlay bakes placement lists through this same cache, so
        // seed the category index here too (idempotent with the 3D control's LoadData).
        data.RenderCache.CategoryIndex = data.CategoryIndex;
        // Same for MODS alternate textures — the placement bake is cached per cell, so the index must
        // be present before whichever control (2D or 3D) bakes first, or the shared cache would keep a
        // no-override list.
        data.RenderCache.AlternateTextureIndex = data.AlternateTexturesByFormId;
        _state.LoadData(data);
        _worldHeightmapDirty = true;
        _currentColorScheme = HeightmapColorScheme.DefaultForGame(data.Game, data.SourceFilePath);
        _cachedGrayscale = data.HeightmapGrayscale;
        _cachedWaterMask = data.HeightmapWaterMask;
        _cachedHmWidth = data.HeightmapPixelWidth;
        _cachedHmHeight = data.HeightmapPixelHeight;

        ColorSchemeComboBox.Items.Clear();
        foreach (var scheme in HeightmapColorScheme.Presets)
        {
            ColorSchemeComboBox.Items.Add(scheme.Name);
        }

        var defaultIdx = Array.IndexOf(HeightmapColorScheme.Presets, _currentColorScheme);
        if (defaultIdx >= 0)
        {
            ColorSchemeComboBox.SelectedIndex = defaultIdx;
        }

        LayerComboBox.Items.Clear();
        foreach (var layer in Enum.GetValues<WorldMapLayer>())
        {
            LayerComboBox.Items.Add(layer.DisplayName());
        }
        LayerComboBox.SelectedIndex = (int)_currentLayer;

        WorldspaceComboBox.Items.Clear();
        foreach (var ws in data.Worldspaces)
        {
            var name = WorldMapColors.FormatWorldspaceName(ws);
            WorldspaceComboBox.Items.Add($"{name} — {ws.Cells.Count} cells");
        }

        if (data.UnlinkedExteriorCells.Count > 0)
        {
            WorldspaceComboBox.Items.Add($"Unlinked Exterior ({data.UnlinkedExteriorCells.Count} cells)");
        }

        InteriorsButton.Content = data.InteriorCells.Count > 0
            ? $"Interiors ({data.InteriorCells.Count})"
            : "Interiors";
        InteriorsButton.IsEnabled = data.InteriorCells.Count > 0;
        AllCellsButton.Content = $"All Cells ({data.AllCells.Count})";

        BuildLegendPanel();

        if (data.Worldspaces.Count > 0)
        {
            WorldspaceComboBox.SelectedIndex = 0;
        }

        // Seed dangling-REFR dropdown. Default to None — overlay is opt-in.
        DanglingRefsComboBox.SelectedIndex = (int)_danglingThreshold;
        var hasDangling = data.DanglingRefs.Grid.Count > 0;
        DanglingRefsComboBox.IsEnabled = hasDangling;
        DanglingRefsLabel.Opacity = hasDangling ? 1.0 : 0.5;
    }

    private void LegendToggle_Click(object sender, RoutedEventArgs e)
    {
        _legendExpanded = !_legendExpanded;
        LegendPanel.Visibility = _legendExpanded ? Visibility.Visible : Visibility.Collapsed;
        LegendToggleIcon.Glyph = _legendExpanded ? "" : "";
    }

    private void BuildLegendPanel()
    {
        WorldMapLegendBuilder.Populate(LegendPanel, _hiddenCategories,
            () => MapCanvas.Invalidate(), OnLegendCategoryToggled);
    }

    private void OnLegendCategoryToggled(PlacedObjectCategory category)
    {
        // The rendered-models overlay shares this category filter — drop it so it re-renders with the
        // new set (covers the user report that category toggles didn't affect the 3D meshes).
        InvalidateTopDownOverlay();

        // Keep the toolbar Map markers checkbox in sync with legend clicks.
        if (category != PlacedObjectCategory.MapMarker || MapMarkersCheckBox is null) return;
        var visible = !_hiddenCategories.Contains(PlacedObjectCategory.MapMarker);
        if (MapMarkersCheckBox.IsChecked == visible) return;
        _suppressMarkersCheckboxEvent = true;
        MapMarkersCheckBox.IsChecked = visible;
        _suppressMarkersCheckboxEvent = false;
    }

    private bool _suppressMarkersCheckboxEvent;

    private void MapMarkersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _suppressMarkersCheckboxEvent) return;
        if (MapMarkersCheckBox.IsChecked == true)
        {
            _hiddenCategories.Remove(PlacedObjectCategory.MapMarker);
        }
        else
        {
            _hiddenCategories.Add(PlacedObjectCategory.MapMarker);
        }
        InvalidateTopDownOverlay();
        BuildLegendPanel();
        MapCanvas?.Invalidate();
    }

    private void NavMeshCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showNavMesh = NavMeshCheckBox?.IsChecked == true;
        MapCanvas?.Invalidate();
    }

    private void CellGridCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        // Pure overlay toggle — no bitmap rebuild, just redraw the composite with/without the grid.
        _showCellGrid = CellGridCheckBox?.IsChecked == true;
        MapCanvas?.Invalidate();
    }

    private void SetCanvasMode(bool canvasVisible)
    {
        MapCanvas.Visibility = canvasVisible ? Visibility.Visible : Visibility.Collapsed;
        LegendOverlay.Visibility = canvasVisible ? Visibility.Visible : Visibility.Collapsed;
        FilterCheckboxPanel.Visibility = canvasVisible ? Visibility.Visible : Visibility.Collapsed;
        CellBrowserPanel.Visibility = canvasVisible ? Visibility.Collapsed : Visibility.Visible;
        // Layer-build status floats over the canvas; collapse it when we switch to the cell
        // list so leftover warnings ("X produced no renderable data") don't overlap the list.
        // It will be re-shown by the next build pipeline pass when canvas mode returns.
        if (!canvasVisible) HideLayerBuildStatus();
    }

    private void ClearWorldspaceSelection()
    {
        _suppressNavEvents = true;
        WorldspaceComboBox.SelectedIndex = -1;
        _suppressNavEvents = false;
    }

    private void WaterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showWater = WaterCheckBox.IsChecked == true;
        // Every view now renders terrain water-FREE and draws water as a separate, toggle-able layer —
        // per-cell tiles (zoomed-in TerrainTextures), the shared world water bitmap (aggregate + secondary
        // layers), or the cell-detail water bitmap. So toggling water is a pure REDRAW: no bitmap rebuild,
        // no cache discard, no re-stream on any layer/zoom.
        // The rendered-models overlay bakes height-correct water into its readback, so a water toggle must
        // re-render it; clearing the bounds key makes the next MaybeScheduleTopDownRequest re-request.
        if (_showRenderedObjects)
        {
            _topDownBoundsKey = null;
        }
        MapCanvas.Invalidate();
    }

    /// <summary>
    ///     Rebuilds the cell-detail bitmap PAIR for <paramref name="cell" />: the terrain bitmap rendered
    ///     water-FREE plus the standalone water bitmap. Built once per selected cell; water is a separate
    ///     draw-time layer, so the water toggle is a pure redraw and never rebuilds these.
    /// </summary>
    private void RebuildCellDetailBitmaps(CellRecord cell)
    {
        _cellHeightmapBitmap?.Dispose();
        _cellWaterBitmap?.Dispose();
        _cellHeightmapBitmap = WorldMapCellDetailRenderer.BuildCellHeightmapBitmap(
            MapCanvas, cell, _currentDefaultWaterHeight, _currentColorScheme,
            showWater: false, _currentLayer, _data, _data?.RenderCache,
            CurrentTerrainShading(), CurrentHillshadeLightDir(), CurrentHillshadeZScale());
        _cellWaterBitmap = WorldMapCellDetailRenderer.BuildCellWaterBitmap(
            MapCanvas, cell, _currentDefaultWaterHeight, _currentLayer, _data?.RenderCache);
    }

    private void DisposeCellDetailBitmaps()
    {
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        _cellWaterBitmap?.Dispose();
        _cellWaterBitmap = null;
    }

    private void DisabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _hideDisabledActors = DisabledCheckBox.IsChecked != true;
        MapCanvas.Invalidate();
    }

    private void DanglingRefsComboBox_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        var idx = DanglingRefsComboBox.SelectedIndex;
        if (idx < 0 || idx > 3)
        {
            return;
        }

        _danglingThreshold = (DanglingRefThreshold)idx;
        MapCanvas.Invalidate();
    }

    /// <summary>Selects the given placed object and redraws the map to highlight it.</summary>
    public void SelectObject(PlacedReference? obj)
    {
        _state.SelectObject(obj);
        MapCanvas.Invalidate();
    }

    /// <summary>Clears the loaded world, all layer toggles, and cached bitmaps back to defaults.</summary>
    public void Reset()
    {
        _data = null;
        _spatialIndex = null;
        _cellGridLookup = null;
        _state.Reset();
        _hiddenCategories.Clear();
        _hideDisabledActors = true;
        _showWater = true;
        WaterCheckBox.IsChecked = true;
        DisabledCheckBox.IsChecked = false;
        _suppressMarkersCheckboxEvent = true;
        MapMarkersCheckBox.IsChecked = true;
        _suppressMarkersCheckboxEvent = false;
        _showNavMesh = false;
        NavMeshCheckBox.IsChecked = false;
        _showRenderedObjects = false;
        TopDownProvider = null;
        if (RenderedObjectsCheckBox is not null) RenderedObjectsCheckBox.IsChecked = false;
        CancelTopDownOverlay();
        DisposeWorldBitmaps();
        _worldHeightmapDirty = true;
        HideLayerBuildStatus();
        DisposeCellDetailBitmaps();
        WorldspaceComboBox.Items.Clear();
        ExportButton.IsEnabled = false;
        MapCanvas.Invalidate();
    }

    public void Dispose()
    {
        CancelTerrainStream();
        CancelTopDownOverlay();
        DisposeWorldBitmaps();
        DisposeCellDetailBitmaps();
        DisposeMarkerIcons();
    }

    /// <summary>Invalidates any in-flight async build without necessarily clearing the current layer.</summary>
    private void InvalidateWorldBitmap(bool keepCurrentBitmap)
    {
        CancelTerrainStream();
        _worldHeightmapBuildVersion++;
        Map2DProfilerTrace.Event("version-bump",
            $"to={_worldHeightmapBuildVersion} keepBitmap={keepCurrentBitmap}");
        _worldHeightmapDirty = true;
        _terrainTextureViewportKey = null;
        if (!keepCurrentBitmap)
        {
            ClearWorldBitmaps();
        }
    }

    /// <summary>
    ///     Cancels any in-flight TerrainTextures streaming build so its background workers
    ///     stop decoding cells the user has already panned away from. Also stops the throttle
    ///     timer and drops any cells buffered for apply (they belong to the abandoned stream).
    ///     Cheap and idempotent — a still-needed viewport will re-arm the timer from the draw path.
    /// </summary>
    private void CancelTerrainStream()
    {
        StopViewportTimer();
        _pendingCellApplies.Clear();
        _inFlightCellKeys.Clear();
        if (_terrainStreamCts is null) return;
        // Cancel only — the stream owns its CTS and disposes it (via a UI-thread hop) once all
        // its awaited work completes. Disposing here raced the stream's live token registrations
        // on worker threads. The field can't reference a disposed CTS: the disposal hop nulls or
        // replaces it first, on this same UI thread.
        _terrainStreamCts.Cancel();
        _terrainStreamCts = null;
    }

    /// <summary>
    ///     Disposes the world-overview bitmap (single or per-cell dict). The per-cell dict
    ///     entries are device-bound <see cref="CanvasBitmap" />s so each one needs explicit
    ///     disposal.
    /// </summary>
    private void DisposeWorldBitmaps()
    {
        CancelTerrainStream();
        _worldHeightmapBuildVersion++;
        ClearWorldBitmaps();
    }

    private void ClearWorldBitmaps()
    {
        _terrainTextureViewportKey = null;
        _pendingCellApplies.Clear();
        _inFlightCellKeys.Clear();
        // TerrainTextures aggregate LOD is worldspace/water/layer-specific — drop it so a switch
        // rebuilds it (a stale aggregate bumps the version, so an in-flight build's result is dropped).
        _terrainAggregateBitmap?.Dispose();
        _terrainAggregateBitmap = null;
        _terrainAggregateWorldspaceFormId = null;
        _terrainTexturesAggregateActive = false;
        _terrainAggregateUnavailable = false;
        _terrainAggregateVersion++;
        // World water layer is worldspace-specific (not layer/water-toggle specific); drop it so a
        // worldspace switch rebuilds it. The version bump drops any in-flight build's result.
        _worldWaterBitmap?.Dispose();
        _worldWaterBitmap = null;
        _worldWaterWorldspaceFormId = null;
        _worldWaterVersion++;
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;
        if (_layerCellBitmaps is not null)
        {
            var disposed = _layerCellBitmaps.Count;
            foreach (var bmp in _layerCellBitmaps.Values) bmp.Dispose();
            _layerCellBitmaps = null;
            _layerCellBitmapsCacheGen++;
            Map2DProfilerTrace.Event("cache-gen-bump",
                $"to={_layerCellBitmapsCacheGen} reason=ClearWorldBitmaps disposed={disposed}");
        }
        if (_layerWaterCellBitmaps is not null)
        {
            foreach (var bmp in _layerWaterCellBitmaps.Values) bmp.Dispose();
            _layerWaterCellBitmaps = null;
        }
        DisposeCoarseTileBitmaps();
        _layerCellBitmapsLayer = null;
        // The cache the held cap was sized for is gone; let the next rebuild size fresh.
        _layerCellBitmapCapPolicy.Reset();
    }

    /// <summary>Disposes the coarse multi-cell tile bitmaps and clears the cache (idempotent).</summary>
    private void DisposeCoarseTileBitmaps()
    {
        if (_coarseTileBitmaps is not null)
        {
            foreach (var bmp in _coarseTileBitmaps.Values) bmp.Dispose();
            _coarseTileBitmaps = null;
        }
        _coarseTileCellSpan = 0;
        _coarseTilePixelsPerCell = 0;
        _coarseTileBitmapsLayer = null;
    }

    // ========================================================================
    // Inner Types
    // ========================================================================

    /// <summary>Top-level display mode of the world map (whole worldspace, a single cell, or a cell list).</summary>
    internal enum ViewMode
    {
        WorldOverview,
        CellDetail,
        CellBrowser
    }

    /// <summary>Which set of cells the cell-browser lists.</summary>
    internal enum BrowserMode
    {
        None,
        Interiors,
        AllCells
    }

    /// <summary>A captured world map navigation state used for back/forward history.</summary>
    internal record WorldNavState(ViewMode Mode, BrowserMode Browser, int WorldspaceComboIndex, uint? CellFormId);

    private readonly record struct TerrainTextureViewportKey(
        int MinGx,
        int MaxGx,
        int MinGy,
        int MaxGy,
        int PixelsPerCell);

    private readonly record struct TerrainTextureViewportRequest(
        TerrainTextureViewportKey Key,
        List<CellRecord> Cells,
        int PixelsPerCell);

    /// <summary>One row in the cell-browser list: a cell plus its precomputed display strings.</summary>
    internal sealed class CellListItem
    {
        public string Group { get; init; } = "";
        public string GridLabel { get; init; } = "";

        /// <summary>The cell's EditorID (or 0xFormID fallback) — the primary name column.</summary>
        public string DisplayName { get; init; } = "";

        /// <summary>The cell's in-game FULL name, shown as a separate column; empty when the cell has none.</summary>
        public string ProperName { get; init; } = "";

        public string ObjectCount { get; init; } = "";
        public required CellRecord Cell { get; init; }
    }

    /// <summary>A named group of cell-list rows for the grouped ListView.</summary>
    internal sealed class CellListGroup(string key, List<CellListItem> items) : List<CellListItem>(items)
    {
        public string Key { get; } = key;
    }

}
