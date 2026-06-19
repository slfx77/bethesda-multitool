using System.Collections.Concurrent;
using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using WinRT.Interop;

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
    private List<CellListItem> _allCellItems = [];
    // Sort order within each group of the cell browser (Grid default; "Object count" surfaces the
    // busiest cells first). Driven by the CellSortCombo dropdown.
    private CellSortMode _cellSortMode = CellSortMode.Grid;
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

    // --- Marker icon bitmaps ---
    private Dictionary<MapMarkerType, CanvasBitmap>? _markerIconBitmaps;

    // Pre-tinted marker icons, rebuilt only when the color scheme changes. The overview used to build a
    // ColorMatrixEffect PER MARKER PER FRAME (DrawTintedIcon) — hundreds of Direct2D effect graphs every
    // frame at zoomed-out overview where every worldspace marker is in view — which tanked frame time
    // (hiding markers was a huge speedup). Tinting each icon once and blitting the cached bitmap collapses
    // that to a handful of one-time tint passes. Keyed by the scheme's packed ARGB so a scheme change rebuilds.
    private Dictionary<MapMarkerType, CanvasBitmap>? _tintedMarkerIconBitmaps;
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
        _initializing = false; // XAML load done — filter toggle handlers may now run for real.
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
        _state.LoadData(data);
        _worldHeightmapDirty = true;
        _currentColorScheme = HeightmapColorScheme.DefaultForFile(data.SourceFilePath);
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
            WorldspaceComboBox.Items.Add($"{name} \u2014 {ws.Cells.Count} cells");
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
        LegendToggleIcon.Glyph = _legendExpanded ? "\uE70D" : "\uE70E";
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

    private void RenderedObjectsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showRenderedObjects = RenderedObjectsCheckBox?.IsChecked == true
            && _topDownProvider?.CanRenderTopDown == true;
        if (_showRenderedObjects)
        {
            // Re-evaluate the viewport + kick a request on the next settle.
            _topDownContext = null;
            _topDownBoundsKey = null;
            _topDownRequestPending = true;
            _topDownLastRequestTick = 0; // first request after enabling fires immediately (no 300ms stall)
            EnsureViewportTimerRunning();
        }
        else
        {
            // Off: drop the overlay so its VRAM is reclaimed and the dot/box markers return.
            CancelTopDownOverlay();
        }
        MapCanvas?.Invalidate();
    }

    /// <summary>Enables the "Rendered models" toggle only when the 3D top-down provider is ready
    /// (D3D12 backend + terrain + reference pipeline up). Disables + unchecks it otherwise.</summary>
    private void UpdateRenderedObjectsAvailability()
    {
        if (RenderedObjectsCheckBox is null) return;
        var available = _topDownProvider?.CanRenderTopDown == true;
        RenderedObjectsCheckBox.IsEnabled = available;
        if (!available && (RenderedObjectsCheckBox.IsChecked == true || _showRenderedObjects))
        {
            RenderedObjectsCheckBox.IsChecked = false;
            _showRenderedObjects = false;
            CancelTopDownOverlay();
        }
    }

    /// <summary>Top-down sprites apply to exterior views only — World Overview, or a CellDetail view
    /// of an exterior cell. Interiors keep the dot/box markers (a top-down render shows the ceiling).</summary>
    private bool IsTopDownEligible() =>
        _state.Mode == ViewMode.WorldOverview || _state.SelectedCell is { IsInterior: false };

    private void DisposeTopDownOverlay()
    {
        _topDownOverlay?.Dispose();
        _topDownOverlay = null;
        _topDownIncomplete = false;
    }

    /// <summary>Cancels any in-flight top-down request, drops the overlay, and resets request state.
    /// Idempotent; safe to call on teardown / toggle-off / worldspace switch.</summary>
    private void CancelTopDownOverlay()
    {
        _topDownGen++;
        _topDownRequestPending = false;
        _topDownContext = null;
        _topDownBoundsKey = null;
        _topDownLastRequestTick = 0; // next enable/request fires immediately rather than waiting out the gate
        if (_topDownCts is not null)
        {
            try { _topDownCts.Cancel(); }
            catch (ObjectDisposedException) { /* already disposed by a concurrent reset — nothing to cancel */ }
            _topDownCts.Dispose();
            _topDownCts = null;
        }
        DisposeTopDownOverlay();
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
            showWater: false, _currentLayer, _data, _data?.RenderCache);
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
    // Toolbar Events
    // ========================================================================

    private void WorldspaceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_data == null || WorldspaceComboBox.SelectedIndex < 0) return;

        var result = _state.SelectWorldspaceIndex(WorldspaceComboBox.SelectedIndex);
        if (result != null)
        {
            _currentDefaultWaterHeight = result.DefaultWaterHeight;
            // Per-worldspace water tint (Shallow + Deep) sourced from the WATR record's DNAM
            // colors. The 2D map's water overlay lerps Shallow→Deep by mask intensity so
            // different worldspaces' waters (Potomac muddy brown vs Lake Mead clean blue,
            // etc.) actually look different. Null when no WATR FormID, no DNAM, or DNAM has
            // no colors — downstream falls back to the legacy solid blue.
            var waterFormId = _state.SelectedWorldspace?.WaterFormId;
            _currentWaterPalette = waterFormId is uint wid && _data is not null
                ? WaterColorPalette.GetOrCreate(_data, wid)
                : null;
            ApplyWorldspaceSwitch();
        }
    }

    private void ApplyWorldspaceSwitch()
    {
        InvalidateWorldBitmap(keepCurrentBitmap: false);
        _worldHmMinX = _worldHmMaxY = _worldHmPixelWidth = _worldHmPixelHeight = 0;
        DisposeCellDetailBitmaps();
        BuildCellGridLookup();
        SetCanvasMode(true);
        ExportButton.IsEnabled = true;
        ApplyZoomToFitWorldspace();
        MapCanvas.Invalidate();
    }

    private void ColorSchemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = ColorSchemeComboBox.SelectedIndex;
        if (idx < 0 || idx >= HeightmapColorScheme.Presets.Length)
        {
            return;
        }

        _currentColorScheme = HeightmapColorScheme.Presets[idx];
        InvalidateWorldBitmap(keepCurrentBitmap: true);
        DisposeCellDetailBitmaps();
        MapCanvas.Invalidate();
    }

    private void LayerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = LayerComboBox.SelectedIndex;
        var values = Enum.GetValues<WorldMapLayer>();
        if (idx < 0 || idx >= values.Length) return;

        _currentLayer = values[idx];

        // Color scheme only applies to the heightmap layer (user-confirmed).
        // Null-guard because the SelectionChanged event can fire during XAML load before
        // sibling fields are assigned (see winui3_selectionchanged_early_fire memory).
        if (ColorSchemeComboBox is not null)
        {
            ColorSchemeComboBox.Visibility = _currentLayer == WorldMapLayer.Heightmap
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        InvalidateWorldBitmap(keepCurrentBitmap: true);

        if (_cellHeightmapBitmap is not null && _state.SelectedCell is not null)
        {
            RebuildCellDetailBitmaps(_state.SelectedCell);
        }

        MapCanvas?.Invalidate();
    }

    internal WorldNavState CaptureNavState() => new(
        _state.Mode,
        _state.ActiveBrowser,
        WorldspaceComboBox.SelectedIndex,
        _state.SelectedCell?.FormId);

    internal void RestoreNavState(WorldNavState state)
    {
        _suppressNavEvents = true;
        DisposeCellDetailBitmaps();
        HoverInfoText.Text = "";

        switch (state.Mode)
        {
            case ViewMode.CellBrowser:
                if (state.Browser == BrowserMode.Interiors)
                {
                    InteriorsButton_Click(this, new RoutedEventArgs());
                }
                else if (state.Browser == BrowserMode.AllCells)
                {
                    AllCellsButton_Click(this, new RoutedEventArgs());
                }

                break;

            case ViewMode.CellDetail when state.CellFormId.HasValue:
                if (state.WorldspaceComboIndex >= 0 &&
                    state.WorldspaceComboIndex != WorldspaceComboBox.SelectedIndex)
                {
                    WorldspaceComboBox.SelectedIndex = state.WorldspaceComboIndex;
                }

                var cell = _state.FindCellByFormId(state.CellFormId.Value);
                if (cell != null)
                {
                    NavigateToCell(cell);
                }

                break;

            default:
                if (state.WorldspaceComboIndex >= 0 &&
                    state.WorldspaceComboIndex < WorldspaceComboBox.Items.Count)
                {
                    WorldspaceComboBox.SelectedIndex = state.WorldspaceComboIndex;
                }

                break;
        }

        _suppressNavEvents = false;
    }

    private void NotifyBeforeNavigate()
    {
        if (!_suppressNavEvents) BeforeNavigate?.Invoke();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (_state.Mode == ViewMode.CellDetail && _state.SelectedCell != null)
        {
            WorldMapViewportHelper.ZoomToFitCell(_state.SelectedCell,
                (float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight,
                out _zoom, out _panOffset);
        }
        else if (GetActiveCells().Count > 0)
        {
            ApplyZoomToFitWorldspace();
        }

        MapCanvas.Invalidate();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;

        var activeCells = GetActiveCells();
        var cellsWithGrid = activeCells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
        if (cellsWithGrid.Count == 0) return;

        var minGx = cellsWithGrid.Min(c => c.GridX!.Value);
        var maxGx = cellsWithGrid.Max(c => c.GridX!.Value);
        var minGy = cellsWithGrid.Min(c => c.GridY!.Value);
        var maxGy = cellsWithGrid.Max(c => c.GridY!.Value);
        var cellsWide = maxGx - minGx + 1;
        var cellsTall = maxGy - minGy + 1;

        var dialog = new MapExportDialog(
            cellsWide, cellsTall,
            initialLayer: _currentLayer,
            initialIncludeMarkers: !_hiddenCategories.Contains(PlacedObjectCategory.MapMarker),
            initialIncludeNavMesh: _showNavMesh,
            initialIncludeWater: _showWater,
            initialIncludeGrid: _showCellGrid,
            initialLongEdgePx: ExportLongEdge)
        {
            XamlRoot = XamlRoot
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary) return;
        var req = dialog.GetRequest();

        // Resolve output px/cell. Don't upscale beyond the layer's real source detail (132/528 for
        // texture, 132 for the 33-native heightmap-family layers).
        var maxGridDim = Math.Max(cellsWide, cellsTall);
        var maxSourcePpc = req.Layer == WorldMapLayer.TerrainTextures
            ? WorldMapLayerRenderer.MaxTexturePixelsPerCell
            : WorldMapLayerRenderer.HeightmapPixelsPerCell * 4;
        var ppc = Math.Clamp(req.LongEdgePx / maxGridDim, 1, maxSourcePpc);

        int cellsPerTile;
        if (req.Tiled)
        {
            // Split into tiles each within the GPU max-texture bound.
            cellsPerTile = Math.Max(1, WorldMapExporter.ExportMaxTileDimension / ppc);
        }
        else
        {
            // Single image: clamp px/cell so the whole worldspace fits one texture.
            ppc = Math.Min(ppc, Math.Max(1, WorldMapExporter.ExportMaxTileDimension / maxGridDim));
            cellsPerTile = maxGridDim;
        }

        var wsName = _state.SelectedWorldspace?.EditorId ?? _state.SelectedWorldspace?.FullName ?? "worldspace";
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        picker.SuggestedFileName = $"{wsName}_map";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        EnsureMarkerIcons(MapCanvas);

        // Apply markers preference: hidden if user unchecked Map markers in the dialog.
        var exportHiddenCategories = new HashSet<PlacedObjectCategory>(_hiddenCategories);
        if (!req.IncludeMarkers)
        {
            exportHiddenCategories.Add(PlacedObjectCategory.MapMarker);
        }
        else
        {
            exportHiddenCategories.Remove(PlacedObjectCategory.MapMarker);
        }

        var progressDialog = new ExportProgressController(XamlRoot);
        _ = progressDialog.ShowAsync();
        ExportButton.IsEnabled = false;
        try
        {
            await RunExportAsync(req, activeCells, file.Path, minGx, maxGx, minGy, maxGy,
                ppc, cellsPerTile, exportHiddenCategories, progressDialog, progressDialog.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            // User canceled — leave whatever tiles were already written.
        }
        catch (Exception ex)
        {
            BethesdaMultitool.Core.Logger.Instance.Warn("Map export failed: {0}", ex.ToString());
        }
        finally
        {
            progressDialog.Complete();
            ExportButton.IsEnabled = true;
        }
    }

    /// <summary>
    ///     Renders the export as one capped image or a grid of GPU-bounded tiles. Decodes each tile's
    ///     terrain cells off the UI thread (bounded memory: only the tile's cells, plus a 1-cell margin
    ///     so cross-tile edge blending is seamless), uploads + composites + saves on the UI thread, and
    ///     reports progress between tiles so the modal dialog animates. Tiled runs emit
    ///     <c>{name}_r{row}_c{col}.png</c> plus a <c>{name}_manifest.json</c>.
    /// </summary>
    private async Task RunExportAsync(
        MapExportRequest req, List<CellRecord> activeCells, string basePath,
        int minGx, int maxGx, int minGy, int maxGy, int ppc, int cellsPerTile,
        HashSet<PlacedObjectCategory> hiddenCategories,
        ExportProgressController progress, CancellationToken ct)
    {
        var cols = (maxGx - minGx) / cellsPerTile + 1;
        var rows = (maxGy - minGy) / cellsPerTile + 1;
        var totalTiles = cols * rows;
        var tiled = totalTiles > 1;

        // Texture layer decodes per-tile; other layers build their single (small) 33-px/cell bitmap
        // once and reuse it for every tile (Win2D clips it to each tile's bounds).
        LandscapeTexturePalette? palette = null;
        WaterColorPalette? waterPalette = null;
        if (req.Layer == WorldMapLayer.TerrainTextures && _data is not null)
        {
            palette = LandscapeTexturePalette.GetOrCreate(_data);
            waterPalette = req.IncludeWater && _state.SelectedWorldspace?.WaterFormId is uint wid
                ? WaterColorPalette.GetOrCreate(_data, wid)
                : null;
        }

        WorldMapHeightmapBuilder.HeightmapInfo? single = null;
        if (palette is null)
        {
            single = WorldMapHeightmapBuilder.Build(
                MapCanvas, activeCells, _cachedGrayscale, _cachedWaterMask,
                _cachedHmWidth, _cachedHmHeight,
                _state.SelectedWorldspace, _data,
                _currentDefaultWaterHeight, _currentColorScheme, req.IncludeWater,
                req.Layer, _data?.RenderCache);
        }

        var dir = Path.GetDirectoryName(basePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var manifestTiles = new List<object>();

        try
        {
            var tileIndex = 0;
            for (var tj = 0; tj < rows; tj++)
            {
                for (var ti = 0; ti < cols; ti++)
                {
                    ct.ThrowIfCancellationRequested();
                    tileIndex++;
                    progress.Report(tiled ? "Rendering tile" : "Rendering", tileIndex, totalTiles);
                    await Task.Yield(); // let the dialog paint the new status before the heavy work

                    var tgx0 = minGx + (ti * cellsPerTile);
                    var tgx1 = Math.Min(maxGx, tgx0 + cellsPerTile - 1);
                    var tgy0 = minGy + (tj * cellsPerTile);
                    var tgy1 = Math.Min(maxGy, tgy0 + cellsPerTile - 1);
                    var tileW = (tgx1 - tgx0 + 1) * ppc;
                    var tileH = (tgy1 - tgy0 + 1) * ppc;

                    Dictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? tileBitmaps = null;
                    if (palette is not null)
                    {
                        // +1-cell margin so edge cells see their cross-tile neighbors when blending.
                        var marginCells = activeCells.Where(c =>
                            c.GridX is int gx && c.GridY is int gy &&
                            gx >= tgx0 - 1 && gx <= tgx1 + 1 && gy >= tgy0 - 1 && gy <= tgy1 + 1).ToList();
                        var cache = _data?.RenderCache;
                        var includeWater = req.IncludeWater;
                        var waterHeight = _currentDefaultWaterHeight;
                        var pal = palette;
                        var wpal = waterPalette;
                        var perCell = await Task.Run(
                            () => WorldMapLayerRenderer.RenderTerrainTexturesPerCell(
                                marginCells, pal, waterHeight, includeWater, cache, ppc, wpal), ct);
                        if (perCell is not null)
                        {
                            tileBitmaps = new Dictionary<(int, int, int), CanvasBitmap>(perCell.Count);
                            foreach (var ((gx, gy), bytes) in perCell)
                            {
                                tileBitmaps[(gx, gy, ppc)] = CanvasBitmap.CreateFromBytes(
                                    MapCanvas, bytes, ppc, ppc,
                                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
                            }
                        }
                    }

                    var tilePath = tiled ? Path.Combine(dir, $"{name}_r{tj}_c{ti}{ext}") : basePath;
                    try
                    {
                        await WorldMapExporter.ExportWorldspacePngAsync(
                            tilePath, tileW, tileH, ppc, tgx0, tgx1, tgy0, tgy1,
                            MapCanvas,
                            single?.Bitmap, single?.PixelWidth ?? 0, single?.PixelHeight ?? 0,
                            single?.MinX ?? 0, single?.MaxY ?? 0,
                            tileBitmaps,
                            _state.FilteredMarkers, hiddenCategories, _markerIconBitmaps, _currentColorScheme,
                            _data, activeCells, req.IncludeNavMesh, req.IncludeGrid);
                    }
                    finally
                    {
                        if (tileBitmaps is not null)
                        {
                            foreach (var bmp in tileBitmaps.Values) bmp.Dispose();
                        }
                    }

                    manifestTiles.Add(new
                    {
                        row = tj, col = ti, file = Path.GetFileName(tilePath),
                        gridX0 = tgx0, gridX1 = tgx1, gridY0 = tgy0, gridY1 = tgy1,
                        imageW = tileW, imageH = tileH
                    });
                }
            }

            if (tiled)
            {
                progress.Report("Writing manifest", totalTiles, totalTiles);
                var manifest = new
                {
                    layer = req.Layer.ToString(),
                    pixelsPerCell = ppc,
                    tilesWide = cols, tilesTall = rows,
                    gridX0 = minGx, gridX1 = maxGx, gridY0 = minGy, gridY1 = maxGy,
                    tiles = manifestTiles
                };
                var json = System.Text.Json.JsonSerializer.Serialize(manifest, IndentedJsonOptions);
                await File.WriteAllTextAsync(Path.Combine(dir, $"{name}_manifest.json"), json, ct);
            }
        }
        finally
        {
            single?.Bitmap.Dispose();
        }
    }

    private void InteriorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null || _data.InteriorCells.Count == 0) return;
        EnterBrowserMode(BrowserMode.Interiors);
        PopulateInteriorCellBrowser();
    }

    private void AllCellsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;
        EnterBrowserMode(BrowserMode.AllCells);
        PopulateCellBrowser();
    }

    private void EnterBrowserMode(BrowserMode browser)
    {
        NotifyBeforeNavigate();
        _state.EnterBrowser(browser);
        SetCanvasMode(false);
        ExportButton.IsEnabled = false;
        ClearWorldspaceSelection();
        FilterHasObjects.IsChecked = false;
        FilterNamedOnly.IsChecked = false;
    }

    private void CellFilter_Changed(object sender, RoutedEventArgs e) => ApplyCellFilters();

    private void CellSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyCellFilters();

    private void CellSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _cellSortMode = CellSortCombo.SelectedIndex == 1 ? CellSortMode.ObjectCount : CellSortMode.Grid;
        if (_allCellItems.Count > 0) ApplyCellFilters();
    }

    private void ApplyCellFilters()
    {
        var query = CellSearchBox.Text?.Trim() ?? "";
        var hasObjects = FilterHasObjects.IsChecked == true;
        var namedOnly = FilterNamedOnly.IsChecked == true;
        var filtered = WorldMapCellBrowser.ApplyFilters(_allCellItems, query, hasObjects, namedOnly);
        RebuildCellListFromItems(filtered);
    }

    // ========================================================================
    // Win2D Draw
    // ========================================================================

    /// <summary>
    ///     Logs a UI-thread render/timer-tick exception (with rich 2D view context) exactly once per
    ///     distinct signature, so a deterministic per-frame fault doesn't flood the log. Callers
    ///     swallow the exception so a single bad frame or rebuild tick can't tear down the whole
    ///     window — WinUI routes an unhandled UI-thread exception to
    ///     <see cref="FalloutApp.App_UnhandledException" />, which terminates the process. The full
    ///     exception (incl. stack) goes to the file log so the root cause is still diagnosable.
    /// </summary>
    private void LogUiThreadFault(string where, Exception ex)
    {
        var sig = $"{where}|{ex.GetType().FullName}|{ex.Message}";
        if (sig == _lastUiFaultSignature) return;
        _lastUiFaultSignature = sig;
        BethesdaMultitool.Core.Logger.Instance.Error(
            "[Map2D] UI-thread fault in {0}: mode={1} layer={2} zoom={3:F5} pan=({4:F1},{5:F1}) " +
            "ws=0x{6:X8} renderedObjects={7} navMesh={8} aggregate={9} cacheSize={10} cap={11}\n{12}",
            where, _state.Mode, _currentLayer, _zoom, _panOffset.X, _panOffset.Y,
            _state.SelectedWorldspace?.FormId ?? 0u, _showRenderedObjects, _showNavMesh,
            _terrainTexturesAggregateActive, _layerCellBitmaps?.Count ?? 0, _layerCellBitmapCap, ex);
    }

    private void MapCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(Color.FromArgb(255, 20, 20, 25));

        if (_data == null) return;

        var canvasW = (float)sender.ActualWidth;
        var canvasH = (float)sender.ActualHeight;

        if (Map2DProfilerTrace.IsEnabled)
        {
            Map2DProfilerTrace.IncrementFrame();
            var cacheSize = _layerCellBitmaps?.Count ?? 0;
            Map2DProfilerTrace.Event("draw",
                $"zoom={_zoom:F4} pan=({_panOffset.X:F1},{_panOffset.Y:F1}) layer={_currentLayer} cacheSize={cacheSize} cap={_layerCellBitmapCap} cacheGen={_layerCellBitmapsCacheGen} buildVersion={_worldHeightmapBuildVersion}");
        }

        // A render exception here used to crash the whole app silently (routed to the console-only
        // App.UnhandledException). Catch + log with full context, skip the bad frame, keep the window
        // alive. The next Invalidate redraws cleanly once the transient condition clears.
        var drawStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            DrawMapContent(ds, canvasW, canvasH);
        }
        catch (Exception ex)
        {
            LogUiThreadFault("MapCanvas_Draw", ex);
        }

        if (Map2DProfilerTrace.IsEnabled)
        {
            var drawMs = System.Diagnostics.Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds;
            if (drawMs >= 40)
            {
                Map2DProfilerTrace.Event("slow-draw",
                    $"ms={drawMs:F0} zoom={_zoom:F4} layer={_currentLayer} cacheSize={_layerCellBitmaps?.Count ?? 0}");
            }
        }
    }

    private void DrawMapContent(CanvasDrawingSession ds, float canvasW, float canvasH)
    {
        // MapCanvas_Draw already early-outs when _data is null, but that guard doesn't flow into this
        // extracted method — re-assert it so the nullable analysis treats _data as non-null below.
        if (_data is null) return;

        EnsureHeightmapBitmap(canvasW, canvasH);
        MaybeScheduleTopDownRequest(canvasW, canvasH);

        if (_state.Mode == ViewMode.WorldOverview)
        {
            EnsureMarkerIcons(MapCanvas);
            // Pre-tint the marker icons once for this color scheme (rebuilt only on scheme change) so the
            // overview blits cached bitmaps instead of building a ColorMatrixEffect per marker per frame.
            var tintedMarkers = EnsureTintedMarkerIcons(MapCanvas, _currentColorScheme) ?? _markerIconBitmaps;
            // Build the standalone world water layer once per worldspace (cheap guard); used as the water
            // pass over every non-per-cell terrain layer (heightmap/vertex/slope/regions/terrain-aggregate).
            EnsureWorldWaterBitmap(_state.SelectedWorldspace?.FormId);

            // TerrainTextures aggregate LOD: when active, composite the single downscaled bitmap and
            // suppress the per-cell dict so the renderer draws the aggregate (it prefers per-cell when
            // present). Otherwise the normal single-bitmap (+ per-cell for TerrainTextures zoomed in).
            var aggActive = _currentLayer == WorldMapLayer.TerrainTextures
                && _terrainTexturesAggregateActive && _terrainAggregateBitmap is not null;
            var overviewBitmap = aggActive ? _terrainAggregateBitmap : _worldHeightmapBitmap;
            var overviewBmpW = aggActive ? _terrainAggPixelWidth : _worldHmPixelWidth;
            var overviewBmpH = aggActive ? _terrainAggPixelHeight : _worldHmPixelHeight;
            var overviewMinX = aggActive ? _terrainAggMinX : _worldHmMinX;
            var overviewMaxY = aggActive ? _terrainAggMaxY : _worldHmMaxY;
            // ONLY the TerrainTextures layer draws the per-cell tile cache. Other layers (Heightmap,
            // VertexColors, Slope, Regions) draw their single aggregate bitmap. Without the layer check,
            // a TerrainTextures session that streamed the whole worldspace left _layerCellBitmaps populated
            // (up to ~16k tiles), and every Heightmap redraw then scanned + DrawImage'd all of them — a
            // continuous UI-thread peg (confirmed via a process dump: the hot thread sat in
            // DrawTextureCellBitmaps with a 16,396-entry cache while on the Heightmap layer). aggActive
            // already implies the TerrainTextures layer, so it stays correct for the aggregate LOD too.
            var overviewCells = (_currentLayer == WorldMapLayer.TerrainTextures && !aggActive)
                ? _layerCellBitmaps
                : null;
            // Exactly one flat-water source per frame: the per-cell tile cache when the per-cell terrain
            // path is active (zoomed-in TerrainTextures), else the shared world water bitmap (aggregate +
            // every secondary single-bitmap layer). Both are suppressed when the overlay is active.
            var overviewWater = overviewCells is not null ? _layerWaterCellBitmaps : null;
            var worldWaterBmp = overviewCells is null ? _worldWaterBitmap : null;
            var overviewBmpPixelsPerCell = aggActive
                ? _terrainAggPixelsPerCell
                : WorldMapLayerRenderer.HeightmapPixelsPerCell;

            // Coarse tiles are a heightmap-family artifact (oversized worldspace). A layer switch keeps
            // them resident for a smooth transition (InvalidateWorldBitmap keepCurrentBitmap), but
            // DrawWorldOverview draws them as an EXCLUSIVE background that wins over the TerrainTextures
            // tiles — so only hand them over when they were built for the CURRENT layer. Otherwise stale
            // heightmap coarse tiles hide the terrain textures (the "toggle layers → heightmap sticks" bug).
            var coarseForLayer = _coarseTileBitmapsLayer == _currentLayer ? _coarseTileBitmaps : null;
            var coarseSpanForLayer = coarseForLayer is not null ? _coarseTileCellSpan : 0;
            var coarsePpcForLayer = coarseForLayer is not null ? _coarseTilePixelsPerCell : 0;

            // Draw-decision trace (gated): on the TerrainTextures layer, what is actually composited —
            // the aggregate, the per-cell tiles, leftover coarse tiles, or just the heightmap base. Logged
            // only when the decision changes so it pinpoints the frame the heightmap starts winning.
            if (Map2DProfilerTrace.IsEnabled && _currentLayer == WorldMapLayer.TerrainTextures)
            {
                var drawKey = $"agg={aggActive}/{overviewBitmap is not null} cells={overviewCells?.Count ?? -1} coarse={coarseForLayer?.Count ?? -1} staleCoarse={(_coarseTileBitmaps is not null && _coarseTileBitmapsLayer != _currentLayer)} hmBmp={_worldHeightmapBitmap is not null} aggBmp={_terrainAggregateBitmap is not null}";
                if (drawKey != _lastTerrainDrawLog)
                {
                    _lastTerrainDrawLog = drawKey;
                    Map2DProfilerTrace.Event("tt-draw", drawKey);
                }
            }

            WorldMapOverviewRenderer.DrawWorldOverview(
                ds, _data, GetActiveCells(), _state.FilteredMarkers, _cellGridLookup, _spatialIndex,
                overviewBitmap,
                overviewBmpW, overviewBmpH, overviewMinX, overviewMaxY, overviewBmpPixelsPerCell,
                overviewCells,
                _zoom, _panOffset, canvasW, canvasH,
                _hiddenCategories, _hideDisabledActors,
                _state.SelectedObject, _hoveredObject,
                tintedMarkers, _currentColorScheme,
                _showCellGrid,
                _showRenderedObjects, _topDownOverlay,
                _topDownWorldMinX, _topDownWorldMaxX, _topDownWorldMinY, _topDownWorldMaxY,
                overviewWater, _showWater,
                worldWaterBmp, _worldWaterMinX, _worldWaterMaxY,
                _worldWaterPixelWidth, _worldWaterPixelHeight,
                WorldMapLayerRenderer.HeightmapPixelsPerCell,
                coarseForLayer, coarseSpanForLayer, coarsePpcForLayer);

            if (_showNavMesh)
            {
                ds.Transform = WorldMapViewportHelper.GetViewTransform(_zoom, _panOffset);
                var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
                    canvasW, canvasH, _zoom, _panOffset);
                WorldMapNavMeshOverlayRenderer.DrawWorldOverview(
                    ds, _data, GetActiveCells(), _spatialIndex, tlWorld, brWorld, _zoom);
            }

            // Dangling-REFR overlay (opt-in via DanglingRefsComboBox).
            // Drawn after overview so tinted cells overlay heightmap + cell grid.
            // Pass the active worldspace so Lucky38-interior actors don't get rendered
            // on the WastelandNV exterior map (their X/Y coords mean different things
            // in each worldspace's coordinate system).
            WorldMapDanglingRefOverlayRenderer.DrawOverlay(
                ds, _data.DanglingRefs, _data, _danglingThreshold,
                _state.SelectedWorldspace?.FormId,
                _spatialIndex,
                _zoom, _panOffset, canvasW, canvasH);
        }
        else if (_state.SelectedCell != null)
        {
            WorldMapCellDetailRenderer.DrawCellDetail(
                ds, _state.SelectedCell, _data, _cellHeightmapBitmap,
                _zoom, _panOffset, canvasW, canvasH,
                _hiddenCategories, _hideDisabledActors,
                _state.SelectedObject, _hoveredObject,
                _showRenderedObjects && IsTopDownEligible(), _topDownOverlay,
                _topDownWorldMinX, _topDownWorldMaxX, _topDownWorldMinY, _topDownWorldMaxY,
                _cellWaterBitmap, _showWater);

            if (_showNavMesh)
            {
                ds.Transform = WorldMapViewportHelper.GetViewTransform(_zoom, _panOffset);
                WorldMapNavMeshOverlayRenderer.DrawCellDetail(ds, _data, _state.SelectedCell, _zoom);
            }
        }

        // HUD (screen-space)
        ds.Transform = System.Numerics.Matrix3x2.Identity;
        ZoomLevelText.Text = $"{_zoom:P0}";
    }

    // ========================================================================
    // Pan/Zoom Input
    // ========================================================================

    private void MapCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MapCanvas);
        _panStartScreen = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _panOffsetAtStart = _panOffset;
        _isPanning = true;
        _pointerWasDragged = false;
        _pointerDownScreen = _panStartScreen;
        MapCanvas.CapturePointer(e.Pointer);
        // Take keyboard focus so WASD panning works after a click without tabbing to the canvas.
        MapCanvas.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void MapCanvas_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.W or Windows.System.VirtualKey.A
            or Windows.System.VirtualKey.S or Windows.System.VirtualKey.D)
        {
            _panKeysDown.Add(e.Key);
            EnsureViewportTimerRunning(); // the timer integrates the pan + won't idle-stop while keys are held
            e.Handled = true;
        }
    }

    private void MapCanvas_KeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_panKeysDown.Remove(e.Key)) e.Handled = true;
    }

    // Drop held keys when focus leaves the canvas (alt-tab / click away) so motion can't get stuck.
    private void MapCanvas_LostFocus(object sender, RoutedEventArgs e) => _panKeysDown.Clear();

    /// <summary>
    ///     Integrates held-WASD panning into <see cref="_panOffset" /> (screen-space, like the pointer
    ///     drag). W/S reveal north/south, A/D reveal west/east — i.e. the camera moves in the key's
    ///     direction. Returns true if the view moved this tick. Suppressed while a pointer drag is
    ///     active so the two pan paths don't fight.
    /// </summary>
    private bool ApplyKeyboardPan()
    {
        if (_panKeysDown.Count == 0 || _isPanning) return false;
        var dx = 0f;
        var dy = 0f;
        if (_panKeysDown.Contains(Windows.System.VirtualKey.A)) dx += PanPixelsPerTick;
        if (_panKeysDown.Contains(Windows.System.VirtualKey.D)) dx -= PanPixelsPerTick;
        if (_panKeysDown.Contains(Windows.System.VirtualKey.W)) dy += PanPixelsPerTick;
        if (_panKeysDown.Contains(Windows.System.VirtualKey.S)) dy -= PanPixelsPerTick;
        if (dx == 0f && dy == 0f) return false;

        _panOffset = new Vector2(_panOffset.X + dx, _panOffset.Y + dy);
        _viewportRebuildPending = true; // re-stream terrain for the shifted viewport
        _topDownRequestPending = true;  // keep the rendered-models overlay in sync when it's on
        MapCanvas.Invalidate();
        return true;
    }

    private void MapCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MapCanvas);
        var currentScreen = new Vector2((float)point.Position.X, (float)point.Position.Y);

        var worldPos = WorldMapViewportHelper.ScreenToWorld(currentScreen, _zoom, _panOffset);
        CoordsText.Text = $"X: {worldPos.X:F0}  Y: {-worldPos.Y:F0}";

        if (_isPanning)
        {
            var delta = currentScreen - _panStartScreen;
            if (delta.Length() > 3f) _pointerWasDragged = true;
            var newOffset = _panOffsetAtStart + delta;
            // EMA-smoothed pan velocity for the Preload margin in BuildTerrainTextureViewportRequest.
            // Coefficient picked empirically: low enough to ignore single-frame jitter, high enough
            // that a deliberate directional drag converges within ~5 frames.
            _panVelocity = Vector2.Lerp(_panVelocity, newOffset - _panOffset, 0.35f);
            _panOffset = newOffset;
            MapCanvas.Invalidate();
        }
        else if (_state.Mode == ViewMode.CellDetail && _state.SelectedCell != null)
        {
            var hitObj = WorldMapHitTester.HitTestPlacedObject(
                worldPos, _state.SelectedCell, _data!, _hiddenCategories, _hideDisabledActors, _zoom);
            if (hitObj != _hoveredObject)
            {
                _hoveredObject = hitObj;
                var hoverName = hitObj != null
                    ? PlacedObjectCategoryResolver.GetReferenceAwareName(hitObj, _data?.Resolver)
                    : null;
                HoverInfoText.Text = hitObj != null
                    ? $"{hitObj.RecordType}: {hoverName} at ({hitObj.X:F0}, {hitObj.Y:F0}, {hitObj.Z:F0})"
                    : FormatCellDisplayName(_state.SelectedCell);
                MapCanvas.Invalidate();
            }

            SetInteractiveCursor(hitObj != null);
        }
        else if (_state.Mode == ViewMode.WorldOverview && _data != null)
        {
            // Check dangling markers first so they take hover priority (matches click priority).
            DanglingRefPosition? hitDangling = null;
            if (_danglingThreshold != DanglingRefThreshold.None)
            {
                hitDangling = WorldMapDanglingRefOverlayRenderer.HitTest(
                    _data.DanglingRefs, _danglingThreshold,
                    _state.SelectedWorldspace?.FormId, worldPos, _zoom, _spatialIndex);
            }

            if (hitDangling != null)
            {
                var cellName = string.IsNullOrEmpty(hitDangling.CellEditorId)
                    ? $"cell 0x{hitDangling.CellFormId:X8}"
                    : hitDangling.CellEditorId;
                HoverInfoText.Text =
                    $"Dangling REFR 0x{hitDangling.FormId:X8} (base 0x{hitDangling.BaseFormId:X8}) " +
                    $"-> {cellName} [{hitDangling.Confidence}] at ({hitDangling.X:F0}, {hitDangling.Y:F0}, {hitDangling.Z:F0})";
                SetInteractiveCursor(true);
            }
            else
            {
                var hover = WorldMapHitTester.ProcessOverviewHover(
                    worldPos, _data, GetActiveCells(), _state.FilteredMarkers, _cellGridLookup,
                    _spatialIndex, _hiddenCategories, _hideDisabledActors, _zoom);
                HoverInfoText.Text = hover.StatusText;
                SetInteractiveCursor(hover.IsInteractive);
                if (hover.HoveredObject != _hoveredObject)
                {
                    _hoveredObject = hover.HoveredObject;
                    MapCanvas.Invalidate();
                }
            }
        }

        e.Handled = true;
    }

    private void MapCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        MapCanvas.ReleasePointerCapture(e.Pointer);
        if (_isPanning && !_pointerWasDragged)
        {
            // Dangling markers take priority — they're drawn on top of everything else, so a
            // click that lands on one should inspect the dangling REFR rather than fall through
            // to the cell or placed-object beneath.
            if (_data != null && _danglingThreshold != DanglingRefThreshold.None &&
                _state.Mode == ViewMode.WorldOverview)
            {
                var worldClick = WorldMapViewportHelper.ScreenToWorld(_pointerDownScreen, _zoom, _panOffset);
                var hitDangling = WorldMapDanglingRefOverlayRenderer.HitTest(
                    _data.DanglingRefs, _danglingThreshold,
                    _state.SelectedWorldspace?.FormId, worldClick, _zoom, _spatialIndex);
                if (hitDangling != null)
                {
                    var synthetic = WorldMapDanglingRefOverlayRenderer.SynthesizePlacedReference(hitDangling);
                    InspectObject?.Invoke(this, synthetic);
                    _isPanning = false;
                    e.Handled = true;
                    return;
                }
            }

            var result = WorldMapHitTester.HandleClick(
                _pointerDownScreen, _state.Mode, _data, GetActiveCells(),
                _state.SelectedCell,
                _state.FilteredMarkers, _cellGridLookup, _spatialIndex, _hiddenCategories, _hideDisabledActors,
                _zoom, _panOffset);

            switch (result.Action)
            {
                case WorldMapHitTester.ClickResult.ClickAction.ShowObject:
                    InspectObject?.Invoke(this, result.Object!);
                    break;
                case WorldMapHitTester.ClickResult.ClickAction.ShowCell:
                    InspectCell?.Invoke(this, result.Cell!);
                    break;
                case WorldMapHitTester.ClickResult.ClickAction.DeselectAndShowCell:
                    _state.SelectObject(null);
                    InspectCell?.Invoke(this, result.Cell!);
                    MapCanvas.Invalidate();
                    break;
            }
        }

        _isPanning = false;
        _panVelocity = Vector2.Zero;
        e.Handled = true;
    }

    private void MapCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MapCanvas);
        var screenPos = new Vector2((float)point.Position.X, (float)point.Position.Y);
        var delta = point.Properties.MouseWheelDelta;

        var worldBeforeZoom = WorldMapViewportHelper.ScreenToWorld(screenPos, _zoom, _panOffset);
        var zoomFactor = delta > 0 ? 1.15f : 1f / 1.15f;
        _zoom = Math.Clamp(_zoom * zoomFactor, 0.001f, 50f);

        var newTransform = System.Numerics.Matrix3x2.CreateScale(_zoom);
        var worldAfterZoom = Vector2.Transform(worldBeforeZoom, newTransform);
        _panOffset = screenPos - worldAfterZoom;

        // Arm the zoom-settle window for the streaming layer only: defer the (expensive, higher-res)
        // re-stream until the zoom stops. The draw composites the existing bitmaps scaled meanwhile.
        // Other layers use a single bitmap and don't re-stream on zoom, so they need no settle.
        if (_state.Mode == ViewMode.WorldOverview && _currentLayer == WorldMapLayer.TerrainTextures)
        {
            _zoomSettleTicks = ZoomSettleTicks;
            EnsureViewportTimerRunning();
        }

        MapCanvas.Invalidate();
        e.Handled = true;
    }

    // ========================================================================
    // Navigation
    // ========================================================================

    /// <summary>Switches to cell-detail view for the given cell and zooms to fit it.</summary>
    public void NavigateToCell(CellRecord cell)
    {
        NotifyBeforeNavigate();
        _state.NavigateToCell(cell);
        _hoveredObject = null;
        SetCanvasMode(true);

        HoverInfoText.Text = FormatCellDisplayName(cell);

        RebuildCellDetailBitmaps(cell);

        WorldMapViewportHelper.ZoomToFitCell(cell,
            (float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight,
            out _zoom, out _panOffset);
        MapCanvas.Invalidate();
    }

    private static string FormatCellDisplayName(CellRecord cell) =>
        cell.FullName ?? cell.EditorId ?? $"0x{cell.FormId:X8}";

    internal void NavigateToCellPublic(CellRecord cell) => NavigateToCell(cell);

    /// <summary>Selects the given worldspace, then centers the overview on the given cell.</summary>
    public void NavigateToWorldspaceAndCell(int worldspaceIndex, CellRecord cell)
    {
        WorldspaceComboBox.SelectedIndex = worldspaceIndex;
        NavigateToCellInOverview(cell);
    }

    /// <summary>Centers the worldspace overview on the given exterior cell without entering cell-detail mode.</summary>
    public void NavigateToCellInOverview(CellRecord cell)
    {
        EnsureOverviewMode();
        if (!cell.GridX.HasValue || !cell.GridY.HasValue) return;

        var cellCenterX = (cell.GridX.Value + 0.5f) * 4096f;
        var cellCenterY = -(cell.GridY.Value + 0.5f) * 4096f;
        var canvasW = Math.Max((float)MapCanvas.ActualWidth, 800f);
        var canvasH = Math.Max((float)MapCanvas.ActualHeight, 600f);
        _zoom = Math.Min(canvasW, canvasH) / (4096f * 3f);
        _panOffset = new Vector2(canvasW / 2f - cellCenterX * _zoom, canvasH / 2f - cellCenterY * _zoom);
        MapCanvas.Invalidate();
    }

    /// <summary>Selects the worldspace at the given combo index.</summary>
    public void NavigateToWorldspace(int worldspaceIndex)
    {
        if (worldspaceIndex >= 0 && worldspaceIndex < WorldspaceComboBox.Items.Count)
            WorldspaceComboBox.SelectedIndex = worldspaceIndex;
    }

    // View-focus sharing with the 3D viewer ------------------------------------------------------

    /// <summary>
    ///     Captures the current location + selection as a <see cref="WorldViewFocus" /> so the 3D viewer
    ///     can resume in the same area when the user switches views. Exterior: the world XY at the map's
    ///     view center (the 2D map negates Y, so it's stored back in the shared frame). Interior: the
    ///     selected interior cell.
    /// </summary>
    internal WorldViewFocus CaptureViewFocus()
    {
        var selected = _state.SelectedObject;
        if (_state.SelectedCell is { IsInterior: true } interior)
        {
            return new WorldViewFocus(-1, true, interior, 0f, 0f, selected, _cellSortMode);
        }

        var canvasW = Math.Max((float)MapCanvas.ActualWidth, 800f);
        var canvasH = Math.Max((float)MapCanvas.ActualHeight, 600f);
        var center2D = WorldMapViewportHelper.ScreenToWorld(
            new Vector2(canvasW / 2f, canvasH / 2f), _zoom, _panOffset);
        // 2D-map Y is the negative of the shared (3D / PlacedReference) Y.
        return new WorldViewFocus(
            WorldspaceComboBox.SelectedIndex, false, null, center2D.X, -center2D.Y, selected, _cellSortMode);
    }

    /// <summary>
    ///     Resumes the view at a <see cref="WorldViewFocus" /> captured from the 3D viewer: switches to
    ///     the same worldspace, centers the overview on the shared world XY (re-flipping Y), and restores
    ///     the selection + cell-list sort. Interiors load the captured cell.
    /// </summary>
    internal void ApplyViewFocus(WorldViewFocus focus)
    {
        if (_data is null) return;
        ApplyCellSortMode(focus.SortMode);

        if (focus.IsInterior && focus.InteriorCell is { } interior)
        {
            NavigateToCell(interior);
            SelectObject(focus.Selected);
            return;
        }

        // Switching the worldspace combo resets to overview + clears the selection synchronously, so
        // select AFTER. No-op when already on the right worldspace.
        if (focus.WorldspaceComboIndex >= 0 &&
            focus.WorldspaceComboIndex < WorldspaceComboBox.Items.Count &&
            focus.WorldspaceComboIndex != WorldspaceComboBox.SelectedIndex)
        {
            WorldspaceComboBox.SelectedIndex = focus.WorldspaceComboIndex;
        }

        // Shared world XY → 2D-map frame (negate Y), center the overview there (keeping the user's zoom).
        CenterOverviewOnWorld(focus.WorldX, -focus.WorldY);
        SelectObject(focus.Selected);
    }

    /// <summary>Centers the overview on a 2D-map world point, preserving the current zoom.</summary>
    private void CenterOverviewOnWorld(float worldX, float worldY)
    {
        EnsureOverviewMode();
        var canvasW = Math.Max((float)MapCanvas.ActualWidth, 800f);
        var canvasH = Math.Max((float)MapCanvas.ActualHeight, 600f);
        if (_zoom <= 0f) _zoom = 0.05f;
        _panOffset = new Vector2(canvasW / 2f - worldX * _zoom, canvasH / 2f - worldY * _zoom);
        MapCanvas.Invalidate();
    }

    private void ApplyCellSortMode(CellSortMode mode)
    {
        _cellSortMode = mode;
        if (CellSortCombo is not null)
        {
            var index = mode == CellSortMode.ObjectCount ? 1 : 0;
            if (CellSortCombo.SelectedIndex != index) CellSortCombo.SelectedIndex = index;
        }
    }

    /// <summary>Centers and zooms the overview on a placed object, sizing the view to its object bounds.</summary>
    public void NavigateToObjectInOverview(PlacedReference obj)
    {
        EnsureOverviewMode();

        var objCenter = new Vector2(obj.X, -obj.Y);
        float viewRadius = 2048f;
        if (_data?.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds) == true)
        {
            var size = Math.Max(bounds.X2 - bounds.X1, bounds.Y2 - bounds.Y1) * obj.Scale;
            viewRadius = Math.Max(size * 3f, 1024f);
        }

        var canvasW = Math.Max((float)MapCanvas.ActualWidth, 800f);
        var canvasH = Math.Max((float)MapCanvas.ActualHeight, 600f);
        _zoom = Math.Min(canvasW, canvasH) / (viewRadius * 4f);
        _panOffset = new Vector2(canvasW / 2f - objCenter.X * _zoom, canvasH / 2f - objCenter.Y * _zoom);
        _state.SelectObject(obj);
        MapCanvas.Invalidate();
    }

    private void EnsureOverviewMode()
    {
        var wasCellDetail = _state.Mode == ViewMode.CellDetail;
        _state.EnsureOverviewMode();
        if (wasCellDetail)
        {
            DisposeCellDetailBitmaps();
        }

        SetCanvasMode(true);
    }

    // ========================================================================
    // Cell Browser
    // ========================================================================

    private void PopulateCellBrowser()
    {
        if (_data == null) return;
        _allCellItems = WorldMapCellBrowser.BuildCellListItems(_data.AllCells, groupInteriors: true, _data);
        CellSearchBox.Text = "";
        RebuildCellListFromItems(_allCellItems);
    }

    private void PopulateInteriorCellBrowser()
    {
        if (_data == null) return;
        _allCellItems = WorldMapCellBrowser.BuildCellListItems(_data.InteriorCells, groupInteriors: false, _data);
        CellSearchBox.Text = "";
        RebuildCellListFromItems(_allCellItems);
    }

    private void RebuildCellListFromItems(List<CellListItem> items)
    {
        var source = WorldMapCellBrowser.BuildGroupedSource(items, _cellSortMode);
        var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource
        {
            IsSourceGrouped = true, Source = source
        };
        CellListView.ItemsSource = cvs.View;
        HoverInfoText.Text = $"{items.Count} cells";
        ZoomLevelText.Text = "";
        CoordsText.Text = "";
    }

    private void CellListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CellListView.SelectedItem is CellListItem item)
        {
            InspectCell?.Invoke(this, item.Cell);
        }
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private List<CellRecord> GetActiveCells()
    {
        return _state.GetActiveCells();
    }

    private void BuildCellGridLookup()
    {
        if (_data is null)
        {
            _cellGridLookup = null;
            _spatialIndex = null;
            return;
        }

        _spatialIndex = WorldSpatialIndex.Build(
            _data,
            GetActiveCells(),
            _state.FilteredMarkers,
            _state.SelectedWorldspace?.FormId,
            _currentDefaultWaterHeight);
        _cellGridLookup = _spatialIndex.CellsByGrid.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private void ApplyZoomToFitWorldspace()
    {
        WorldMapViewportHelper.ZoomToFitWorldspace(
            GetActiveCells(),
            (float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight,
            out _zoom, out _panOffset);
    }

    /// <summary>
    ///     Called from the draw callback every frame. For the live TerrainTextures overview this is
    ///     deliberately cheap: it does NOT query the spatial index, diff the cache, sort, or dispatch
    ///     a stream inline (all of which scale with visible-cell count and would stutter a zoomed-out
    ///     pan). It only compares a cheaply-computed viewport key and, when it changed, flags a
    ///     rebuild for the throttle timer (<see cref="RebuildTerrainTextureViewport" />). Other layers
    ///     keep the original synchronous single-bitmap build, gated on <see cref="_worldHeightmapDirty" />.
    /// </summary>
    private void EnsureHeightmapBitmap(float canvasW, float canvasH)
    {
        if (_data is null) return;

        var isTerrainTexturesViewport = _state.Mode == ViewMode.WorldOverview &&
            _currentLayer == WorldMapLayer.TerrainTextures;

        if (isTerrainTexturesViewport)
        {
            // Zoomed out far enough that a cell is tiny on screen → use the aggregate LOD (one
            // downscaled whole-worldspace bitmap) instead of streaming the whole worldspace per-cell.
            var screenPxPerCell = _cellSize * _zoom;
            var wantAggregate = screenPxPerCell < TerrainAggregateScreenPxThreshold && !_terrainAggregateUnavailable;
            _terrainTexturesAggregateActive = wantAggregate;

            if (wantAggregate)
            {
                var wsId = _state.SelectedWorldspace?.FormId;
                if (_terrainAggregateBitmap is null || _terrainAggregateWorldspaceFormId != wsId)
                {
                    EnsureTerrainAggregateBuild(wsId);
                }
                return;
            }

            // Per-cell hot path — cheap viewport-key compute only. The quantized key changes when the
            // visible cell-grid bounds (or resolution) change; that's the signal to schedule the
            // expensive rebuild on the timer. A pan smaller than a cell, or a pan back over the
            // same cells, leaves the key unchanged and costs nothing here.
            var key = ComputeTerrainViewportBounds(canvasW, canvasH, out _, out _, out _, out _, out _);
            if (_terrainTextureViewportKey != key)
            {
                _viewportRebuildPending = true;
                EnsureViewportTimerRunning();
            }

            return;
        }

        // Non-TerrainTextures layers build a single aggregate bitmap; per-cell cost is low so the
        // batch path is fine and the work is dirty-gated (rebuilt only on layer/data/toggle change).
        if (!_worldHeightmapDirty) return;

        var activeCells = GetActiveCells();
        if (activeCells.Count == 0) return;

        _worldHeightmapDirty = false;
        _terrainTextureViewportKey = null;

        var version = ++_worldHeightmapBuildVersion;
        ShowLayerBuildStatus($"Building {_currentLayer.DisplayName()}...", busy: true);

        var request = new LayerBuildRequest(
            version,
            activeCells.ToList(),
            _state.SelectedWorldspace,
            _data,
            _currentDefaultWaterHeight,
            _currentColorScheme,
            // ShowWater=false: live world-overview layers render water-FREE; the standalone world water
            // bitmap (_worldWaterBitmap) is the toggle-able water pass. Export keeps baking via
            // WorldMapHeightmapBuilder (a separate path).
            false,
            _currentLayer,
            _cachedGrayscale,
            _cachedWaterMask,
            _cachedHmWidth,
            _cachedHmHeight,
            _data.RenderCache,
            null,
            WorldMapLayerRenderer.TexturePixelsPerCell);

        _ = BuildAndApplyWorldBitmapAsync(request);
    }

    /// <summary>
    ///     The heavy TerrainTextures viewport rebuild, run only from the throttle timer (≤~30×/s)
    ///     instead of every draw frame. Queries the spatial index, diffs the cell-bitmap cache,
    ///     sorts the to-build set urgent-first, and dispatches the background streaming build.
    /// </summary>
    private void RebuildTerrainTextureViewport(float canvasW, float canvasH)
    {
        if (_data is null) return;
        if (_state.Mode != ViewMode.WorldOverview || _currentLayer != WorldMapLayer.TerrainTextures) return;

        var activeCells = GetActiveCells();
        if (activeCells.Count == 0) return;

        var viewportRequest = BuildTerrainTextureViewportRequest(canvasW, canvasH, activeCells);
        var requestCells = viewportRequest.Cells;
        var texturePixelsPerCell = viewportRequest.PixelsPerCell;
        var terrainTextureKey = viewportRequest.Key;
        var viewportCellCount = viewportRequest.Cells.Count;

        // An empty request means the viewport is entirely off the populated cell region (mid
        // pan-stress sweep, or scrolled past the worldspace edge). That's "no viewport signal",
        // not a real resize: keep the cap and the cache as-is (the pan-back history is exactly
        // what the user is about to scroll back onto), don't cancel the trailing stream, and
        // don't dispatch a junk build.
        if (viewportCellCount == 0)
        {
            _terrainTextureViewportKey = terrainTextureKey;
            // Off-region time must not count against the cap's hold window, or a >holdMs
            // excursion would decay the cap on the first partial re-entry frame and mass-evict
            // the very pan-back history the hold protects.
            _layerCellBitmapCapPolicy.Touch(Environment.TickCount64);
            Map2DProfilerTrace.Event("viewport-rebuild",
                $"layer={_currentLayer} ppc={texturePixelsPerCell} viewport=0 cap={_layerCellBitmapCap} cacheSize={_layerCellBitmaps?.Count ?? 0} toBuild=0 kind=empty");
            HideLayerBuildStatus();
            MapCanvas.Invalidate();
            return;
        }

        // Scale the in-stream LRU cap to the current viewport BEFORE the stream starts.
        // Otherwise, with a large viewport (~6000 cells for zoomed-out WastelandNV), the
        // 256-entry default would evict cells we just rendered, in the same stream, before
        // the user ever sees them — "Loading" goes away with most of the view still blank.
        //
        // ×3 budget: 1× current viewport + 1× zoom-transition stand-ins at a stale resolution
        // + 1× pan-back history (cells the user just scrolled off-screen). The 1× pan slot
        // is what makes "pan off, pan back" a cache hit instead of a re-render — see the
        // LRU touch below. The policy adds shrink hysteresis: a transiently collapsed request
        // (sweep exiting the populated region) must not drag the cap down and mass-evict that
        // pan-back history.
        _layerCellBitmapCap = _layerCellBitmapCapPolicy.Update(viewportCellCount, Environment.TickCount64);

        // Byte clamp on top of the count cap: a tile is texturePixelsPerCell² × 4 bytes (4.5 MB at the
        // 1056 tier), so the count-only cap could retain >1 GB of GPU textures while panning zoomed in.
        // Clamp so count × current-tile-bytes stays under the resident-byte ceiling — auto-shrinks the
        // cap to ~85 at 1056 and leaves it at the full count for the small tiers (where bytes are tiny).
        var tileBytes = (long)texturePixelsPerCell * texturePixelsPerCell * 4;
        if (tileBytes > 0)
        {
            var byteCap = (int)Math.Min(int.MaxValue, Math.Max(MinLayerCellBitmapCap, MaxLayerCellResidentBytes / tileBytes));
            _layerCellBitmapCap = Math.Min(_layerCellBitmapCap, byteCap);
        }

        // Incremental rebuild for the TerrainTextures live view: keep already-built cell
        // bitmaps inside the new viewport, evict cells that scrolled off, and dispatch a
        // build only for the cells we don't have at the target resolution yet. With the
        // multi-resolution cache, old-resolution bitmaps remain as visual stand-ins
        // while target-resolution ones build — no blank during zoom transitions.
        var canReuseCache = _layerCellBitmaps is not null
            && _layerCellBitmapsLayer == _currentLayer;

        if (canReuseCache)
        {
            // Single pass over the viewport request (not the whole cache): for each requested
            // cell, either it's already cached at the target resolution (drop from the build set,
            // and LRU-touch it so it doesn't evict first) or it needs building. This replaces the
            // old full-cache `.ToList()` copy + HashSet + Remove/Add loop + `.Where().ToList()`,
            // which were all O(cache size) and ran on the UI thread (per the throttle, ≤~30×/s).
            //
            // LRU touch (Remove-then-Add to relocate to the MRU tail; the OrderedDictionary indexer
            // setter replaces IN PLACE and would NOT move the entry) only matters when the cache is
            // over cap and eviction can actually fire — `underPressure` skips the array-shuffling in
            // the common steady-state pan.
            var underPressure = _layerCellBitmaps!.Count > _layerCellBitmapCap;
            var toBuild = new List<CellRecord>(requestCells.Count);
            foreach (var c in requestCells)
            {
                if (c.GridX is not int gx || c.GridY is not int gy) continue;
                var key = (gx, gy, texturePixelsPerCell);
                if (_layerCellBitmaps.TryGetValue(key, out var bmp))
                {
                    if (underPressure)
                    {
                        _layerCellBitmaps.Remove(key);
                        _layerCellBitmaps.Add(key, bmp);
                    }
                }
                else if (!_inFlightCellKeys.ContainsKey(key))
                {
                    // Not cached AND not already queued/decoding — schedule it. Excluding in-flight
                    // keys stops successive rebuilds during a pan from re-requesting the same cells
                    // (which produced the ~3× re-decode/re-upload churn at far zoom).
                    toBuild.Add(c);
                }
            }

            requestCells = toBuild;
            _terrainTextureViewportKey = terrainTextureKey;

            Map2DProfilerTrace.Event("viewport-rebuild",
                $"layer={_currentLayer} ppc={texturePixelsPerCell} viewport={viewportCellCount} cap={_layerCellBitmapCap} cacheSize={_layerCellBitmaps.Count} toBuild={requestCells.Count}");

            if (requestCells.Count == 0)
            {
                // Viewport changed but no new cells need building — common when panning
                // by less than a cell or when reversing direction back over cached cells.
                HideLayerBuildStatus();
                MapCanvas.Invalidate();
                return;
            }
        }
        else
        {
            // Layer / resolution / worldspace changed: replace the whole cache.
            _terrainTextureViewportKey = terrainTextureKey;
            Map2DProfilerTrace.Event("viewport-rebuild",
                $"layer={_currentLayer} ppc={texturePixelsPerCell} viewport={viewportCellCount} cap={_layerCellBitmapCap} cacheSize=0 toBuild={requestCells.Count} kind=full");
        }

        if (LandscapeTexturePalette.GetOrCreate(_data) is not { } palette || requestCells.Count == 0)
        {
            return;
        }

        var version = ++_worldHeightmapBuildVersion;
        ShowLayerBuildStatus($"Building {_currentLayer.DisplayName()}...", busy: true);

        // Urgent-first ordering: sort by squared distance from viewport center so
        // the parallel worker pool picks central cells before margin/preload cells.
        // Cesium's Preload priority tier — Urgent (visible) drains before Normal/Preload.
        var (vpTl, vpBr) = WorldMapViewportHelper.GetVisibleWorldBounds(canvasW, canvasH, _zoom, _panOffset);
        var centerX = (vpTl.X + vpBr.X) * 0.5f;
        var centerY = (vpTl.Y + vpBr.Y) * 0.5f;
        requestCells.Sort((a, b) =>
        {
            var ax = (a.GridX!.Value + 0.5f) * _cellSize - centerX;
            var ay = -(a.GridY!.Value + 0.5f) * _cellSize - centerY;
            var bx = (b.GridX!.Value + 0.5f) * _cellSize - centerX;
            var by = -(b.GridY!.Value + 0.5f) * _cellSize - centerY;
            return (ax * ax + ay * ay).CompareTo(bx * bx + by * by);
        });

        // Cancel-only; the old stream disposes its own CTS once it unwinds (see the disposal
        // hop in StreamAndApplyTerrainTexturesAsync) — disposing here raced its registrations.
        _terrainStreamCts?.Cancel();
        var streamCts = new CancellationTokenSource();
        _terrainStreamCts = streamCts;
        // Capture cache-gen on the UI thread BEFORE the stream starts. Worker threads read
        // this value when enqueuing apply tasks; if cache-gen bumps after the stream begins
        // (worldspace switch, layer change), the captured generation goes stale and the
        // applies drop on the ApplyOne side. Per-stream `version` is kept for status-banner
        // messages but no longer controls apply-drop — pan-induced rebuilds are common
        // and would needlessly kill in-flight applies whose cache target is still valid.
        var cacheGen = _layerCellBitmapsCacheGen;
        Map2DProfilerTrace.Event("stream-start",
            $"v={version} cacheGen={cacheGen} ppc={texturePixelsPerCell} requested={requestCells.Count}");
        Interlocked.Increment(ref _activeTerrainStreams);
        _ = StreamAndApplyTerrainTexturesAsync(
            version, cacheGen, requestCells, palette, texturePixelsPerCell, streamCts);
    }

    /// <summary>Lazily creates and starts the viewport-rebuild throttle timer (UI thread only).</summary>
    private void EnsureViewportTimerRunning()
    {
        if (_viewportRebuildTimer is null)
        {
            _viewportRebuildTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ViewportRebuildThrottleMs)
            };
            _viewportRebuildTimer.Tick += ViewportRebuildTimer_Tick;
        }

        if (!_viewportRebuildTimer.IsEnabled) _viewportRebuildTimer.Start();
    }

    private void StopViewportTimer() => _viewportRebuildTimer?.Stop();

    /// <summary>
    ///     Cheap per-draw check: when the rendered-models overlay is on, detect whether the view
    ///     context (mode / cell / worldspace / disabled filter / eligibility) or the visible bounds
    ///     have changed enough to warrant a fresh top-down render, and if so flag a request for the
    ///     throttle timer. A context change also drops the stale overlay (its world coords no longer
    ///     apply). Mirrors the terrain viewport-key approach — no heavy work on the draw path.
    /// </summary>
    private void MaybeScheduleTopDownRequest(float canvasW, float canvasH)
    {
        if (!_showRenderedObjects || _topDownProvider?.CanRenderTopDown != true) return;

        var eligible = IsTopDownEligible();
        var ctx = (_state.Mode, _state.SelectedCell?.FormId ?? 0u,
            _state.SelectedWorldspace?.FormId ?? 0u, _hideDisabledActors, eligible);
        if (!ctx.Equals(_topDownContext))
        {
            _topDownContext = ctx;
            _topDownBoundsKey = null;
            // Context changed — the old overlay's world coords no longer match the current view.
            DisposeTopDownOverlay();
            if (eligible)
            {
                _topDownRequestPending = true;
                EnsureViewportTimerRunning();
            }
            return;
        }

        if (!eligible) return;

        var (tl, br) = WorldMapViewportHelper.GetVisibleWorldBounds(canvasW, canvasH, _zoom, _panOffset);
        // Quantum ≈ 128 screen px in world units: re-request after a pan of that much. Plus a zoom
        // bucket so a zoom change also refreshes. The 25% render margin (> the quantum) keeps the
        // edges covered between refreshes.
        var q = 128f / MathF.Max(_zoom, 1e-6f);
        var boundsKey = (
            (int)MathF.Round(MathF.Min(tl.X, br.X) / q),
            (int)MathF.Round(MathF.Min(tl.Y, br.Y) / q),
            (int)MathF.Round(MathF.Max(tl.X, br.X) / q),
            (int)MathF.Round(MathF.Max(tl.Y, br.Y) / q),
            (int)MathF.Round(MathF.Log2(MathF.Max(_zoom, 1e-6f)) * 8f));
        if (!boundsKey.Equals(_topDownBoundsKey))
        {
            _topDownBoundsKey = boundsKey;
            _topDownRequestPending = true;
            EnsureViewportTimerRunning();
        }
    }

    /// <summary>
    ///     Drops the cached "Rendered models" overlay and schedules a fresh render. Called when the
    ///     legend's category filter changes (the overlay shares that filter via RenderTopDownAsync, but
    ///     the per-draw request key doesn't track categories, so a toggle wouldn't otherwise refresh it).
    /// </summary>
    private void InvalidateTopDownOverlay()
    {
        if (!_showRenderedObjects || _topDownProvider?.CanRenderTopDown != true) return;
        DisposeTopDownOverlay();
        _topDownBoundsKey = null;
        if (IsTopDownEligible())
        {
            _topDownRequestPending = true;
            EnsureViewportTimerRunning();
        }
    }

    /// <summary>
    ///     Requests a top-down render of the visible world rect (+ a margin) from the 3D provider,
    ///     uploads the BGRA readback as a <see cref="CanvasBitmap" />, and stores it as the overlay.
    ///     Runs on the UI thread (the provider records D3D12 on the device thread and offloads only
    ///     the readback). Single-flighted via <see cref="_topDownInFlight" />; stale results (after a
    ///     teardown that bumped <see cref="_topDownGen" />) are discarded.
    /// </summary>
    private async Task RequestTopDownOverlayAsync()
    {
        var provider = _topDownProvider;
        if (provider is null || _data is null) return;
        var canvasW = (float)MapCanvas.ActualWidth;
        var canvasH = (float)MapCanvas.ActualHeight;
        if (canvasW < 1f || canvasH < 1f) return;

        _topDownInFlight = true;
        var gen = ++_topDownGen;
        CancellationToken ct;
        try
        {
            _topDownCts?.Dispose();
            _topDownCts = new CancellationTokenSource();
            ct = _topDownCts.Token;
        }
        catch (ObjectDisposedException)
        {
            _topDownInFlight = false;
            return;
        }

        try
        {
            // Visible bounds are in map space (X = world X, Y = -worldNorthY). Add a margin, then
            // convert to world north-Y for the renderer.
            var (tl, br) = WorldMapViewportHelper.GetVisibleWorldBounds(canvasW, canvasH, _zoom, _panOffset);
            var mapMinX = MathF.Min(tl.X, br.X);
            var mapMaxX = MathF.Max(tl.X, br.X);
            var mapMinY = MathF.Min(tl.Y, br.Y);
            var mapMaxY = MathF.Max(tl.Y, br.Y);
            var marginX = (mapMaxX - mapMinX) * TopDownMarginFraction;
            var marginY = (mapMaxY - mapMinY) * TopDownMarginFraction;
            mapMinX -= marginX; mapMaxX += marginX;
            mapMinY -= marginY; mapMaxY += marginY;

            var worldMinX = mapMinX;
            var worldMaxX = mapMaxX;
            var worldMinY = -mapMaxY; // map Y = -worldNorthY
            var worldMaxY = -mapMinY;
            var pxW = (int)MathF.Round(canvasW * (1f + 2f * TopDownMarginFraction));
            var pxH = (int)MathF.Round(canvasH * (1f + 2f * TopDownMarginFraction));

            var render = await provider.RenderTopDownAsync(
                worldMinX, worldMaxX, worldMinY, worldMaxY, pxW, pxH,
                showDisabled: !_hideDisabledActors,
                showWater: _showWater,
                worldspaceFormId: _state.SelectedWorldspace?.FormId,
                // Apply the legend's category filter to the rendered-models overlay so category
                // toggles drive the 3D meshes too (snapshot — the render awaits off the UI thread).
                hiddenCategories: new HashSet<PlacedObjectCategory>(_hiddenCategories),
                ct);

            if (gen != _topDownGen) return; // superseded (teardown / toggle off)
            if (render is null) { _topDownIncomplete = false; return; }

            var bmp = CanvasBitmap.CreateFromBytes(
                MapCanvas, render.Bgra, render.Width, render.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            _topDownOverlay?.Dispose();
            _topDownOverlay = bmp;
            _topDownWorldMinX = render.WorldMinX;
            _topDownWorldMaxX = render.WorldMaxX;
            _topDownWorldMinY = render.WorldMinY;
            _topDownWorldMaxY = render.WorldMaxY;
            _topDownIncomplete = !render.IsComplete;

            if (_dumpTopDown && !_topDownDumpWritten)
            {
                _topDownDumpWritten = true;
                try
                {
                    var path = Path.Combine(Path.GetTempPath(), "fallout-map2d-topdown.png");
                    await bmp.SaveAsync(path, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
                    Map2DProfilerTrace.Event("topdown-dump", path);
                }
                catch (Exception ex) { Map2DProfilerTrace.Event("topdown-dump-error", ex.Message); }
            }

            MapCanvas.Invalidate();
        }
        catch (OperationCanceledException) { /* request superseded by a newer top-down render — expected */ }
        catch (Exception ex)
        {
            Map2DProfilerTrace.Event("topdown-error", ex.Message);
        }
        finally
        {
            _topDownInFlight = false;
            // While the render reported streaming still in flight, keep the viewport timer alive so it
            // re-issues the next render. Single-flight + the timer ticking faster than a render
            // completes already makes these effectively back-to-back; the real load-speed lever is the
            // unthrottled per-render mesh budget (see WorldView3DControl.RenderTopDownAsync), which lets
            // each render drain the whole decode backlog — so cadence is not the bottleneck.
            if (_topDownIncomplete) EnsureViewportTimerRunning();
        }
    }

    /// <summary>
    ///     Single timer tick: drain a bounded batch of completed cell bitmaps onto the GPU, then run
    ///     at most one throttled viewport rebuild. Self-stops when there's nothing left to do so we
    ///     don't keep a ~30 Hz wakeup alive forever.
    /// </summary>
    private void ViewportRebuildTimer_Tick(object? sender, object e)
    {
        // After unload the DispatcherQueue goes null; there's no UI left to update.
        if (DispatcherQueue is null)
        {
            StopViewportTimer();
            return;
        }

        // Diagnostic: this tick runs on the UI thread, so a long tick IS a visible stall/freeze.
        // Time it and emit a `slow-tick` trace when it exceeds the budget — unambiguous (unlike a
        // draw-gap, which idle pauses also produce) for attributing a freeze to the streaming path.
        var tickStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // Same rationale as MapCanvas_Draw: a throw here (e.g. a per-cell GPU upload in
        // DrainPendingCellApplies, or a viewport rebuild) otherwise escapes to App.UnhandledException
        // and kills the window. Log with context + skip this tick; the timer fires again next interval.
        try
        {
            DrainPendingCellApplies();
            ApplyKeyboardPan();

            // While a zoom gesture is settling, hold off the rebuild (keep pending set) so we don't
            // re-stream throwaway intermediate zoom levels. Each wheel notch re-arms _zoomSettleTicks.
            if (_zoomSettleTicks > 0)
            {
                _zoomSettleTicks--;
            }
            else if (_viewportRebuildPending
                && _state.Mode == ViewMode.WorldOverview
                && _currentLayer == WorldMapLayer.TerrainTextures)
            {
                _viewportRebuildPending = false;
                RebuildTerrainTextureViewport((float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight);
            }

            // Rendered-models overlay: request a fresh top-down render once the view settles (not mid
            // pan/zoom), or keep re-requesting while the last render was still streaming in. Gated to
            // TopDownMinIntervalMs so the incomplete-streaming re-request loop fires at a throttled
            // cadence (~3/sec) instead of every ~30 Hz tick — each request is a full scene render +
            // readback + CanvasBitmap upload, which otherwise starves pan/zoom (bug: overlay crawl).
            if (_showRenderedObjects
                && _zoomSettleTicks == 0
                && !_isPanning
                && !_topDownInFlight
                && _topDownProvider?.CanRenderTopDown == true
                && (_topDownRequestPending || _topDownIncomplete)
                && IsTopDownEligible()
                && Environment.TickCount64 - _topDownLastRequestTick >= TopDownMinIntervalMs)
            {
                _topDownRequestPending = false;
                _topDownLastRequestTick = Environment.TickCount64;
                _ = RequestTopDownOverlayAsync();
            }
        }
        catch (Exception ex)
        {
            LogUiThreadFault("ViewportRebuildTimer_Tick", ex);
        }

        var tickMs = System.Diagnostics.Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;
        if (tickMs >= 40)
        {
            Map2DProfilerTrace.Event("slow-tick",
                $"ms={tickMs:F0} layer={_currentLayer} cacheSize={_layerCellBitmaps?.Count ?? 0} cap={_layerCellBitmapCap} ppc-tier={ChooseTerrainTexturePixelsPerCell(_zoom)}");
        }

        // Idle: no pending rebuild, not zoom-settling, not actively panning, nothing left to apply,
        // no stream still producing cells, and no top-down overlay work outstanding. Stop until the
        // next viewport change / pointer event.
        if (!_viewportRebuildPending
            && _zoomSettleTicks == 0
            && !_isPanning
            && _panKeysDown.Count == 0
            && _pendingCellApplies.IsEmpty
            && Volatile.Read(ref _activeTerrainStreams) == 0
            && !(_showRenderedObjects && (_topDownInFlight || _topDownRequestPending || _topDownIncomplete)))
        {
            StopViewportTimer();
        }
    }

    /// <summary>
    ///     Drains completed cells from <see cref="_pendingCellApplies" />. Genuine GPU uploads are
    ///     capped at <see cref="MaxCellAppliesPerTick" /> per tick so a burst can't starve input;
    ///     but cells that are stale (cache replaced) or ALREADY present at the target resolution are
    ///     skipped cheaply and do NOT count against the budget. Skipping already-cached cells is what
    ///     stops the "constant refresh" churn: overlapping streams re-produce cells the viewport
    ///     already shows, and re-uploading them (dispose + CreateFromBytes + invalidate) every frame
    ///     was the wasted work observed after the image was already complete. One coalesced
    ///     invalidate at the end, only if something new was actually uploaded.
    /// </summary>
    private void DrainPendingCellApplies()
    {
        var uploads = 0;
        long uploadBytes = 0;
        var applied = false;
        while (_pendingCellApplies.TryDequeue(out var item))
        {
            var key = (item.cell.GridX, item.cell.GridY, item.cell.PixelsPerCell);
            _inFlightCellKeys.TryRemove(key, out _);

            // Stale generation (layer/worldspace switch) — drop cheaply.
            if (item.cacheGen != _layerCellBitmapsCacheGen) continue;

            // Already displayed at this resolution — the bitmap would be byte-identical, so skip the
            // redundant dispose+upload+invalidate. Cheap skip, uncapped, so a large duplicate backlog
            // clears in one tick and the timer can idle instead of looping.
            if (_layerCellBitmaps is not null && _layerCellBitmaps.ContainsKey(key)) continue;

            ApplyOneTerrainTextureCell(item.cacheGen, item.cell);
            applied = true;
            // Stop on EITHER the count cap OR the byte budget. Without the byte budget a tick of big
            // (1056²≈4.5 MB) cells dumps >200 MB of synchronous GPU upload and freezes the UI on a zoom
            // transition; the byte check paces large tiers to ~5 cells/tick while small tiers still drain
            // 48/tick. The just-applied cell always counts, so progress is guaranteed (no starvation).
            var ppc = item.cell.PixelsPerCell;
            uploadBytes += (long)ppc * ppc * 4;
            if (item.cell.Water is not null) uploadBytes += (long)ppc * ppc * 4;
            if (++uploads >= MaxCellAppliesPerTick || uploadBytes >= MaxCellApplyBytesPerTick) break;
        }

        if (applied) MapCanvas.Invalidate();
    }

    /// <summary>
    ///     Consumes the per-cell streaming output from
    ///     <see cref="WorldMapLayerRenderer.StreamTerrainTexturesPerCell" /> and buffers each
    ///     completed cell into <see cref="_pendingCellApplies" />. The throttle timer drains the
    ///     buffer at a bounded rate (<see cref="DrainPendingCellApplies" />), so a burst of
    ///     thousands of completed cells can't flood the UI thread with GPU uploads and starve
    ///     pointer input. Cells still appear progressively, just paced.
    /// </summary>
    private async Task StreamAndApplyTerrainTexturesAsync(
        int version,
        int cacheGen,
        List<CellRecord> requestCells,
        LandscapeTexturePalette palette,
        int pixelsPerCell,
        CancellationTokenSource streamCts)
    {
        var ct = streamCts.Token;
        var requestedCount = requestCells.Count;
        var producedCount = 0;
        try
        {
            await foreach (var cell in WorldMapLayerRenderer.StreamTerrainTexturesPerCell(
                    requestCells,
                    palette,
                    _currentDefaultWaterHeight,
                    _data?.RenderCache,
                    pixelsPerCell,
                    _currentWaterPalette,
                    ct).WithCancellation(ct).ConfigureAwait(false))
            {
                producedCount++;
                // Buffer for the throttle timer to drain on the UI thread (bounded per tick).
                // Enqueue is lock-free and never blocks the worker; the captured cacheGen lets a
                // stale apply (from before a cache replacement) be dropped at drain time. Mark the
                // key in-flight so concurrent rebuilds don't re-request a cell already queued here.
                _inFlightCellKeys[(cell.GridX, cell.GridY, cell.PixelsPerCell)] = 0;
                _pendingCellApplies.Enqueue((cacheGen, cell));
            }

            BethesdaMultitool.Core.Logger.Instance.Info(
                "TerrainTextures stream v{0} complete: requested={1}, produced={2}, skipped-by-worker={3}",
                version, requestedCount, producedCount, requestedCount - producedCount);

            Map2DProfilerTrace.Event("stream-end",
                $"v={version} cacheGen={cacheGen} requested={requestedCount} produced={producedCount}");

            var doneQueue = DispatcherQueue;
            doneQueue?.TryEnqueue(() =>
            {
                if (version == _worldHeightmapBuildVersion) HideLayerBuildStatus();
            });
        }
        catch (OperationCanceledException)
        {
            // Expected on viewport-change cancellation. Drop silently.
        }
        catch (Exception ex)
        {
            // Log the full exception (including AggregateException inner) to the diagnostic
            // file. The visible banner has limited width and can clip long stack traces.
            BethesdaMultitool.Core.Logger.Instance.Warn(
                "TerrainTextures streaming failed: {0}", ex.ToString());
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (version != _worldHeightmapBuildVersion) return;
                ShowLayerBuildStatus(
                    $"{WorldMapLayer.TerrainTextures.DisplayName()} failed: {ex.Message}",
                    busy: false);
                MapCanvas.Invalidate();
            });
        }
        finally
        {
            Interlocked.Decrement(ref _activeTerrainStreams);

            // The stream owns its CTS: dispose it on the UI thread AFTER all awaited work has
            // completed (no token registrations remain). The UI side only ever calls Cancel() —
            // disposing inline there raced the live registrations on these worker threads
            // (CancellationTokenSource.Dispose is not safe concurrent with Register/Cancel).
            // The UI hop serializes the dispose against the UI's Cancel, and clearing the field
            // first means CancelTerrainStream can never see a disposed CTS.
            var doneDispatcher = DispatcherQueue;
            if (doneDispatcher is null || !doneDispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_terrainStreamCts, streamCts))
                    {
                        _terrainStreamCts = null;
                    }

                    streamCts.Dispose();
                }))
            {
                // Dispatcher gone (control torn down): no UI thread can Cancel anymore either,
                // so disposing here is race-free.
                streamCts.Dispose();
            }
        }
    }

    /// <summary>
    ///     UI-thread per-cell apply: turn the streamed RGBA bytes into a device-bound
    ///     <see cref="CanvasBitmap" />, slot it into the cell-bitmap cache (overwriting any
    ///     prior bitmap at the same key), and invalidate the canvas. Win2D coalesces
    ///     back-to-back invalidations so this scales to streaming many cells per frame.
    ///     <para>
    ///         Drop check uses <see cref="_layerCellBitmapsCacheGen" /> (bumped only on cache
    ///         replacement) instead of <see cref="_worldHeightmapBuildVersion" /> (bumped per
    ///         viewport rebuild). During a fast pan, the per-stream version increments on
    ///         every viewport change, and old applies queued behind newer ones would all drop
    ///         their work even though the cache they were targeting is still alive and valid.
    ///         Cache-gen only fires the drop when an apply would actually corrupt the cache
    ///         (different layer / cleared dict / worldspace switch).
    ///     </para>
    /// </summary>
    private void ApplyOneTerrainTextureCell(int cacheGen, WorldMapLayerRenderer.TerrainTextureCellResult cell)
    {
        if (cacheGen != _layerCellBitmapsCacheGen)
        {
            Map2DProfilerTrace.Event("cache-drop-by-gen",
                $"cell=({cell.GridX},{cell.GridY},{cell.PixelsPerCell}) applyGen={cacheGen} currentGen={_layerCellBitmapsCacheGen}");
            return;
        }

        var key = (cell.GridX, cell.GridY, cell.PixelsPerCell);
        _layerCellBitmaps ??= new OrderedDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>();
        var replacedStale = _layerCellBitmaps.TryGetValue(key, out var stale);
        if (replacedStale)
        {
            stale!.Dispose();
            // Remove so the re-add lands at the tail (LRU MRU end). The indexer setter on
            // OrderedDictionary preserves position and would leave a replaced entry near the
            // dict head, where it would be evicted first instead of last.
            _layerCellBitmaps.Remove(key);
        }
        _layerCellBitmaps.Add(key, CanvasBitmap.CreateFromBytes(
            MapCanvas,
            cell.Pixels,
            cell.PixelsPerCell,
            cell.PixelsPerCell,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized));
        _layerCellBitmapsLayer = WorldMapLayer.TerrainTextures;

        // Water tile travels in lockstep with the terrain tile under the SAME key (it is a subset —
        // dry cells carry no tile). Premultiplied bytes → default alpha mode, so source-over over the
        // water-free terrain reproduces the old baked overlay exactly.
        if (_layerWaterCellBitmaps is not null && _layerWaterCellBitmaps.TryGetValue(key, out var staleWater))
        {
            staleWater.Dispose();
            _layerWaterCellBitmaps.Remove(key);
        }
        if (cell.Water is { } waterBytes)
        {
            _layerWaterCellBitmaps ??= new OrderedDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>();
            _layerWaterCellBitmaps.Add(key, CanvasBitmap.CreateFromBytes(
                MapCanvas,
                waterBytes,
                cell.PixelsPerCell,
                cell.PixelsPerCell,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized));
        }

        Map2DProfilerTrace.Event("cache-add",
            $"cell=({cell.GridX},{cell.GridY},{cell.PixelsPerCell}) replaced={replacedStale} dictSize={_layerCellBitmaps.Count} cap={_layerCellBitmapCap}");

        // Memory cap. Once we exceed the soft limit, drop the LRU head — guaranteed to be the
        // genuinely oldest entry because OrderedDictionary preserves insertion order strictly
        // (see _layerCellBitmaps doc-comment for the pre-2026-06-05 Dictionary bug this fixed).
        // viewport-leave already evicts most of the stale ones in EnsureHeightmapBitmap; this
        // cap is the safety net for resolutions retained across zoom thresholds plus the
        // viewport×3 budget for pan-back stand-ins.
        while (_layerCellBitmaps.Count > _layerCellBitmapCap)
        {
            var oldest = _layerCellBitmaps.GetAt(0);
            oldest.Value.Dispose();
            _layerCellBitmaps.RemoveAt(0);
            // Evict the water tile for the same key (if any) so the water cache can never outgrow the
            // terrain cache — it's a subset keyed identically.
            if (_layerWaterCellBitmaps is not null && _layerWaterCellBitmaps.TryGetValue(oldest.Key, out var evictWater))
            {
                evictWater.Dispose();
                _layerWaterCellBitmaps.Remove(oldest.Key);
            }
            Map2DProfilerTrace.Event("cache-evict",
                $"cell=({oldest.Key.gx},{oldest.Key.gy},{oldest.Key.pixelsPerCell}) reason=cap dictSize={_layerCellBitmaps.Count} cap={_layerCellBitmapCap}");
        }

        // Canvas invalidation is hoisted to DrainPendingCellApplies (one per drained batch)
        // so a 48-cell tick coalesces into a single redraw instead of 48.
    }

    /// <summary>
    ///     Computes the quantized TerrainTextures viewport key and its world-space bounds (with the
    ///     pan-direction preload bias) WITHOUT touching the spatial index. Shared by the cheap
    ///     per-frame key compare in <see cref="EnsureHeightmapBitmap" /> and the heavy
    ///     <see cref="BuildTerrainTextureViewportRequest" /> so both derive the key from identical
    ///     math — if they diverged, rebuilds would fire spuriously or never.
    /// </summary>
    private TerrainTextureViewportKey ComputeTerrainViewportBounds(
        float canvasW, float canvasH,
        out float minCanvasX, out float maxCanvasX, out float minCanvasY, out float maxCanvasY,
        out int pixelsPerCell)
    {
        pixelsPerCell = ChooseTerrainTexturePixelsPerCell(_zoom);
        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasW, canvasH, _zoom, _panOffset);

        // Preload-direction bias. Cesium "Preload" priority tier: extend the viewport
        // margin in the direction of pan motion so cells about to scroll into view are
        // already decoded by the time they arrive. Screen-space pan velocity is positive
        // when the user drags the map content right/down, which means new content is
        // entering from the LEFT/TOP — extend the margin on the opposite side from velocity.
        // World Y is inverted from screen Y, so the Y bias gets flipped too.
        const int baseMarginCells = 1;
        const int preloadMarginCells = 3;
        const float velocityThresholdPx = 6f;
        var marginWorld = _cellSize * baseMarginCells;
        var preloadWorld = _cellSize * preloadMarginCells;

        var biasLeft = _panVelocity.X > velocityThresholdPx ? preloadWorld : marginWorld;
        var biasRight = _panVelocity.X < -velocityThresholdPx ? preloadWorld : marginWorld;
        var biasTopWorld = _panVelocity.Y > velocityThresholdPx ? preloadWorld : marginWorld;
        var biasBottomWorld = _panVelocity.Y < -velocityThresholdPx ? preloadWorld : marginWorld;

        minCanvasX = Math.Min(tlWorld.X, brWorld.X) - biasLeft;
        maxCanvasX = Math.Max(tlWorld.X, brWorld.X) + biasRight;
        // World Y grows northward, so the screen-top edge is the WORLD MAX-Y and screen-bottom is WORLD MIN-Y.
        minCanvasY = Math.Min(tlWorld.Y, brWorld.Y) - biasBottomWorld;
        maxCanvasY = Math.Max(tlWorld.Y, brWorld.Y) + biasTopWorld;

        var minGx = (int)MathF.Floor(minCanvasX / _cellSize);
        var maxGx = (int)MathF.Floor(maxCanvasX / _cellSize);
        var minGy = (int)MathF.Floor(-maxCanvasY / _cellSize);
        var maxGy = (int)MathF.Floor(-minCanvasY / _cellSize);
        return new TerrainTextureViewportKey(minGx, maxGx, minGy, maxGy, pixelsPerCell);
    }

    private TerrainTextureViewportRequest BuildTerrainTextureViewportRequest(
        float canvasW, float canvasH, List<CellRecord> activeCells)
    {
        var key = ComputeTerrainViewportBounds(
            canvasW, canvasH,
            out var minCanvasX, out var maxCanvasX, out var minCanvasY, out var maxCanvasY,
            out var pixelsPerCell);

        var visibleCells = new List<CellRecord>();
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryCellsInViewport(
                new Vector2(minCanvasX, minCanvasY),
                new Vector2(maxCanvasX, maxCanvasY),
                visibleCells);
        }
        else
        {
            foreach (var cell in activeCells)
            {
                if (cell.GridX is not int gx || cell.GridY is not int gy)
                {
                    continue;
                }

                if (gx >= key.MinGx && gx <= key.MaxGx && gy >= key.MinGy && gy <= key.MaxGy)
                {
                    visibleCells.Add(cell);
                }
            }
        }

        // An empty result is a valid signal (viewport entirely off the populated region);
        // RebuildTerrainTextureViewport treats it as "no viewport" and keeps cap/cache/stream
        // untouched. The old Take(1) fallback here dispatched a junk 1-cell off-screen build
        // and canceled the live stream at every pan-stress sweep boundary.
        return new TerrainTextureViewportRequest(key, visibleCells, pixelsPerCell);
    }

    private int ChooseTerrainTexturePixelsPerCell(float zoom)
    {
        var screenPixelsPerCell = _cellSize * zoom;

        // Low tiers (33/66) for the aggregate→per-cell transition zone: rendering cells at ~display
        // resolution keeps minification to ≤~1.4× (vs ~5× at a fixed 132), which — with the
        // HighQualityCubic composite — removes the moire there, and is cheaper (smaller tiles). The
        // 132/264/528 upper tiers (zoomed-in detail) are unchanged.
        if (screenPixelsPerCell <= WorldMapLayerRenderer.HeightmapPixelsPerCell)
        {
            return WorldMapLayerRenderer.HeightmapPixelsPerCell; // 33
        }

        if (screenPixelsPerCell <= WorldMapLayerRenderer.HeightmapPixelsPerCell * 2)
        {
            return WorldMapLayerRenderer.HeightmapPixelsPerCell * 2; // 66
        }

        if (screenPixelsPerCell <= WorldMapLayerRenderer.TexturePixelsPerCell * 1.25f)
        {
            return WorldMapLayerRenderer.TexturePixelsPerCell; // 132
        }

        if (screenPixelsPerCell <= WorldMapLayerRenderer.TexturePixelsPerCell * 2.5f)
        {
            return WorldMapLayerRenderer.TexturePixelsPerCell * 2; // 264
        }

        if (screenPixelsPerCell <= WorldMapLayerRenderer.TexturePixelsPerCell * 5f)
        {
            return WorldMapLayerRenderer.TexturePixelsPerCell * 4; // 528
        }

        // Zoomed in past ~660 screen px/cell: render at 1056 so the composite carries ~132 px per
        // tile-repeat (sampled from the 256 mip, downscaled) instead of magnifying the 528 tier. Few
        // cells are visible at this zoom, so the larger per-cell bitmaps stay memory-bounded.
        return WorldMapLayerRenderer.MaxTexturePixelsPerCell; // 1056
    }

    /// <summary>
    ///     Kicks a background build of the whole-worldspace TerrainTextures aggregate (one downscaled
    ///     bitmap), used as the zoomed-out LOD. Built once per worldspace + cached; on completion the
    ///     bitmap is composited via the single-bitmap path. If the worldspace is too large for one
    ///     bitmap, flags <see cref="_terrainAggregateUnavailable" /> so the view falls back to per-cell.
    /// </summary>
    private void EnsureTerrainAggregateBuild(uint? worldspaceFormId)
    {
        if (_terrainAggregateBuilding || _data is null) return;
        var activeCells = GetActiveCells();
        if (activeCells.Count == 0) return;
        if (LandscapeTexturePalette.GetOrCreate(_data) is not { } palette) return;

        _terrainAggregateBuilding = true;
        var version = ++_terrainAggregateVersion;
        ShowLayerBuildStatus("Building terrain overview...", busy: true);

        var request = new LayerBuildRequest(
            version,
            activeCells.ToList(),
            _state.SelectedWorldspace,
            _data,
            _currentDefaultWaterHeight,
            _currentColorScheme,
            // ShowWater=false: the zoomed-out terrain aggregate renders water-FREE; the world water
            // bitmap is drawn over it (scaled) as the toggle-able water pass.
            false,
            WorldMapLayer.TerrainTextures,
            _cachedGrayscale,
            _cachedWaterMask,
            _cachedHmWidth,
            _cachedHmHeight,
            _data.RenderCache,
            palette,
            WorldMapLayerRenderer.TexturePixelsPerCell,
            PreferAggregate: true,
            WaterPalette: _showWater ? _currentWaterPalette : null);

        _ = BuildTerrainAggregateAsync(request, worldspaceFormId);
    }

    private async Task BuildTerrainAggregateAsync(LayerBuildRequest request, uint? worldspaceFormId)
    {
        try
        {
            var result = await WorldLayerBuildService.BuildAsync(request).ConfigureAwait(false);
            _ = DispatcherQueue.TryEnqueue(() => ApplyTerrainAggregateResult(result, worldspaceFormId));
        }
        catch (Exception ex)
        {
            BethesdaMultitool.Core.Logger.Instance.Warn("Terrain aggregate build failed: {0}", ex.ToString());
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _terrainAggregateBuilding = false;
                HideLayerBuildStatus();
            });
        }
    }

    private void ApplyTerrainAggregateResult(LayerBuildResult result, uint? worldspaceFormId)
    {
        _terrainAggregateBuilding = false;
        if (result.Version != _terrainAggregateVersion) return; // superseded by a newer aggregate build

        if (result.Bitmap is { } bmp)
        {
            _terrainAggregateBitmap?.Dispose();
            _terrainAggregateBitmap = CanvasBitmap.CreateFromBytes(
                MapCanvas, bmp.Pixels, bmp.Width, bmp.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            _terrainAggMinX = bmp.MinCellX;
            _terrainAggMaxY = bmp.MaxCellY;
            _terrainAggPixelWidth = bmp.Width;
            _terrainAggPixelHeight = bmp.Height;
            // CellPixelsPerCell carries the aggregate's adaptive px/cell (< 33 for large worldspaces) —
            // the composite scales by CellWorldSize / this so the bitmap lands on the right world rect.
            _terrainAggPixelsPerCell = result.CellPixelsPerCell > 0
                ? result.CellPixelsPerCell
                : WorldMapLayerRenderer.HeightmapPixelsPerCell;
            _terrainAggregateWorldspaceFormId = worldspaceFormId;
            _terrainAggregateUnavailable = false;
            BethesdaMultitool.Core.Logger.Instance.Info(
                "TerrainTextures aggregate LOD built: {0}x{1}px @ {2}px/cell ws=0x{3:X8}",
                bmp.Width, bmp.Height, _terrainAggPixelsPerCell, worldspaceFormId ?? 0);
        }
        else
        {
            // Worldspace too large for a single aggregate bitmap — fall back to per-cell streaming.
            BethesdaMultitool.Core.Logger.Instance.Info(
                "TerrainTextures aggregate LOD unavailable (worldspace too large) — using per-cell.");
            _terrainAggregateUnavailable = true;
            _terrainTexturesAggregateActive = false;
            _viewportRebuildPending = true;
            EnsureViewportTimerRunning();
        }

        HideLayerBuildStatus();
        MapCanvas.Invalidate();
    }

    /// <summary>
    ///     Lazily builds the standalone world water bitmap (<see cref="_worldWaterBitmap" />) once per
    ///     worldspace. Cheap CPU-only pass (33 px/cell water mask → premult RGBA), so it runs on a
    ///     background task rather than the heavier <see cref="WorldLayerBuildService" /> path. Built
    ///     regardless of the water toggle so toggling water ON is an instant redraw. No-op when already
    ///     built/building for this worldspace.
    /// </summary>
    private void EnsureWorldWaterBitmap(uint? worldspaceFormId)
    {
        if (_worldWaterBuilding || _data is null) return;
        // Build ONCE per worldspace — keyed on the worldspace, NOT on a non-null bitmap. A worldspace
        // with no water makes RenderWorldWaterAggregate return null; the old `_worldWaterBitmap is not
        // null` guard then never tripped, so this re-kicked a FULL-worldspace water build EVERY draw
        // (each a whole-heightmap pass on a background core) and its completion re-Invalidate'd the
        // canvas — a continuous CPU peg with the map visually static. Confirmed via process dump
        // (_worldWaterBuilding=1, every other thread idle). A worldspace switch clears the id in
        // ClearWorldBitmaps so a real change still rebuilds.
        if (_worldWaterWorldspaceFormId == worldspaceFormId) return;

        var cells = GetActiveCells();
        if (cells.Count == 0) return;

        _worldWaterBuilding = true;
        // Mark this worldspace attempted up front so a null result (no water) OR a build exception can't
        // re-trigger the guard above next frame. ApplyWorldWaterResult re-affirms it on completion.
        _worldWaterWorldspaceFormId = worldspaceFormId;
        var version = ++_worldWaterVersion;
        var defaultWaterHeight = _currentDefaultWaterHeight;
        var palette = _currentWaterPalette; // toggle-independent: the bitmap is built colored, drawn only when _showWater
        var cache = _data.RenderCache;
        // Snapshot the cell list for the background pass (mirrors the terrain-aggregate build).
        _ = BuildWorldWaterBitmapAsync(cells.ToList(), defaultWaterHeight, palette, cache, version, worldspaceFormId);
    }

    private async Task BuildWorldWaterBitmapAsync(
        List<CellRecord> cells, float? defaultWaterHeight, WaterColorPalette? palette,
        WorldRenderCache? cache, int version, uint? worldspaceFormId)
    {
        try
        {
            var bmp = await Task.Run(() =>
                WorldMapLayerRenderer.RenderWorldWaterAggregate(cells, defaultWaterHeight, cache, palette))
                .ConfigureAwait(false);
            _ = DispatcherQueue.TryEnqueue(() => ApplyWorldWaterResult(bmp, version, worldspaceFormId));
        }
        catch (Exception ex)
        {
            BethesdaMultitool.Core.Logger.Instance.Warn("World water bitmap build failed: {0}", ex.ToString());
            _ = DispatcherQueue.TryEnqueue(() => { _worldWaterBuilding = false; });
        }
    }

    private void ApplyWorldWaterResult(WorldMapLayerRenderer.LayerBitmap? bmp, int version, uint? worldspaceFormId)
    {
        _worldWaterBuilding = false;
        if (version != _worldWaterVersion) return; // superseded (worldspace switched / cache cleared)

        _worldWaterBitmap?.Dispose();
        _worldWaterBitmap = null;
        if (bmp is { } b)
        {
            _worldWaterBitmap = CanvasBitmap.CreateFromBytes(
                MapCanvas, b.Pixels, b.Width, b.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            _worldWaterMinX = b.MinCellX;
            _worldWaterMaxY = b.MaxCellY;
            _worldWaterPixelWidth = b.Width;
            _worldWaterPixelHeight = b.Height;
        }
        _worldWaterWorldspaceFormId = worldspaceFormId;
        Map2DProfilerTrace.Event("world-water-built",
            $"ws=0x{worldspaceFormId ?? 0:X8} present={(_worldWaterBitmap is not null)} size={_worldWaterPixelWidth}x{_worldWaterPixelHeight}");
        MapCanvas.Invalidate();
    }

    private async Task BuildAndApplyWorldBitmapAsync(LayerBuildRequest request)
    {
        try
        {
            var result = await WorldLayerBuildService.BuildAsync(request).ConfigureAwait(false);
            _ = DispatcherQueue.TryEnqueue(() => ApplyWorldBitmapBuildResult(result));
        }
        catch (Exception ex)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (request.Version == _worldHeightmapBuildVersion)
                {
                    _worldHeightmapDirty = false;
                    if (request.Layer == WorldMapLayer.TerrainTextures)
                    {
                        _terrainTextureViewportKey = null;
                    }
                    ShowLayerBuildStatus($"{request.Layer.DisplayName()} failed: {ex.Message}", busy: false);
                    MapCanvas.Invalidate();
                }
            });
        }
    }

    private void ApplyWorldBitmapBuildResult(LayerBuildResult result)
    {
        if (result.Version != _worldHeightmapBuildVersion)
        {
            return;
        }

        var hasContent = result.Bitmap is not null || result.CellPixels is not null
            || result.CoarseTiles is not null;
        if (!hasContent)
        {
            if (_currentLayer == WorldMapLayer.TerrainTextures)
            {
                _terrainTextureViewportKey = null;
            }
            ShowLayerBuildStatus(result.Message ?? $"{_currentLayer.DisplayName()} has no renderable data.", busy: false);
            MapCanvas.Invalidate();
            return;
        }

        // Incremental merge for per-cell results: when the cached dict was already populated
        // for the current layer, dispatch only built new cells (the EnsureHeightmapBitmap
        // diff). Merge them in instead of disposing the rest. The key now includes
        // pixelsPerCell so multiple resolutions of the same cell can coexist.
        var isIncrementalMerge = result.CellPixels is not null
            && _layerCellBitmaps is not null
            && _layerCellBitmapsLayer == _currentLayer;

        if (!isIncrementalMerge)
        {
            _worldHeightmapBitmap?.Dispose();
            _worldHeightmapBitmap = null;
            if (_layerCellBitmaps is not null)
            {
                var disposed = _layerCellBitmaps.Count;
                foreach (var bmp in _layerCellBitmaps.Values) bmp.Dispose();
                _layerCellBitmaps = null;
                _layerCellBitmapsCacheGen++;
                Map2DProfilerTrace.Event("cache-gen-bump",
                    $"to={_layerCellBitmapsCacheGen} reason=ApplyWorldBitmapBuildResult disposed={disposed}");
            }
            DisposeCoarseTileBitmaps();
            _layerCellBitmapsLayer = null;
            // The cache the held cap was sized for was just replaced; size fresh next rebuild.
            _layerCellBitmapCapPolicy.Reset();
        }

        if (result.CellPixels is not null)
        {
            _layerCellBitmaps ??= new OrderedDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>(result.CellPixels.Count);
            foreach (var ((gx, gy), pixels) in result.CellPixels)
            {
                var tripleKey = (gx, gy, result.CellPixelsPerCell);
                if (_layerCellBitmaps.TryGetValue(tripleKey, out var stale))
                {
                    stale.Dispose();
                    _layerCellBitmaps.Remove(tripleKey);
                }
                _layerCellBitmaps.Add(tripleKey, CanvasBitmap.CreateFromBytes(
                    MapCanvas,
                    pixels,
                    result.CellPixelsPerCell,
                    result.CellPixelsPerCell,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized));
            }
            _layerCellBitmapsLayer = _currentLayer;
        }
        else if (result.CoarseTiles is not null)
        {
            // Oversized worldspace: one bitmap per coarse tile (bounded count). The cleared-above state
            // guarantees _coarseTileBitmaps is null here, so this is always a fresh full build.
            _coarseTileBitmaps = new Dictionary<(int tileGx, int tileGy), CanvasBitmap>(result.CoarseTiles.Count);
            _coarseTileCellSpan = result.CoarseTileCellSpan;
            _coarseTilePixelsPerCell = result.CoarseTilePixelsPerCell;
            _coarseTileBitmapsLayer = _currentLayer;
            var tileSidePx = result.CoarseTileCellSpan * result.CoarseTilePixelsPerCell;
            foreach (var ((tileGx, tileGy), pixels) in result.CoarseTiles)
            {
                _coarseTileBitmaps[(tileGx, tileGy)] = CanvasBitmap.CreateFromBytes(
                    MapCanvas,
                    pixels,
                    tileSidePx,
                    tileSidePx,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            }
            Map2DProfilerTrace.Event("coarse-tiles-built",
                $"layer={_currentLayer} tiles={_coarseTileBitmaps.Count} span={_coarseTileCellSpan} ppc={_coarseTilePixelsPerCell}");
        }
        else if (result.Bitmap is { } bitmap)
        {
            _worldHeightmapBitmap = CanvasBitmap.CreateFromBytes(
                MapCanvas,
                bitmap.Pixels,
                bitmap.Width,
                bitmap.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            _worldHmMinX = bitmap.MinCellX;
            _worldHmMaxY = bitmap.MaxCellY;
            _worldHmPixelWidth = bitmap.Width;
            _worldHmPixelHeight = bitmap.Height;
        }

        HideLayerBuildStatus();
        MapCanvas.Invalidate();
    }

    private void ShowLayerBuildStatus(string message, bool busy)
    {
        if (LayerBuildOverlay is null) return;
        LayerBuildStatusText.Text = message;
        LayerBuildProgress.IsActive = busy;
        LayerBuildProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LayerBuildOverlay.Visibility = Visibility.Visible;
    }

    private void HideLayerBuildStatus()
    {
        if (LayerBuildOverlay is null) return;
        LayerBuildProgress.IsActive = false;
        LayerBuildOverlay.Visibility = Visibility.Collapsed;
    }

    private void SetInteractiveCursor(bool interactive)
    {
        var shape = interactive ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow;
        if (shape != _currentCursorShape)
        {
            _currentCursorShape = shape;
            ProtectedCursor = InputSystemCursor.Create(shape);
        }
    }

    private void EnsureMarkerIcons(ICanvasResourceCreator resourceCreator)
    {
        if (_markerIconBitmaps != null) return;

        _markerIconBitmaps = new Dictionary<MapMarkerType, CanvasBitmap>();
        foreach (var type in Enum.GetValues<MapMarkerType>())
        {
            if (type == MapMarkerType.None) continue;
            var png = MapMarkerIconProvider.GetIconPng(type);
            if (png == null) continue;

            using var ms = new MemoryStream(png);
            // Non-pumping block: GetAwaiter().GetResult() on the STA UI thread (this runs inside
            // the Win2D Draw handler) is a COM pumping wait that can re-enter XAML and fail-fast
            // (see NonPumpingWait docs). The decode runs on the thread pool, so polling completes.
            var loadTask = CanvasBitmap.LoadAsync(resourceCreator, ms.AsRandomAccessStream()).AsTask();
            Core.Orchestration.NonPumpingWait.Wait(loadTask);
            var bitmap = loadTask.GetAwaiter().GetResult();
            _markerIconBitmaps[type] = bitmap;
        }
    }

    /// <summary>
    ///     Returns marker icons pre-tinted to the current color scheme, rebuilding the cache only when the
    ///     scheme changes. Tinting once here (a handful of one-shot <see cref="ColorMatrixEffect" /> passes
    ///     into per-icon render targets) replaces the old per-marker-per-frame effect in
    ///     <c>DrawMapMarkers</c>, which dominated frame time at zoomed-out overview (every worldspace marker
    ///     in view). Returns null if the base icons aren't loaded yet.
    /// </summary>
    private Dictionary<MapMarkerType, CanvasBitmap>? EnsureTintedMarkerIcons(
        ICanvasResourceCreator resourceCreator, HeightmapColorScheme scheme)
    {
        if (_markerIconBitmaps is null) return null;

        var argb = (uint)((255 << 24) | (scheme.R << 16) | (scheme.G << 8) | scheme.B);
        if (_tintedMarkerIconBitmaps is not null && _tintedMarkerColorArgb == argb)
        {
            return _tintedMarkerIconBitmaps;
        }

        DisposeTintedMarkerIcons();
        var tintScale = new Matrix5x4
        {
            M11 = scheme.R / 255f, M22 = scheme.G / 255f, M33 = scheme.B / 255f, M44 = 1f
        };
        var tinted = new Dictionary<MapMarkerType, CanvasBitmap>(_markerIconBitmaps.Count);
        foreach (var (type, icon) in _markerIconBitmaps)
        {
            var w = (float)icon.SizeInPixels.Width;
            var h = (float)icon.SizeInPixels.Height;
            // 96 DPI render target → DIPs == pixels, so the icon maps 1:1 with no resample.
            var rt = new CanvasRenderTarget(resourceCreator, w, h, 96f);
            using (var rtds = rt.CreateDrawingSession())
            {
                rtds.Clear(Colors.Transparent);
                using var tintEffect = new ColorMatrixEffect { Source = icon, ColorMatrix = tintScale };
                rtds.DrawImage(tintEffect, new Rect(0, 0, w, h), new Rect(0, 0, w, h));
            }
            tinted[type] = rt; // CanvasRenderTarget is a CanvasBitmap; blit it directly per marker.
        }

        _tintedMarkerIconBitmaps = tinted;
        _tintedMarkerColorArgb = argb;
        return tinted;
    }

    private void DisposeMarkerIcons()
    {
        if (_markerIconBitmaps != null)
        {
            foreach (var bmp in _markerIconBitmaps.Values) bmp.Dispose();
            _markerIconBitmaps = null;
        }
        DisposeTintedMarkerIcons();
    }

    private void DisposeTintedMarkerIcons()
    {
        if (_tintedMarkerIconBitmaps != null)
        {
            foreach (var bmp in _tintedMarkerIconBitmaps.Values) bmp.Dispose();
            _tintedMarkerIconBitmaps = null;
            _tintedMarkerColorArgb = 0;
        }
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
        public string DisplayName { get; init; } = "";
        public string ObjectCount { get; init; } = "";
        public required CellRecord Cell { get; init; }
    }

    /// <summary>A named group of cell-list rows for the grouped ListView.</summary>
    internal sealed class CellListGroup(string key, List<CellListItem> items) : List<CellListItem>(items)
    {
        public string Key { get; } = key;
    }

    // ========================================================================
    // Profiler-driving surface (used by BethesdaMap2DProfiler to script
    // viewport/zoom/pan sequences for telemetry capture).
    // ========================================================================

    internal int Profiler_WorldspaceCount => WorldspaceComboBox.Items.Count;

    /// <summary>Worldspace picker labels ("Name — N cells") for the capture harness to pick a dense one.</summary>
    internal IReadOnlyList<string> Profiler_WorldspaceLabels =>
        [.. WorldspaceComboBox.Items.Select(o => o?.ToString() ?? string.Empty)];

    /// <summary>True when the TerrainTextures aggregate-LOD bitmap is the active overview (zoomed out).</summary>
    internal bool Profiler_TerrainAggregateActive => _terrainTexturesAggregateActive;

    internal int Profiler_WorldspaceSelectedIndex
    {
        get => WorldspaceComboBox.SelectedIndex;
        set => WorldspaceComboBox.SelectedIndex = value;
    }

    internal WorldMapLayer Profiler_Layer
    {
        get => _currentLayer;
        set => LayerComboBox.SelectedIndex = (int)value;
    }

    /// <summary>
    ///     Profiler hook to drive the "Rendered models" top-down overlay (so a headless run is a true
    ///     full-path perf test, not a 2D-only one). Routes through the real checkbox so the existing
    ///     handler runs — which gates enabling on <c>TopDownProvider.CanRenderTopDown</c> and kicks the
    ///     first overlay request. The caller must therefore wait until the provider is ready before
    ///     setting <c>true</c>, else the gate leaves it off.
    /// </summary>
    internal bool Profiler_ShowRenderedObjects
    {
        get => _showRenderedObjects;
        set
        {
            if (RenderedObjectsCheckBox is not null) RenderedObjectsCheckBox.IsChecked = value;
        }
    }

    internal float Profiler_Zoom => _zoom;

    internal Vector2 Profiler_PanOffset => _panOffset;

    internal int Profiler_CacheCount => _layerCellBitmaps?.Count ?? 0;
    internal int Profiler_CacheCap => _layerCellBitmapCap;
    internal int Profiler_BuildVersion => _worldHeightmapBuildVersion;
    internal int Profiler_CacheGen => _layerCellBitmapsCacheGen;

    /// <summary>Diagnostics: which overview bitmaps are currently resident (for the layer-toggle repro).</summary>
    internal bool Profiler_AggregateBitmapPresent => _terrainAggregateBitmap is not null;
    internal bool Profiler_HeightmapBitmapPresent => _worldHeightmapBitmap is not null;
    internal bool Profiler_AggregateUnavailable => _terrainAggregateUnavailable;
    internal WorldMapLayer? Profiler_CellBitmapsLayer => _layerCellBitmapsLayer;

    /// <summary>
    ///     Coverage of the CURRENT viewport by the per-cell bitmap cache at the target
    ///     resolution: (populated cells visible, of those cached at the current ppc tier).
    ///     A pan-stress scenario asserts cached ≈ visible at the end of its sweeps — the
    ///     numeric form of "no permanently unpainted cells".
    /// </summary>
    internal (int Visible, int Cached) Profiler_VisibleCellCoverage()
    {
        var activeCells = GetActiveCells();
        if (activeCells.Count == 0) return (0, 0);

        var request = BuildTerrainTextureViewportRequest(
            (float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight, activeCells);
        if (_layerCellBitmaps is null) return (request.Cells.Count, 0);

        var cached = 0;
        foreach (var c in request.Cells)
        {
            if (c.GridX is not int gx || c.GridY is not int gy) continue;
            if (_layerCellBitmaps.ContainsKey((gx, gy, request.PixelsPerCell))) cached++;
        }

        return (request.Cells.Count, cached);
    }

    /// <summary>
    ///     Set zoom + pan together (atomic, single invalidate). Mirrors the math used by
    ///     <see cref="MapCanvas_PointerWheelChanged" /> so a profiler-driven zoom hits the
    ///     same code paths as the user's wheel input.
    /// </summary>
    internal void Profiler_SetView(float zoom, Vector2 panOffset)
    {
        _zoom = Math.Clamp(zoom, 0.001f, 50f);
        _panOffset = panOffset;
        MapCanvas.Invalidate();
    }

    /// <summary>
    ///     Move pan by a screen-space delta and tick the EMA velocity, matching
    ///     <see cref="MapCanvas_PointerMoved" />. The velocity is what drives the
    ///     Preload-margin asymmetry in the viewport request, so a scripted pan that skips
    ///     this would behave differently from a user pan.
    /// </summary>
    internal void Profiler_PanBy(Vector2 deltaScreen)
    {
        var newOffset = _panOffset + deltaScreen;
        _panVelocity = Vector2.Lerp(_panVelocity, newOffset - _panOffset, 0.35f);
        _panOffset = newOffset;
        MapCanvas.Invalidate();
    }

    internal float Profiler_CanvasWidth => (float)MapCanvas.ActualWidth;
    internal float Profiler_CanvasHeight => (float)MapCanvas.ActualHeight;

    /// <summary>
    ///     Centers the view on the centroid of the active worldspace's grid cells at the given
    ///     zoom. The centroid sits inside the populated region — unlike <see cref="ApplyZoomToFitWorldspace" />'s
    ///     bounding-box center, which on an irregular worldspace (e.g. WastelandNV) can land in an
    ///     unpopulated notch, leaving a profiler zoom-in streaming a near-empty viewport. The capture
    ///     harness uses this so the aggregate→per-cell transition actually streams a dense viewport.
    /// </summary>
    internal void Profiler_CenterOnActiveCells(float zoom)
    {
        double sx = 0, sy = 0;
        var n = 0;
        foreach (var c in GetActiveCells())
        {
            if (c.GridX is not int gx || c.GridY is not int gy) continue;
            // Canvas-world space: X grows east, Y is the NEGATED grid Y (north is up / min canvas Y),
            // matching WorldMapViewportHelper.GetViewTransform and the cell rects drawn in the overview.
            sx += (gx + 0.5) * _cellSize;
            sy += -(gy + 0.5) * _cellSize;
            n++;
        }

        if (n == 0) return;

        var centroid = new Vector2((float)(sx / n), (float)(sy / n));
        var screenCenter = new Vector2(
            (float)MapCanvas.ActualWidth * 0.5f, (float)MapCanvas.ActualHeight * 0.5f);
        _zoom = Math.Clamp(zoom, 0.001f, 50f);
        // screen = canvasWorld * zoom + pan  (see Profiler_SetView / MapCanvas_PointerWheelChanged).
        _panOffset = screenCenter - centroid * _zoom;
        MapCanvas.Invalidate();
    }
}
