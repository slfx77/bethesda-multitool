using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.UI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using WinRT.Interop;

namespace FalloutXbox360Utils;

public sealed partial class WorldMapControl : UserControl, IDisposable
{
    private const int ExportLongEdge = 4096;

    // --- Legend / Browser ---
    private readonly HashSet<PlacedObjectCategory> _hiddenCategories = [];
    private readonly WorldMapStateController _state = new();

    // --- Cell browser ---
    private List<CellListItem> _allCellItems = [];
    private byte[]? _cachedGrayscale;
    private int _cachedHmWidth, _cachedHmHeight;
    private byte[]? _cachedWaterMask;

    // --- Cell grid lookup ---
    private Dictionary<(int x, int y), CellRecord>? _cellGridLookup;
    private CanvasBitmap? _cellHeightmapBitmap;
    private WorldSpatialIndex? _spatialIndex;

    // --- Heightmap tinting ---
    private HeightmapColorScheme _currentColorScheme = HeightmapColorScheme.Amber;

    // --- Layer selection ---
    private WorldMapLayer _currentLayer = WorldMapLayer.Heightmap;

    // --- Overlay toggles ---
    private bool _showNavMesh;

    // --- Cursor ---
    private InputSystemCursorShape _currentCursorShape = InputSystemCursorShape.Arrow;
    private float? _currentDefaultWaterHeight;
    private WaterColorPalette? _currentWaterPalette;
    private WorldViewData? _data;

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

    // --- Click detection ---
    private Vector2 _pointerDownScreen;
    private bool _pointerWasDragged;
    private bool _showWater = true;
    private bool _suppressNavEvents;

    // --- Heightmap bitmaps ---
    private CanvasBitmap? _worldHeightmapBitmap;
    private bool _worldHeightmapDirty = true;
    private int _worldHeightmapBuildVersion;
    private int _worldHmMinX, _worldHmMaxY;
    private int _worldHmPixelWidth, _worldHmPixelHeight;

    /// <summary>
    ///     Cancellation source for the in-flight TerrainTextures streaming build. Replaced
    ///     and cancelled on every viewport change so abandoned cells don't keep decoding on
    ///     background workers — mirrors Mapbox GL Native's "abandon outdated tile work" policy.
    /// </summary>
    private CancellationTokenSource? _terrainStreamCts;

    /// <summary>
    ///     Per-cell GPU bitmaps for overview layers that would exceed D3D's max texture size
    ///     as one large bitmap. Composited cell-by-cell in <see cref="WorldMapOverviewRenderer" />.
    ///     Key is <c>(gridX, gridY, pixelsPerCell)</c> so multiple resolutions of the same cell
    ///     can coexist — when the user zooms across a resolution threshold the previous tier
    ///     remains as a visual stand-in (Cesium's "Ancestor Meets SSE" pattern) until the
    ///     target-resolution bitmap finishes building.
    /// </summary>
    private Dictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? _layerCellBitmaps;

    /// <summary>Which layer the cached cell bitmaps belong to. Used to detect layer switches.</summary>
    private WorldMapLayer? _layerCellBitmapsLayer;

    /// <summary>
    ///     Bumped only when <see cref="_layerCellBitmaps" /> is actually replaced (set to null,
    ///     reinitialised in a non-incremental rebuild, or its <see cref="_layerCellBitmapsLayer" />
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

    private TerrainTextureViewportKey? _terrainTextureViewportKey;

    // --- Pan/Zoom ---
    private float _zoom = 0.05f;

    public WorldMapControl()
    {
        InitializeComponent();
        // Cancel any in-flight TerrainTextures stream when the control unloads (tab switch
        // or app shutdown). Without this, the async loop in StreamAndApplyTerrainTexturesAsync
        // keeps producing cells, tries to enqueue onto a now-null DispatcherQueue, and throws
        // NullReferenceException — visible as "TerrainTextures streaming failed" in the log.
        Unloaded += (_, _) => CancelTerrainStream();
    }

    // --- Navigation ---
    internal event Action? BeforeNavigate;

    // --- Events ---
    public event EventHandler<PlacedReference>? InspectObject;
    public event EventHandler<CellRecord>? InspectCell;

    internal void LoadData(WorldViewData data)
    {
        _data = data;
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
        if (_suppressMarkersCheckboxEvent) return;
        if (MapMarkersCheckBox.IsChecked == true)
        {
            _hiddenCategories.Remove(PlacedObjectCategory.MapMarker);
        }
        else
        {
            _hiddenCategories.Add(PlacedObjectCategory.MapMarker);
        }
        BuildLegendPanel();
        MapCanvas?.Invalidate();
    }

    private void NavMeshCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _showNavMesh = NavMeshCheckBox?.IsChecked == true;
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
        _showWater = WaterCheckBox.IsChecked == true;
        // keepCurrentBitmap MUST be false here. The water state is baked into every cached
        // bitmap (heightmap and per-cell TerrainTextures alike), and EnsureHeightmapBitmap's
        // "diff against the cached dict" filter would skip every viewport cell as already-
        // built and the stream would render nothing. Discarding the cache is the only way
        // the next stream pass actually re-renders the visible cells with the new water
        // state — without it, the user has to pan cells off-screen and back to refresh.
        InvalidateWorldBitmap(keepCurrentBitmap: false);
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        if (_state.SelectedCell is not null)
        {
            _cellHeightmapBitmap = WorldMapCellDetailRenderer.BuildCellHeightmapBitmap(
                MapCanvas, _state.SelectedCell, _currentDefaultWaterHeight,
                _currentColorScheme, _showWater, _currentLayer, _data, _data?.RenderCache);
        }
        MapCanvas.Invalidate();
    }

    private void DisabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
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

    public void SelectObject(PlacedReference? obj)
    {
        _state.SelectObject(obj);
        MapCanvas.Invalidate();
    }

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
        DisposeWorldBitmaps();
        _worldHeightmapDirty = true;
        HideLayerBuildStatus();
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        WorldspaceComboBox.Items.Clear();
        ExportButton.IsEnabled = false;
        MapCanvas.Invalidate();
    }

    public void Dispose()
    {
        CancelTerrainStream();
        DisposeWorldBitmaps();
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
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
    ///     stop decoding cells the user has already panned away from. Cheap and idempotent.
    /// </summary>
    private void CancelTerrainStream()
    {
        if (_terrainStreamCts is null) return;
        try { _terrainStreamCts.Cancel(); }
        catch (ObjectDisposedException) { }
        _terrainStreamCts.Dispose();
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
        _layerCellBitmapsLayer = null;
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
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
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
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
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
            _cellHeightmapBitmap.Dispose();
            _cellHeightmapBitmap = WorldMapCellDetailRenderer.BuildCellHeightmapBitmap(
                MapCanvas, _state.SelectedCell, _currentDefaultWaterHeight,
                _currentColorScheme, _showWater, _currentLayer, _data, _data?.RenderCache);
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
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
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
            initialIncludeMarkers: !_hiddenCategories.Contains(PlacedObjectCategory.MapMarker),
            initialIncludeNavMesh: _showNavMesh,
            initialIncludeWater: _showWater,
            initialLongEdgePx: ExportLongEdge)
        {
            XamlRoot = XamlRoot
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary) return;
        var req = dialog.GetRequest();

        var layout = WorldMapExporter.ComputeExportLayout(activeCells, req.LongEdgePx);
        if (layout == null) return;

        var wsName = _state.SelectedWorldspace?.EditorId ?? _state.SelectedWorldspace?.FullName ?? "worldspace";
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        picker.SuggestedFileName = $"{wsName}_map";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        EnsureMarkerIcons(MapCanvas);

        // Build a fresh bitmap matching the dialog's water choice; the on-screen bitmap may
        // have been built with the opposite water flag. For the TerrainTextures layer we
        // build the per-cell GPU bitmap dictionary instead — same reason as the live view
        // (a single bitmap exceeds the GPU max-texture-size on large worldspaces).
        WorldMapHeightmapBuilder.HeightmapInfo? bitmapInfo = null;
        Dictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? exportCellBitmaps = null;
        if (_currentLayer == WorldMapLayer.TerrainTextures && _data is not null
            && LandscapeTexturePalette.GetOrCreate(_data) is { } exportPalette)
        {
            exportCellBitmaps = WorldMapHeightmapBuilder.BuildTerrainTextureCells(
                MapCanvas, activeCells, exportPalette,
                _currentDefaultWaterHeight, req.IncludeWater, _data.RenderCache);
        }
        else
        {
            bitmapInfo = WorldMapHeightmapBuilder.Build(
                MapCanvas, activeCells, _cachedGrayscale, _cachedWaterMask,
                _cachedHmWidth, _cachedHmHeight,
                _state.SelectedWorldspace, _data,
                _currentDefaultWaterHeight, _currentColorScheme, req.IncludeWater,
                _currentLayer, _data?.RenderCache);
        }

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

        ExportButton.IsEnabled = false;
        try
        {
            var (imageW, imageH, ppc, lMinGx, lMaxGx, lMinGy, lMaxGy) = layout.Value;
            await WorldMapExporter.ExportWorldspacePngAsync(
                file.Path, imageW, imageH, ppc, lMinGx, lMaxGx, lMinGy, lMaxGy,
                MapCanvas,
                bitmapInfo?.Bitmap ?? _worldHeightmapBitmap,
                bitmapInfo?.PixelWidth ?? _worldHmPixelWidth,
                bitmapInfo?.PixelHeight ?? _worldHmPixelHeight,
                bitmapInfo?.MinX ?? _worldHmMinX,
                bitmapInfo?.MaxY ?? _worldHmMaxY,
                exportCellBitmaps,
                _state.FilteredMarkers, exportHiddenCategories, _markerIconBitmaps, _currentColorScheme,
                _data, activeCells, req.IncludeNavMesh);
        }
        finally
        {
            bitmapInfo?.Bitmap.Dispose();
            if (exportCellBitmaps is not null)
            {
                foreach (var bmp in exportCellBitmaps.Values) bmp.Dispose();
            }
            ExportButton.IsEnabled = true;
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

        EnsureHeightmapBitmap(canvasW, canvasH);

        if (_state.Mode == ViewMode.WorldOverview)
        {
            EnsureMarkerIcons(sender);
            WorldMapOverviewRenderer.DrawWorldOverview(
                ds, _data, GetActiveCells(), _state.FilteredMarkers, _cellGridLookup, _spatialIndex,
                _worldHeightmapBitmap,
                _worldHmPixelWidth, _worldHmPixelHeight, _worldHmMinX, _worldHmMaxY,
                _layerCellBitmaps,
                _zoom, _panOffset, canvasW, canvasH,
                _hiddenCategories, _hideDisabledActors,
                _state.SelectedObject, _hoveredObject,
                _markerIconBitmaps, _currentColorScheme);

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
                _state.SelectedObject, _hoveredObject);

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
        e.Handled = true;
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

        MapCanvas.Invalidate();
        e.Handled = true;
    }

    // ========================================================================
    // Navigation
    // ========================================================================

    public void NavigateToCell(CellRecord cell)
    {
        NotifyBeforeNavigate();
        _state.NavigateToCell(cell);
        _hoveredObject = null;
        SetCanvasMode(true);

        HoverInfoText.Text = FormatCellDisplayName(cell);

        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = WorldMapCellDetailRenderer.BuildCellHeightmapBitmap(
            MapCanvas, cell, _currentDefaultWaterHeight, _currentColorScheme, _showWater,
            _currentLayer, _data, _data?.RenderCache);

        WorldMapViewportHelper.ZoomToFitCell(cell,
            (float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight,
            out _zoom, out _panOffset);
        MapCanvas.Invalidate();
    }

    private static string FormatCellDisplayName(CellRecord cell) =>
        cell.FullName ?? cell.EditorId ?? $"0x{cell.FormId:X8}";

    internal void NavigateToCellPublic(CellRecord cell) => NavigateToCell(cell);

    public void NavigateToWorldspaceAndCell(int worldspaceIndex, CellRecord cell)
    {
        WorldspaceComboBox.SelectedIndex = worldspaceIndex;
        NavigateToCellInOverview(cell);
    }

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

    public void NavigateToWorldspace(int worldspaceIndex)
    {
        if (worldspaceIndex >= 0 && worldspaceIndex < WorldspaceComboBox.Items.Count)
            WorldspaceComboBox.SelectedIndex = worldspaceIndex;
    }

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
            _cellHeightmapBitmap?.Dispose();
            _cellHeightmapBitmap = null;
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
        var source = WorldMapCellBrowser.BuildGroupedSource(items);
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

    private void EnsureHeightmapBitmap(float canvasW, float canvasH)
    {
        if (_data is null) return;

        var activeCells = GetActiveCells();
        if (activeCells.Count == 0) return;

        var requestCells = activeCells.ToList();
        var texturePixelsPerCell = WorldMapLayerRenderer.TexturePixelsPerCell;
        TerrainTextureViewportKey? terrainTextureKey = null;
        var isTerrainTexturesViewport = _state.Mode == ViewMode.WorldOverview &&
            _currentLayer == WorldMapLayer.TerrainTextures;
        var viewportCellCount = activeCells.Count;

        if (isTerrainTexturesViewport)
        {
            var viewportRequest = BuildTerrainTextureViewportRequest(canvasW, canvasH, activeCells);
            requestCells = viewportRequest.Cells;
            texturePixelsPerCell = viewportRequest.PixelsPerCell;
            terrainTextureKey = viewportRequest.Key;
            viewportCellCount = viewportRequest.Cells.Count;

            // Scale the in-stream LRU cap to the current viewport BEFORE the stream starts.
            // Otherwise, with a large viewport (~6000 cells for zoomed-out WastelandNV), the
            // 256-entry default would evict cells we just rendered, in the same stream, before
            // the user ever sees them — "Loading" goes away with most of the view still blank.
            //
            // ×3 budget: 1× current viewport + 1× zoom-transition stand-ins at a stale resolution
            // + 1× pan-back history (cells the user just scrolled off-screen). The 1× pan slot
            // is what makes "pan off, pan back" a cache hit instead of a re-render — see the
            // eager-evict removal further down in this method.
            _layerCellBitmapCap = Math.Max(MinLayerCellBitmapCap, viewportRequest.Cells.Count * 3);

            if (_terrainTextureViewportKey != terrainTextureKey)
            {
                _worldHeightmapDirty = true;
            }
        }

        if (!_worldHeightmapDirty) return;

        // Incremental rebuild for the TerrainTextures live view: keep already-built cell
        // bitmaps inside the new viewport, evict cells that scrolled off, and dispatch a
        // build only for the cells we don't have at the target resolution yet. With the
        // multi-resolution cache (P2), old-resolution bitmaps remain as visual stand-ins
        // while target-resolution ones build — no blank during zoom transitions.
        var canReuseCache = isTerrainTexturesViewport
            && _layerCellBitmaps is not null
            && _layerCellBitmapsLayer == _currentLayer;

        if (canReuseCache)
        {
            // Pan-back cache hit: don't eagerly evict cells just because they scrolled out of
            // the current viewport. Cells that left recently are still in the dict and pan-back
            // lands on a hit instead of re-rendering. Memory is bounded by _layerCellBitmapCap
            // (viewport×3) — the per-add LRU prune in ApplyOneTerrainTextureCell handles
            // eviction when new cells overflow the budget.

            // LRU-on-read: .NET dict iteration order is INSERTION order (not actual LRU). The
            // LRU prune in ApplyOneTerrainTextureCell pops Keys.First() — i.e. the earliest-
            // inserted key — which is often a cell that's been on screen across many streams
            // and never got re-rendered (the filter below excludes already-cached cells from
            // the next stream). Without this touch, the user sees "cells paint, then disappear"
            // as the FIFO eviction kicks out currently-visible cells. Remove-then-add reinserts
            // each in-viewport key at the dict's tail so they form the MRU end; off-viewport
            // cells sit at the head and evict first when the cap is hit.
            var neededKeys = new HashSet<(int gx, int gy)>(requestCells.Count);
            foreach (var c in requestCells)
            {
                if (c.GridX is int gx && c.GridY is int gy) neededKeys.Add((gx, gy));
            }
            foreach (var kv in _layerCellBitmaps!.ToList())
            {
                if (neededKeys.Contains((kv.Key.gx, kv.Key.gy)))
                {
                    _layerCellBitmaps.Remove(kv.Key);
                    _layerCellBitmaps[kv.Key] = kv.Value;
                }
            }

            // Only build the cells that aren't already cached AT THE TARGET RESOLUTION.
            // Cells present only at the wrong resolution stay (visual stand-in via the draw-time
            // best-available picker) AND get scheduled for build at the target res.
            requestCells = requestCells
                .Where(c => c.GridX is int gx && c.GridY is int gy
                    && !_layerCellBitmaps!.ContainsKey((gx, gy, texturePixelsPerCell)))
                .ToList();

            _terrainTextureViewportKey = terrainTextureKey;
            _worldHeightmapDirty = false;

            Map2DProfilerTrace.Event("viewport-rebuild",
                $"layer={_currentLayer} ppc={texturePixelsPerCell} viewport={viewportCellCount} cap={_layerCellBitmapCap} cacheSize={_layerCellBitmaps?.Count ?? 0} toBuild={requestCells.Count}");

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
            _worldHeightmapDirty = false;
            _terrainTextureViewportKey = terrainTextureKey;
            Map2DProfilerTrace.Event("viewport-rebuild",
                $"layer={_currentLayer} ppc={texturePixelsPerCell} viewport={viewportCellCount} cap={_layerCellBitmapCap} cacheSize=0 toBuild={requestCells.Count} kind=full");
        }

        var version = ++_worldHeightmapBuildVersion;
        ShowLayerBuildStatus($"Building {_currentLayer.DisplayName()}...", busy: true);

        // TerrainTextures streams cells individually via IAsyncEnumerable so the user sees
        // the viewport populate progressively. Every other layer still goes through the
        // batch BuildAsync path because their per-cell cost is much lower (no per-pixel
        // palette sampling) and the batch reduces overhead.
        if (isTerrainTexturesViewport
            && LandscapeTexturePalette.GetOrCreate(_data) is { } palette
            && requestCells.Count > 0)
        {
            // Urgent-first ordering (P3): sort by squared distance from viewport center so
            // the parallel worker pool picks central cells before margin/preload cells.
            // Cesium's Preload priority tier — Urgent (visible) drains before Normal/Preload.
            var (vpTl, vpBr) = WorldMapViewportHelper.GetVisibleWorldBounds(canvasW, canvasH, _zoom, _panOffset);
            var centerX = (vpTl.X + vpBr.X) * 0.5f;
            var centerY = (vpTl.Y + vpBr.Y) * 0.5f;
            requestCells.Sort((a, b) =>
            {
                var ax = (a.GridX!.Value + 0.5f) * WorldGridConstants.CellSize - centerX;
                var ay = -(a.GridY!.Value + 0.5f) * WorldGridConstants.CellSize - centerY;
                var bx = (b.GridX!.Value + 0.5f) * WorldGridConstants.CellSize - centerX;
                var by = -(b.GridY!.Value + 0.5f) * WorldGridConstants.CellSize - centerY;
                return (ax * ax + ay * ay).CompareTo(bx * bx + by * by);
            });

            _terrainStreamCts?.Cancel();
            _terrainStreamCts?.Dispose();
            _terrainStreamCts = new CancellationTokenSource();
            // Capture cache-gen on the UI thread BEFORE the stream starts. Worker threads read
            // this value when enqueuing apply tasks; if cache-gen bumps after the stream begins
            // (worldspace switch, layer change), the captured generation goes stale and the
            // applies drop on the ApplyOne side. Per-stream `version` is kept for status-banner
            // messages but no longer controls apply-drop — pan-induced version bumps are common
            // and would needlessly kill in-flight applies whose cache target is still valid.
            var cacheGen = _layerCellBitmapsCacheGen;
            Map2DProfilerTrace.Event("stream-start",
                $"v={version} cacheGen={cacheGen} ppc={texturePixelsPerCell} requested={requestCells.Count}");
            _ = StreamAndApplyTerrainTexturesAsync(
                version, cacheGen, requestCells, palette, texturePixelsPerCell, _terrainStreamCts.Token);
            return;
        }

        var request = new LayerBuildRequest(
            version,
            requestCells,
            _state.SelectedWorldspace,
            _data,
            _currentDefaultWaterHeight,
            _currentColorScheme,
            _showWater,
            _currentLayer,
            _cachedGrayscale,
            _cachedWaterMask,
            _cachedHmWidth,
            _cachedHmHeight,
            _data.RenderCache,
            _currentLayer == WorldMapLayer.TerrainTextures
                ? LandscapeTexturePalette.GetOrCreate(_data)
                : null,
            texturePixelsPerCell);

        _ = BuildAndApplyWorldBitmapAsync(request);
    }

    /// <summary>
    ///     Consumes the per-cell streaming output from
    ///     <see cref="WorldMapLayerRenderer.StreamTerrainTexturesPerCell" /> and pushes each
    ///     completed cell to the UI thread for immediate <see cref="CanvasBitmap" /> upload
    ///     + canvas invalidation. Cells appear progressively instead of all-at-once.
    /// </summary>
    private async Task StreamAndApplyTerrainTexturesAsync(
        int version,
        int cacheGen,
        List<CellRecord> requestCells,
        LandscapeTexturePalette palette,
        int pixelsPerCell,
        CancellationToken ct)
    {
        var requestedCount = requestCells.Count;
        var producedCount = 0;
        var rejectedByQueue = 0;
        try
        {
            await foreach (var cell in WorldMapLayerRenderer.StreamTerrainTexturesPerCell(
                    requestCells,
                    palette,
                    _currentDefaultWaterHeight,
                    _showWater,
                    _data?.RenderCache,
                    pixelsPerCell,
                    _currentWaterPalette,
                    ct).WithCancellation(ct).ConfigureAwait(false))
            {
                producedCount++;
                // Hop to the UI thread to create the device-bound bitmap + invalidate the canvas.
                // DispatcherQueue can be null after the control unloads (tab switch, shutdown);
                // bail cleanly so we don't NRE — the cancellation token usually catches this
                // first, but defensive null-check makes shutdown races silent.
                var queue = DispatcherQueue;
                if (queue is null) break;
                if (!queue.TryEnqueue(() => ApplyOneTerrainTextureCell(cacheGen, cell)))
                {
                    rejectedByQueue++;
                }
            }

            FalloutXbox360Utils.Core.Logger.Instance.Info(
                "TerrainTextures stream v{0} complete: requested={1}, produced={2}, queue-rejected={3}, skipped-by-worker={4}",
                version, requestedCount, producedCount, rejectedByQueue,
                requestedCount - producedCount - rejectedByQueue);

            Map2DProfilerTrace.Event("stream-end",
                $"v={version} cacheGen={cacheGen} requested={requestedCount} produced={producedCount} queue-rejected={rejectedByQueue}");

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
            FalloutXbox360Utils.Core.Logger.Instance.Warn(
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
        _layerCellBitmaps ??= new Dictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>();
        var replacedStale = _layerCellBitmaps.TryGetValue(key, out var stale);
        if (replacedStale) stale!.Dispose();
        _layerCellBitmaps[key] = CanvasBitmap.CreateFromBytes(
            MapCanvas,
            cell.Pixels,
            cell.PixelsPerCell,
            cell.PixelsPerCell,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
        _layerCellBitmapsLayer = WorldMapLayer.TerrainTextures;

        Map2DProfilerTrace.Event("cache-add",
            $"cell=({cell.GridX},{cell.GridY},{cell.PixelsPerCell}) replaced={replacedStale} dictSize={_layerCellBitmaps.Count} cap={_layerCellBitmapCap}");

        // Memory cap. Once we exceed the soft limit, drop the oldest insertion-order entry.
        // Insertion order is .NET dict iteration order (post-net5); not strict LRU but close
        // enough — viewport-leave already evicts most of the stale ones in EnsureHeightmapBitmap.
        // The cap is scaled per-viewport (see _layerCellBitmapCap) so streamed cells aren't
        // evicted by later-streamed cells in the same stream.
        while (_layerCellBitmaps.Count > _layerCellBitmapCap)
        {
            var oldestKey = _layerCellBitmaps.Keys.First();
            _layerCellBitmaps[oldestKey].Dispose();
            _layerCellBitmaps.Remove(oldestKey);
            Map2DProfilerTrace.Event("cache-evict",
                $"cell=({oldestKey.gx},{oldestKey.gy},{oldestKey.pixelsPerCell}) reason=cap dictSize={_layerCellBitmaps.Count} cap={_layerCellBitmapCap}");
        }

        MapCanvas.Invalidate();
    }

    private TerrainTextureViewportRequest BuildTerrainTextureViewportRequest(
        float canvasW, float canvasH, List<CellRecord> activeCells)
    {
        var pixelsPerCell = ChooseTerrainTexturePixelsPerCell(_zoom);
        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasW, canvasH, _zoom, _panOffset);

        // P3 — Preload-direction bias. Cesium "Preload" priority tier: extend the viewport
        // margin in the direction of pan motion so cells about to scroll into view are
        // already decoded by the time they arrive. Screen-space pan velocity is positive
        // when the user drags the map content right/down, which means new content is
        // entering from the LEFT/TOP — extend the margin on the opposite side from velocity.
        // World Y is inverted from screen Y, so the Y bias gets flipped too.
        const int baseMarginCells = 1;
        const int preloadMarginCells = 3;
        const float velocityThresholdPx = 6f;
        var marginWorld = WorldGridConstants.CellSize * baseMarginCells;
        var preloadWorld = WorldGridConstants.CellSize * preloadMarginCells;

        var biasLeft = _panVelocity.X > velocityThresholdPx ? preloadWorld : marginWorld;
        var biasRight = _panVelocity.X < -velocityThresholdPx ? preloadWorld : marginWorld;
        var biasTopWorld = _panVelocity.Y > velocityThresholdPx ? preloadWorld : marginWorld;
        var biasBottomWorld = _panVelocity.Y < -velocityThresholdPx ? preloadWorld : marginWorld;

        var minCanvasX = Math.Min(tlWorld.X, brWorld.X) - biasLeft;
        var maxCanvasX = Math.Max(tlWorld.X, brWorld.X) + biasRight;
        // World Y grows northward, so the screen-top edge is the WORLD MAX-Y and screen-bottom is WORLD MIN-Y.
        var minCanvasY = Math.Min(tlWorld.Y, brWorld.Y) - biasBottomWorld;
        var maxCanvasY = Math.Max(tlWorld.Y, brWorld.Y) + biasTopWorld;

        var minGx = (int)MathF.Floor(minCanvasX / WorldGridConstants.CellSize);
        var maxGx = (int)MathF.Floor(maxCanvasX / WorldGridConstants.CellSize);
        var minGy = (int)MathF.Floor(-maxCanvasY / WorldGridConstants.CellSize);
        var maxGy = (int)MathF.Floor(-minCanvasY / WorldGridConstants.CellSize);
        var key = new TerrainTextureViewportKey(minGx, maxGx, minGy, maxGy, pixelsPerCell);

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

                if (gx >= minGx && gx <= maxGx && gy >= minGy && gy <= maxGy)
                {
                    visibleCells.Add(cell);
                }
            }
        }

        if (visibleCells.Count == 0)
        {
            visibleCells = activeCells
                .Where(c => c.GridX.HasValue && c.GridY.HasValue)
                .Take(1)
                .ToList();
        }

        return new TerrainTextureViewportRequest(key, visibleCells, pixelsPerCell);
    }

    private static int ChooseTerrainTexturePixelsPerCell(float zoom)
    {
        var screenPixelsPerCell = WorldGridConstants.CellSize * zoom;
        if (screenPixelsPerCell <= WorldMapLayerRenderer.TexturePixelsPerCell * 1.25f)
        {
            return WorldMapLayerRenderer.TexturePixelsPerCell;
        }

        if (screenPixelsPerCell <= WorldMapLayerRenderer.TexturePixelsPerCell * 2.5f)
        {
            return WorldMapLayerRenderer.TexturePixelsPerCell * 2;
        }

        return WorldMapLayerRenderer.MaxTexturePixelsPerCell;
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

        var hasContent = result.Bitmap is not null || result.CellPixels is not null;
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
        // pixelsPerCell so multiple resolutions of the same cell can coexist (P2).
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
            _layerCellBitmapsLayer = null;
        }

        if (result.CellPixels is not null)
        {
            _layerCellBitmaps ??= new Dictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>(result.CellPixels.Count);
            foreach (var ((gx, gy), pixels) in result.CellPixels)
            {
                var tripleKey = (gx, gy, result.CellPixelsPerCell);
                if (_layerCellBitmaps.TryGetValue(tripleKey, out var stale)) stale.Dispose();
                _layerCellBitmaps[tripleKey] = CanvasBitmap.CreateFromBytes(
                    MapCanvas,
                    pixels,
                    result.CellPixelsPerCell,
                    result.CellPixelsPerCell,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            }
            _layerCellBitmapsLayer = _currentLayer;
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
            var bitmap = CanvasBitmap.LoadAsync(resourceCreator, ms.AsRandomAccessStream()).GetAwaiter().GetResult();
            _markerIconBitmaps[type] = bitmap;
        }
    }

    private void DisposeMarkerIcons()
    {
        if (_markerIconBitmaps != null)
        {
            foreach (var bmp in _markerIconBitmaps.Values) bmp.Dispose();
            _markerIconBitmaps = null;
        }
    }

    // ========================================================================
    // Inner Types
    // ========================================================================

    internal enum ViewMode
    {
        WorldOverview,
        CellDetail,
        CellBrowser
    }

    internal enum BrowserMode
    {
        None,
        Interiors,
        AllCells
    }

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

    internal sealed class CellListItem
    {
        public string Group { get; init; } = "";
        public string GridLabel { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string ObjectCount { get; init; } = "";
        public required CellRecord Cell { get; init; }
    }

    internal sealed class CellListGroup(string key, List<CellListItem> items) : List<CellListItem>(items)
    {
        public string Key { get; } = key;
    }

    // ========================================================================
    // Profiler-driving surface (used by FalloutMap2DProfiler to script
    // viewport/zoom/pan sequences for telemetry capture).
    // ========================================================================

    internal int Profiler_WorldspaceCount => WorldspaceComboBox.Items.Count;

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

    internal float Profiler_Zoom => _zoom;

    internal Vector2 Profiler_PanOffset => _panOffset;

    internal int Profiler_CacheCount => _layerCellBitmaps?.Count ?? 0;
    internal int Profiler_CacheCap => _layerCellBitmapCap;
    internal int Profiler_BuildVersion => _worldHeightmapBuildVersion;
    internal int Profiler_CacheGen => _layerCellBitmapsCacheGen;

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
}
