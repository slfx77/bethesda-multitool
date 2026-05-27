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

    // --- Heightmap tinting ---
    private HeightmapColorScheme _currentColorScheme = HeightmapColorScheme.Amber;

    // --- Cursor ---
    private InputSystemCursorShape _currentCursorShape = InputSystemCursorShape.Arrow;
    private float? _currentDefaultWaterHeight;
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
    private int _worldHmMinX, _worldHmMaxY;
    private int _worldHmPixelWidth, _worldHmPixelHeight;

    // --- Pan/Zoom ---
    private float _zoom = 0.05f;

    public WorldMapControl()
    {
        InitializeComponent();
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
        WorldMapLegendBuilder.Populate(LegendPanel, _hiddenCategories, () => MapCanvas.Invalidate());
    }

    private void SetCanvasMode(bool canvasVisible)
    {
        MapCanvas.Visibility = canvasVisible ? Visibility.Visible : Visibility.Collapsed;
        LegendOverlay.Visibility = canvasVisible ? Visibility.Visible : Visibility.Collapsed;
        FilterCheckboxPanel.Visibility = canvasVisible ? Visibility.Visible : Visibility.Collapsed;
        CellBrowserPanel.Visibility = canvasVisible ? Visibility.Collapsed : Visibility.Visible;
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
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        _worldHeightmapDirty = true;
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

    private async void SpritesCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_data == null)
        {
            return;
        }

        if (SpritesCheckBox.IsChecked == true)
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker,
                WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                var registry = WorldMapSpriteRegistry.Load(folder.Path, MapCanvas);
                if (registry != null)
                {
                    _data.SpriteRegistry?.Dispose();
                    _data.SpriteRegistry = registry;
                    MapCanvas.Invalidate();
                    return;
                }
            }

            // If loading failed or was cancelled, uncheck
            SpritesCheckBox.IsChecked = false;
        }
        else
        {
            _data.SpriteRegistry?.Dispose();
            _data.SpriteRegistry = null;
            MapCanvas.Invalidate();
        }
    }

    public void SelectObject(PlacedReference? obj)
    {
        _state.SelectObject(obj);
        MapCanvas.Invalidate();
    }

    public void Reset()
    {
        _data = null;
        _state.Reset();
        _hiddenCategories.Clear();
        _hideDisabledActors = true;
        _showWater = true;
        WaterCheckBox.IsChecked = true;
        DisabledCheckBox.IsChecked = false;
        _worldHeightmapDirty = true;
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        WorldspaceComboBox.Items.Clear();
        ExportButton.IsEnabled = false;
        MapCanvas.Invalidate();
    }

    public void Dispose()
    {
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        DisposeMarkerIcons();
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
            ApplyWorldspaceSwitch();
        }
    }

    private void ApplyWorldspaceSwitch()
    {
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;
        _worldHeightmapDirty = true;
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
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        _worldHeightmapDirty = true;
        MapCanvas.Invalidate();
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

        var layout = WorldMapExporter.ComputeExportLayout(GetActiveCells(), ExportLongEdge);
        if (layout == null) return;

        var wsName = _state.SelectedWorldspace?.EditorId ?? _state.SelectedWorldspace?.FullName ?? "worldspace";
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        picker.SuggestedFileName = $"{wsName}_map";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        EnsureHeightmapBitmap();
        EnsureMarkerIcons(MapCanvas);

        ExportButton.IsEnabled = false;
        try
        {
            var (imageW, imageH, ppc, minGx, maxGx, minGy, maxGy) = layout.Value;
            await WorldMapExporter.ExportWorldspacePngAsync(
                file.Path, imageW, imageH, ppc, minGx, maxGx, minGy, maxGy,
                MapCanvas, _worldHeightmapBitmap,
                _worldHmPixelWidth, _worldHmPixelHeight, _worldHmMinX, _worldHmMaxY,
                _state.FilteredMarkers, _hiddenCategories, _markerIconBitmaps, _currentColorScheme);
        }
        finally
        {
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

        EnsureHeightmapBitmap();

        var canvasW = (float)sender.ActualWidth;
        var canvasH = (float)sender.ActualHeight;

        if (_state.Mode == ViewMode.WorldOverview)
        {
            EnsureMarkerIcons(sender);
            WorldMapOverviewRenderer.DrawWorldOverview(
                ds, _data, GetActiveCells(), _state.FilteredMarkers, _cellGridLookup,
                _worldHeightmapBitmap,
                _worldHmPixelWidth, _worldHmPixelHeight, _worldHmMinX, _worldHmMaxY,
                _zoom, _panOffset, canvasW, canvasH,
                _hiddenCategories, _hideDisabledActors,
                _state.SelectedObject, _hoveredObject,
                _markerIconBitmaps, _currentColorScheme);

            // Dangling-REFR overlay (opt-in via DanglingRefsComboBox).
            // Drawn after overview so tinted cells overlay heightmap + cell grid.
            // Pass the active worldspace so Lucky38-interior actors don't get rendered
            // on the WastelandNV exterior map (their X/Y coords mean different things
            // in each worldspace's coordinate system).
            WorldMapDanglingRefOverlayRenderer.DrawOverlay(
                ds, _data.DanglingRefs, _data, _danglingThreshold,
                _state.SelectedWorldspace?.FormId,
                _zoom, _panOffset, canvasW, canvasH);
        }
        else if (_state.SelectedCell != null)
        {
            WorldMapCellDetailRenderer.DrawCellDetail(
                ds, _state.SelectedCell, _data, _cellHeightmapBitmap,
                _zoom, _panOffset, canvasW, canvasH,
                _hiddenCategories, _hideDisabledActors,
                _state.SelectedObject, _hoveredObject);
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
            _panOffset = _panOffsetAtStart + delta;
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
                    _state.SelectedWorldspace?.FormId, worldPos, _zoom);
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
                    _hiddenCategories, _hideDisabledActors, _zoom);
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
                    _state.SelectedWorldspace?.FormId, worldClick, _zoom);
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
                _state.FilteredMarkers, _cellGridLookup, _hiddenCategories, _hideDisabledActors,
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
            MapCanvas, cell, _currentDefaultWaterHeight, _currentColorScheme, _showWater);

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
        _cellGridLookup = _state.BuildCellGridLookup();
    }

    private void ApplyZoomToFitWorldspace()
    {
        WorldMapViewportHelper.ZoomToFitWorldspace(
            GetActiveCells(),
            (float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight,
            out _zoom, out _panOffset);
    }

    private void EnsureHeightmapBitmap()
    {
        if (!_worldHeightmapDirty || GetActiveCells().Count == 0) return;

        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;

        var info = WorldMapHeightmapBuilder.Build(
            MapCanvas, GetActiveCells(), _cachedGrayscale, _cachedWaterMask,
            _cachedHmWidth, _cachedHmHeight,
            _state.SelectedWorldspace, _data,
            _currentDefaultWaterHeight, _currentColorScheme, _showWater);

        if (info.HasValue)
        {
            _worldHeightmapBitmap = info.Value.Bitmap;
            _worldHmMinX = info.Value.MinX;
            _worldHmMaxY = info.Value.MaxY;
            _worldHmPixelWidth = info.Value.PixelWidth;
            _worldHmPixelHeight = info.Value.PixelHeight;
        }

        _worldHeightmapDirty = false;
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
}
