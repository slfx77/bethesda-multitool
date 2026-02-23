using System.Collections.Frozen;
using System.Numerics;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace FalloutXbox360Utils;

public sealed partial class WorldMapControl : UserControl, IDisposable
{
    private const float MinZoom = 0.001f;
    private const float MaxZoom = 50f;

    // --- Constants ---
    private const float CellWorldSize = 4096f;
    private const int HmGridSize = 33;

    // --- Export ---
    // Target the long edge at 4096px — large enough for detail, small enough to view at 1:1
    private const int ExportLongEdge = 4096;

    // ========================================================================
    // Color Helpers
    // ========================================================================

    private static readonly FrozenDictionary<PlacedObjectCategory, Color> CategoryColors =
        BuildWorldCategoryColors();

    // --- Legend category visibility ---
    private readonly HashSet<PlacedObjectCategory> _hiddenCategories = [];
    private BrowserMode _activeBrowser = BrowserMode.None;

    // --- Cell browser search ---
    private List<CellListItem> _allCellItems = [];
    private byte[]? _cachedGrayscale;
    private int _cachedHmWidth, _cachedHmHeight;
    private byte[]? _cachedWaterMask;

    // --- Cell grid lookup (built when worldspace changes) ---
    private Dictionary<(int x, int y), CellRecord>? _cellGridLookup;
    private CanvasBitmap? _cellHeightmapBitmap;

    // --- Heightmap tinting ---
    private HeightmapColorScheme _currentColorScheme = HeightmapColorScheme.Amber;

    // ========================================================================
    // Cursor Helpers
    // ========================================================================

    private InputSystemCursorShape _currentCursorShape = InputSystemCursorShape.Arrow;
    private float? _currentDefaultWaterHeight;
    private WorldViewData? _data;

    // --- Filtered markers for the selected worldspace ---
    private List<PlacedReference> _filteredMarkers = [];
    private bool _hideDisabledActors = true;

    // --- Hover / Selection ---
    private PlacedReference? _hoveredObject;
    private bool _isPanning;
    private bool _legendExpanded = true;

    // --- Map marker icon bitmaps (loaded from embedded PNGs, tinted to scheme color) ---
    private Dictionary<MapMarkerType, CanvasBitmap>? _markerIconBitmaps;

    // --- State ---
    private ViewMode _mode = ViewMode.WorldOverview;
    private Vector2 _panOffset;
    private Vector2 _panOffsetAtStart;
    private Vector2 _panStartScreen;

    // --- Click detection ---
    private Vector2 _pointerDownScreen;
    private bool _pointerWasDragged;
    private CellRecord? _selectedCell;
    private PlacedReference? _selectedObject;
    private WorldspaceRecord? _selectedWorldspace;
    private bool _showWater = true;
    private bool _suppressNavEvents;

    // --- Unlinked exterior cells (DMP files without WRLD records) ---
    private List<CellRecord>? _unlinkedCells;

    // --- Cached Heightmap Bitmaps ---
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
    /// <summary>Raised before any internal navigation so the parent can push unified nav state.</summary>
    internal event Action? BeforeNavigate;

    // --- Events ---
    public event EventHandler<PlacedReference>? InspectObject;
    public event EventHandler<CellRecord>? InspectCell;

    internal void LoadData(WorldViewData data)
    {
        _data = data;
        _worldHeightmapDirty = true;
        _currentColorScheme = HeightmapColorScheme.DefaultForFile(data.SourceFilePath);
        _cachedGrayscale = data.HeightmapGrayscale;
        _cachedWaterMask = data.HeightmapWaterMask;
        _cachedHmWidth = data.HeightmapPixelWidth;
        _cachedHmHeight = data.HeightmapPixelHeight;

        // Populate color scheme combo box
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
            var name = FormatWorldspaceName(ws);
            WorldspaceComboBox.Items.Add($"{name} \u2014 {ws.Cells.Count} cells");
        }

        if (data.UnlinkedExteriorCells.Count > 0)
        {
            WorldspaceComboBox.Items.Add($"Unlinked Exterior ({data.UnlinkedExteriorCells.Count} cells)");
        }

        // Update toolbar button labels with counts
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
    }

    private void LegendToggle_Click(object sender, RoutedEventArgs e)
    {
        _legendExpanded = !_legendExpanded;
        LegendPanel.Visibility = _legendExpanded ? Visibility.Visible : Visibility.Collapsed;
        LegendToggleIcon.Glyph = _legendExpanded ? "\uE70D" : "\uE70E";
    }

    private void BuildLegendPanel()
    {
        LegendPanel.Children.Clear();
        var grayBorder = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 100, 100, 100));
        var grayFill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
        var graySwatchBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(255, 100, 100, 100));
        var whiteBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.White);
        var dimTextBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Color.FromArgb(128, 255, 255, 255));

        foreach (var category in Enum.GetValues<PlacedObjectCategory>())
        {
            var color = GetCategoryColor(category);
            var colorBorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            var colorFillBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Color.FromArgb(60, color.R, color.G, color.B));
            var colorSwatchBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);

            var swatch = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(category == PlacedObjectCategory.MapMarker ? 5 : 2),
                Background = colorSwatchBrush
            };
            var label = new TextBlock
            {
                Text = GetCategoryDisplayName(category),
                FontSize = 10,
                Foreground = whiteBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            var content = new StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            content.Children.Add(swatch);
            content.Children.Add(label);

            var item = new Border
            {
                Child = content,
                BorderBrush = colorBorderBrush,
                Background = colorFillBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var enabled = true;
            var capturedCategory = category;

            // Use PointerPressed (handled) to prevent the canvas from capturing the pointer,
            // and PointerReleased for the toggle. Using Tapped alone is unreliable because
            // the canvas's PointerPressed handler calls CapturePointer, which can steal the
            // release event and prevent the Tapped gesture from completing.
            item.PointerPressed += (_, args) => args.Handled = true;
            item.PointerReleased += (_, args) =>
            {
                args.Handled = true;
                enabled = !enabled;
                if (enabled)
                {
                    _hiddenCategories.Remove(capturedCategory);
                    item.BorderBrush = colorBorderBrush;
                    item.Background = colorFillBrush;
                    swatch.Background = colorSwatchBrush;
                    label.Foreground = whiteBrush;
                }
                else
                {
                    _hiddenCategories.Add(capturedCategory);
                    item.BorderBrush = grayBorder;
                    item.Background = grayFill;
                    swatch.Background = graySwatchBrush;
                    label.Foreground = dimTextBrush;
                }

                MapCanvas.Invalidate();
            };

            LegendPanel.Children.Add(item);
        }
    }

    private void SetCanvasMode(bool canvasVisible)
    {
        MapCanvas.Visibility = canvasVisible ? Visibility.Visible : Visibility.Collapsed;
        LegendOverlay.Visibility = canvasVisible ? Visibility.Visible : Visibility.Collapsed;
        FilterCheckboxPanel.Visibility = canvasVisible ? Visibility.Visible : Visibility.Collapsed;
        CellBrowserPanel.Visibility = canvasVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    ///     Deselects the worldspace combo box so that re-selecting the same entry
    ///     will fire SelectionChanged (otherwise the user can't navigate back to the
    ///     worldspace view when there's only one worldspace).
    /// </summary>
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

    public void SelectObject(PlacedReference? obj)
    {
        _selectedObject = obj;
        MapCanvas.Invalidate();
    }

    public void Reset()
    {
        _data = null;
        _selectedWorldspace = null;
        _unlinkedCells = null;
        _selectedCell = null;
        _selectedObject = null;
        _mode = ViewMode.WorldOverview;
        _activeBrowser = BrowserMode.None;
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
        if (_data == null || WorldspaceComboBox.SelectedIndex < 0)
        {
            return;
        }

        var idx = WorldspaceComboBox.SelectedIndex;

        // Dropdown now only contains worldspaces + optional "Unlinked Exterior" entry
        var unlinkedIdx = _data.UnlinkedExteriorCells.Count > 0 ? _data.Worldspaces.Count : -1;

        if (idx >= 0 && idx < _data.Worldspaces.Count)
        {
            // Worldspace selected
            _selectedWorldspace = _data.Worldspaces[idx];
            _unlinkedCells = null;
            _mode = ViewMode.WorldOverview;
            _activeBrowser = BrowserMode.None;
            _selectedCell = null;

            _worldHeightmapBitmap?.Dispose();
            _worldHeightmapBitmap = null;
            _worldHeightmapDirty = true;
            _worldHmMinX = _worldHmMaxY = _worldHmPixelWidth = _worldHmPixelHeight = 0;
            _currentDefaultWaterHeight = _selectedWorldspace.DefaultWaterHeight;
            _cellHeightmapBitmap?.Dispose();
            _cellHeightmapBitmap = null;
            _filteredMarkers = _data.MarkersByWorldspace
                .GetValueOrDefault(_selectedWorldspace.FormId) ?? [];
            BuildCellGridLookup();
            SetCanvasMode(true);
            ExportButton.IsEnabled = true;
            ZoomToFitWorldspace();
            MapCanvas.Invalidate();
        }
        else if (idx == unlinkedIdx)
        {
            // Unlinked exterior cells (DMP files without WRLD records)
            _selectedWorldspace = null;
            _unlinkedCells = _data.UnlinkedExteriorCells;
            _mode = ViewMode.WorldOverview;
            _activeBrowser = BrowserMode.None;
            _selectedCell = null;

            _worldHeightmapBitmap?.Dispose();
            _worldHeightmapBitmap = null;
            _worldHeightmapDirty = true;
            _worldHmMinX = _worldHmMaxY = _worldHmPixelWidth = _worldHmPixelHeight = 0;
            _currentDefaultWaterHeight = null;
            _cellHeightmapBitmap?.Dispose();
            _cellHeightmapBitmap = null;
            _filteredMarkers = _data.UnlinkedMapMarkers;
            BuildCellGridLookup();
            SetCanvasMode(true);
            ExportButton.IsEnabled = true;
            ZoomToFitWorldspace();
            MapCanvas.Invalidate();
        }
    }

    private void ColorSchemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = ColorSchemeComboBox.SelectedIndex;
        if (idx < 0 || idx >= HeightmapColorScheme.Presets.Length)
        {
            return;
        }

        _currentColorScheme = HeightmapColorScheme.Presets[idx];

        // Re-tint without recomputing grayscale — dispose bitmaps and rebuild
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        _worldHeightmapDirty = true;
        MapCanvas.Invalidate();
    }

    /// <summary>Captures the current World Map state for unified navigation.</summary>
    internal WorldNavState CaptureNavState() => new(
        _mode, _activeBrowser,
        WorldspaceComboBox.SelectedIndex,
        _selectedCell?.FormId);

    /// <summary>Restores a previously captured World Map state.</summary>
    internal void RestoreNavState(WorldNavState state)
    {
        _suppressNavEvents = true;
        _selectedCell = null;
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        HoverInfoText.Text = "";

        switch (state.Mode)
        {
            case ViewMode.CellBrowser:
                // Restore to a browser view
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
                // Restore worldspace first if needed
                if (state.WorldspaceComboIndex >= 0 &&
                    state.WorldspaceComboIndex != WorldspaceComboBox.SelectedIndex)
                {
                    WorldspaceComboBox.SelectedIndex = state.WorldspaceComboIndex;
                }

                // Find and navigate to the cell
                var cell = FindCellByFormId(state.CellFormId.Value);
                if (cell != null)
                {
                    NavigateToCell(cell);
                }

                break;

            default:
                // WorldOverview — restore worldspace selection
                if (state.WorldspaceComboIndex >= 0 &&
                    state.WorldspaceComboIndex < WorldspaceComboBox.Items.Count)
                {
                    WorldspaceComboBox.SelectedIndex = state.WorldspaceComboIndex;
                }

                break;
        }

        _suppressNavEvents = false;
    }

    /// <summary>Fires BeforeNavigate unless suppressed (during restore).</summary>
    private void NotifyBeforeNavigate()
    {
        if (!_suppressNavEvents)
        {
            BeforeNavigate?.Invoke();
        }
    }

    private CellRecord? FindCellByFormId(uint formId)
    {
        // Check current worldspace cells
        if (_selectedWorldspace != null)
        {
            var cell = _selectedWorldspace.Cells.Find(c => c.FormId == formId);
            if (cell != null)
            {
                return cell;
            }
        }

        // Check all cells
        return _data?.AllCells.Find(c => c.FormId == formId);
    }

    // Navigation buttons are now managed by the parent unified navigation system.

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == ViewMode.CellDetail && _selectedCell != null)
        {
            ZoomToFitCell(_selectedCell);
        }
        else if (GetActiveCells().Count > 0)
        {
            ZoomToFitWorldspace();
        }

        MapCanvas.Invalidate();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null)
        {
            return;
        }

        var cells = GetActiveCells();
        var cellsWithGrid = cells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
        if (cellsWithGrid.Count == 0)
        {
            return;
        }

        var minGridX = cellsWithGrid.Min(c => c.GridX!.Value);
        var maxGridX = cellsWithGrid.Max(c => c.GridX!.Value);
        var minGridY = cellsWithGrid.Min(c => c.GridY!.Value);
        var maxGridY = cellsWithGrid.Max(c => c.GridY!.Value);

        var gridW = maxGridX - minGridX + 1;
        var gridH = maxGridY - minGridY + 1;

        // Derive image size: long edge = ExportLongEdge, short edge proportional.
        // pixelsPerCell is deterministic from grid extent (same worldspace = same scale).
        var maxGridDim = Math.Max(gridW, gridH);
        var pixelsPerCell = ExportLongEdge / maxGridDim;
        if (pixelsPerCell < 1)
        {
            pixelsPerCell = 1;
        }

        var imageW = gridW * pixelsPerCell;
        var imageH = gridH * pixelsPerCell;

        var wsName = _selectedWorldspace?.EditorId
                     ?? _selectedWorldspace?.FullName
                     ?? "worldspace";

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        picker.SuggestedFileName = $"{wsName}_map";
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        ExportButton.IsEnabled = false;
        try
        {
            await ExportWorldspacePngAsync(file.Path, imageW, imageH, pixelsPerCell,
                minGridX, maxGridX, minGridY, maxGridY);
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    private void InteriorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null || _data.InteriorCells.Count == 0)
        {
            return;
        }

        NotifyBeforeNavigate();

        _selectedWorldspace = null;
        _unlinkedCells = null;
        _mode = ViewMode.CellBrowser;
        _activeBrowser = BrowserMode.Interiors;
        _selectedCell = null;
        _filteredMarkers = [];
        SetCanvasMode(false);
        ExportButton.IsEnabled = false;
        ClearWorldspaceSelection();
        FilterHasObjects.IsChecked = false;
        FilterNamedOnly.IsChecked = false;
        PopulateInteriorCellBrowser();
    }

    private void AllCellsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null)
        {
            return;
        }

        NotifyBeforeNavigate();

        _selectedWorldspace = null;
        _unlinkedCells = null;
        _mode = ViewMode.CellBrowser;
        _activeBrowser = BrowserMode.AllCells;
        _selectedCell = null;
        _filteredMarkers = [];
        SetCanvasMode(false);
        ExportButton.IsEnabled = false;
        ClearWorldspaceSelection();
        FilterHasObjects.IsChecked = false;
        FilterNamedOnly.IsChecked = false;
        PopulateCellBrowser();
    }

    private void CellFilter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyCellFilters();
    }

    private void ApplyCellFilters()
    {
        var query = CellSearchBox.Text?.Trim() ?? "";
        var hasObjects = FilterHasObjects.IsChecked == true;
        var namedOnly = FilterNamedOnly.IsChecked == true;

        IEnumerable<CellListItem> filtered = _allCellItems;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(i =>
                i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.GridLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Group.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (hasObjects)
        {
            filtered = filtered.Where(i => i.Cell.PlacedObjects.Count > 0);
        }

        if (namedOnly)
        {
            filtered = filtered.Where(i =>
                !string.IsNullOrEmpty(i.Cell.FullName) || !string.IsNullOrEmpty(i.Cell.EditorId));
        }

        RebuildCellListFromItems(filtered.ToList());
    }

    // ========================================================================
    // Win2D Events
    // ========================================================================

    private void MapCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(Color.FromArgb(255, 20, 20, 25));

        if (_data == null)
        {
            return;
        }

        // Build heightmap bitmap if needed
        if (_worldHeightmapDirty && GetActiveCells().Count > 0)
        {
            BuildWorldHeightmapBitmap(sender);
            _worldHeightmapDirty = false;
        }

        if (_mode == ViewMode.WorldOverview)
        {
            DrawWorldOverview(ds);
        }
        else
        {
            DrawCellDetail(ds);
        }

        // HUD (screen-space)
        ds.Transform = Matrix3x2.Identity;
        ZoomLevelText.Text = $"{_zoom:P0}";
    }

    // ========================================================================
    // World Overview Rendering
    // ========================================================================

    private void DrawWorldOverview(CanvasDrawingSession ds)
    {
        ds.Transform = GetViewTransform();

        // 1. Heightmap background
        if (_worldHeightmapBitmap != null)
        {
            var pixelScale = CellWorldSize / HmGridSize;
            var bitmapWorldW = _worldHmPixelWidth * pixelScale;
            var bitmapWorldH = _worldHmPixelHeight * pixelScale;
            var bitmapX = _worldHmMinX * CellWorldSize;
            var bitmapY = -(_worldHmMaxY + 1) * CellWorldSize;

            ds.DrawImage(_worldHeightmapBitmap,
                new Rect(bitmapX, bitmapY, bitmapWorldW, bitmapWorldH));
        }

        // 2. Cell grid
        DrawCellGrid(ds);

        // 3. Placed objects (LOD-based)
        var activeCells = GetActiveCells();
        if (_zoom > 0.05f && activeCells.Count > 0)
        {
            var (tlWorld, brWorld) = GetVisibleWorldBounds();

            // 3a. Regular cells (with grid coordinates)
            // Note: Persistent cells (HasPersistentObjects) may have grid coords like (0,0)
            // but hold objects scattered across the entire worldspace.  Skip grid-based culling
            // for those cells and rely on per-object IsPointInView instead.
            foreach (var cell in activeCells)
            {
                if (!cell.HasPersistentObjects && !IsCellVisible(cell, tlWorld, brWorld))
                {
                    continue;
                }

                foreach (var obj in cell.PlacedObjects)
                {
                    if (_hiddenCategories.Contains(GetObjectCategory(obj)))
                    {
                        continue;
                    }

                    if (_hideDisabledActors && obj.IsInitiallyDisabled)
                    {
                        continue;
                    }

                    if (!IsPointInView(obj.X, -obj.Y, tlWorld, brWorld, GetObjectViewMargin(obj)))
                    {
                        continue;
                    }

                    if (_zoom > 0.07f)
                    {
                        DrawPlacedObjectBox(ds, obj, outlineOnly: true);
                    }
                    else
                    {
                        DrawPlacedObjectDot(ds, obj);
                    }
                }
            }
        }

        // 4. Map markers (always visible)
        DrawMapMarkers(ds);

        // 4b. NPC/Creature dots (always visible)
        DrawActorDots(ds);

        // 4c. Save overlay markers (save file positions)
        DrawSaveOverlay(ds);

        // 5. Selected object highlight
        if (_selectedObject != null)
        {
            DrawSelectedObjectHighlight(ds, _selectedObject);
            DrawSpawnOverlay(ds, _selectedObject);
        }

        // 6. Hovered object highlight (overview)
        if (_hoveredObject != null)
        {
            DrawPlacedObjectHighlight(ds, _hoveredObject);
        }
    }

    private void DrawCellGrid(CanvasDrawingSession ds)
    {
        if (GetActiveCells().Count == 0)
        {
            return;
        }

        var (tlWorld, brWorld) = GetVisibleWorldBounds();
        var startCellX = (int)Math.Floor(Math.Min(tlWorld.X, brWorld.X) / CellWorldSize) - 1;
        var endCellX = (int)Math.Ceiling(Math.Max(tlWorld.X, brWorld.X) / CellWorldSize) + 1;
        var startCellY = (int)Math.Floor(Math.Min(tlWorld.Y, brWorld.Y) / CellWorldSize) - 1;
        var endCellY = (int)Math.Ceiling(Math.Max(tlWorld.Y, brWorld.Y) / CellWorldSize) + 1;

        // Clamp to reasonable range
        startCellX = Math.Max(startCellX, -200);
        endCellX = Math.Min(endCellX, 200);
        startCellY = Math.Max(startCellY, -200);
        endCellY = Math.Min(endCellY, 200);

        // When the worldspace has no heightmap data, fill existing cells with black
        // so they look the same as heightmap gaps (black) rather than transparent.
        if (_worldHeightmapBitmap == null && _cellGridLookup is { Count: > 0 })
        {
            var cellFill = Color.FromArgb(255, 8, 8, 10);
            foreach (var ((cx, cy), _) in _cellGridLookup)
            {
                if (cx < startCellX || cx > endCellX)
                {
                    continue;
                }

                // Grid Y is inverted in world space
                var worldLeft = cx * CellWorldSize;
                var worldTop = -(cy + 1) * CellWorldSize;
                ds.FillRectangle(worldLeft, worldTop, CellWorldSize, CellWorldSize, cellFill);
            }
        }

        var gridColor = Color.FromArgb(40, 255, 255, 255);
        var lineWidth = 1f / _zoom;

        // Vertical lines
        for (var cx = startCellX; cx <= endCellX; cx++)
        {
            var worldX = cx * CellWorldSize;
            ds.DrawLine(worldX, startCellY * CellWorldSize, worldX, endCellY * CellWorldSize, gridColor, lineWidth);
        }

        // Horizontal lines
        for (var cy = startCellY; cy <= endCellY; cy++)
        {
            var worldY = cy * CellWorldSize;
            ds.DrawLine(startCellX * CellWorldSize, worldY, endCellX * CellWorldSize, worldY, gridColor, lineWidth);
        }

        // Cell coordinate labels at sufficient zoom
        if (_zoom > 0.05f)
        {
            var labelColor = Color.FromArgb(100, 255, 255, 255);
            using var textFormat = new CanvasTextFormat
            {
                FontSize = 10f / _zoom,
                FontFamily = "Consolas"
            };

            // Only label cells that have data
            foreach (var cell in GetActiveCells())
            {
                if (!cell.GridX.HasValue || !cell.GridY.HasValue)
                {
                    continue;
                }

                var cx = cell.GridX.Value;
                var cy = cell.GridY.Value;
                var labelX = cx * CellWorldSize + 50;
                var labelY = -(cy + 1) * CellWorldSize + 50;

                if (!IsPointInView(labelX, labelY, tlWorld, brWorld, CellWorldSize))
                {
                    continue;
                }

                ds.DrawText($"{cx},{cy}", labelX, labelY, labelColor, textFormat);
            }
        }
    }

    private void DrawMapMarkers(CanvasDrawingSession ds)
    {
        // Map markers are always visible regardless of zoom level.
        if (_data == null || _filteredMarkers.Count == 0 ||
            _hiddenCategories.Contains(PlacedObjectCategory.MapMarker))
        {
            return;
        }

        EnsureMarkerIcons(ds);
        var (tlWorld, brWorld) = GetVisibleWorldBounds();
        var markerSize = 16f / _zoom;

        using var labelFormat = new CanvasTextFormat
        {
            FontSize = 10f / _zoom,
            FontFamily = "Segoe UI"
        };

        // Fallback glyph format for marker types without an icon
        using var glyphFormat = new CanvasTextFormat
        {
            FontSize = 12f / _zoom,
            FontFamily = "Segoe MDL2 Assets",
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };

        // Tint color from the current HUD color scheme
        var tint = Color.FromArgb(255, _currentColorScheme.R, _currentColorScheme.G, _currentColorScheme.B);

        foreach (var marker in _filteredMarkers)
        {
            var pos = new Vector2(marker.X, -marker.Y);

            if (!IsPointInView(pos.X, pos.Y, tlWorld, brWorld, markerSize * 2))
            {
                continue;
            }

            var destRect = new Rect(
                pos.X - markerSize / 2, pos.Y - markerSize / 2,
                markerSize, markerSize);

            // Draw icon tinted to scheme color, or fall back to colored circle + glyph
            if (marker.MarkerType.HasValue &&
                _markerIconBitmaps?.TryGetValue(marker.MarkerType.Value, out var icon) == true)
            {
                DrawTintedIcon(ds, icon, destRect, tint);
            }
            else
            {
                var color = GetMarkerColor(marker.MarkerType);
                var radius = markerSize / 2;
                ds.FillCircle(pos, radius, WithAlpha(color, 200));
                ds.DrawCircle(pos, radius, Colors.White, 1f / _zoom);
                var glyph = GetMarkerGlyph(marker.MarkerType);
                ds.DrawText(glyph, destRect, Colors.White, glyphFormat);
            }

            // Label at sufficient zoom
            if (_zoom > 0.05f && !string.IsNullOrEmpty(marker.MarkerName))
            {
                var labelPos = new Vector2(pos.X + markerSize / 2 + 2f / _zoom, pos.Y - markerSize / 4);
                ds.DrawText(marker.MarkerName, labelPos, tint, labelFormat);
            }
        }
    }

    private void DrawActorDots(CanvasDrawingSession ds)
    {
        // All actor dots (including persistent cell NPCs/Creatures) require minimum zoom.
        if (_data == null || _zoom <= 0.02f)
        {
            return;
        }

        var npcHidden = _hiddenCategories.Contains(PlacedObjectCategory.Npc);
        var creatureHidden = _hiddenCategories.Contains(PlacedObjectCategory.Creature);
        if (npcHidden && creatureHidden)
        {
            return;
        }

        var (tlWorld, brWorld) = GetVisibleWorldBounds();
        var dotRadius = 5f / _zoom;
        var outlineWidth = 1f / _zoom;
        var npcColor = GetCategoryColor(PlacedObjectCategory.Npc);
        var creatureColor = GetCategoryColor(PlacedObjectCategory.Creature);

        foreach (var cell in GetActiveCells())
        {
            // For grid cells, skip those not in viewport.
            // Persistent cells hold objects scattered across the worldspace,
            // so bypass viewport culling (per-object IsPointInView handles it).
            if (cell.GridX.HasValue && cell.GridY.HasValue && !cell.HasPersistentObjects
                && !IsCellVisible(cell, tlWorld, brWorld))
            {
                continue;
            }

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.IsMapMarker)
                {
                    continue;
                }

                if (_hideDisabledActors && obj.IsInitiallyDisabled)
                {
                    continue;
                }

                Color color;
                if (obj.RecordType == "ACHR" && !npcHidden)
                {
                    color = npcColor;
                }
                else if (obj.RecordType == "ACRE" && !creatureHidden)
                {
                    color = creatureColor;
                }
                else
                {
                    continue;
                }

                var pos = new Vector2(obj.X, -obj.Y);
                if (!IsPointInView(pos.X, pos.Y, tlWorld, brWorld, dotRadius * 2))
                {
                    continue;
                }

                var fillAlpha = obj.IsInitiallyDisabled ? (byte)60 : (byte)180;
                var outlineAlpha = obj.IsInitiallyDisabled ? (byte)80 : (byte)255;
                ds.FillCircle(pos, dotRadius, WithAlpha(color, fillAlpha));
                ds.DrawCircle(pos, dotRadius, WithAlpha(Colors.White, outlineAlpha), outlineWidth);
            }
        }
    }

    private void DrawSaveOverlay(CanvasDrawingSession ds)
    {
        if (_data?.SaveOverlayMarkers == null || _data.SaveOverlayMarkers.Count == 0)
        {
            return;
        }

        var (tlWorld, brWorld) = GetVisibleWorldBounds();
        var dotRadius = 4f / _zoom;
        var outlineWidth = 1f / _zoom;

        var achrColor = Color.FromArgb(255, 0, 200, 200); // Teal for NPCs
        var acreColor = Color.FromArgb(255, 255, 140, 0); // Orange for creatures
        var refrColor = Color.FromArgb(255, 120, 120, 120); // Gray for objects

        foreach (var obj in _data.SaveOverlayMarkers)
        {
            var pos = new Vector2(obj.X, -obj.Y);
            if (!IsPointInView(pos.X, pos.Y, tlWorld, brWorld, dotRadius * 2))
            {
                continue;
            }

            var color = obj.RecordType switch
            {
                "ACHR" => achrColor,
                "ACRE" => acreColor,
                _ => refrColor
            };

            ds.FillCircle(pos, dotRadius, WithAlpha(color, 150));
            ds.DrawCircle(pos, dotRadius, WithAlpha(Colors.White, 200), outlineWidth);
        }

        // Player marker (prominent)
        if (_data.PlayerPosition is var (px, py, _))
        {
            var playerPos = new Vector2(px, -py);
            if (IsPointInView(playerPos.X, playerPos.Y, tlWorld, brWorld, 20f / _zoom))
            {
                var playerRadius = 8f / _zoom;
                var playerOutline = 2f / _zoom;
                ds.FillCircle(playerPos, playerRadius, Color.FromArgb(220, 255, 215, 0)); // Gold
                ds.DrawCircle(playerPos, playerRadius, Colors.White, playerOutline);
                ds.DrawCircle(playerPos, playerRadius * 1.5f, Color.FromArgb(100, 255, 215, 0), playerOutline);
            }
        }
    }

    // ========================================================================
    // Cell Detail Rendering
    // ========================================================================

    private void DrawCellDetail(CanvasDrawingSession ds)
    {
        if (_selectedCell == null)
        {
            return;
        }

        ds.Transform = GetViewTransform();

        // 1. Cell heightmap background
        if (_cellHeightmapBitmap != null && _selectedCell.GridX.HasValue && _selectedCell.GridY.HasValue)
        {
            var cellX = _selectedCell.GridX.Value;
            var cellY = _selectedCell.GridY.Value;
            var originX = cellX * CellWorldSize;
            var originY = -(cellY + 1) * CellWorldSize;

            ds.DrawImage(_cellHeightmapBitmap,
                new Rect(originX, originY, CellWorldSize, CellWorldSize));
        }

        // 2. Cell boundary
        if (_selectedCell.GridX.HasValue && _selectedCell.GridY.HasValue)
        {
            var cellX = _selectedCell.GridX.Value;
            var cellY = _selectedCell.GridY.Value;
            var originX = cellX * CellWorldSize;
            var originY = -(cellY + 1) * CellWorldSize;
            ds.DrawRectangle(new Rect(originX, originY, CellWorldSize, CellWorldSize),
                Color.FromArgb(80, 255, 255, 255), 2f / _zoom);
        }

        // 3. Placed objects
        var (tlWorld, brWorld) = GetVisibleWorldBounds();
        foreach (var obj in _selectedCell.PlacedObjects)
        {
            if (_hiddenCategories.Contains(GetObjectCategory(obj)))
            {
                continue;
            }

            if (_hideDisabledActors && obj.IsInitiallyDisabled)
            {
                continue;
            }

            if (!IsPointInView(obj.X, -obj.Y, tlWorld, brWorld, GetObjectViewMargin(obj)))
            {
                continue;
            }

            DrawPlacedObjectBox(ds, obj);
        }

        // 4. Selected object highlight
        if (_selectedObject != null)
        {
            DrawSelectedObjectHighlight(ds, _selectedObject);
            DrawSpawnOverlay(ds, _selectedObject);
        }

        // 5. Hovered object highlight
        if (_hoveredObject != null)
        {
            DrawPlacedObjectHighlight(ds, _hoveredObject);
        }
    }

    private void DrawPlacedObjectBox(CanvasDrawingSession ds, PlacedReference obj, bool outlineOnly = false)
    {
        if (_data == null)
        {
            return;
        }

        var category = obj.IsMapMarker
            ? PlacedObjectCategory.MapMarker
            : obj.RecordType switch
            {
                "ACHR" => PlacedObjectCategory.Npc,
                "ACRE" => PlacedObjectCategory.Creature,
                _ => _data.CategoryIndex.GetValueOrDefault(obj.BaseFormId, PlacedObjectCategory.Unknown)
            };
        var color = GetCategoryColor(category);
        var pos = new Vector2(obj.X, -obj.Y);
        var lineWidth = 1f / _zoom;

        if (_data.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds))
        {
            var halfW = (bounds.X2 - bounds.X1) * 0.5f * obj.Scale;
            var halfH = (bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale;

            // Skip degenerate bounds
            if (halfW < 1f && halfH < 1f)
            {
                ds.FillCircle(pos, 6f / _zoom, WithAlpha(color, 120));
                ds.DrawCircle(pos, 6f / _zoom, color, lineWidth);
                return;
            }

            if (outlineOnly)
            {
                // Fast path: 4 line draws, no geometry allocation
                var rotation = Matrix3x2.CreateRotation(-obj.RotZ, pos);
                Span<Vector2> corners = stackalloc Vector2[4];
                corners[0] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y - halfH), rotation);
                corners[1] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y - halfH), rotation);
                corners[2] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y + halfH), rotation);
                corners[3] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y + halfH), rotation);
                ds.DrawLine(corners[0], corners[1], color, lineWidth);
                ds.DrawLine(corners[1], corners[2], color, lineWidth);
                ds.DrawLine(corners[2], corners[3], color, lineWidth);
                ds.DrawLine(corners[3], corners[0], color, lineWidth);
            }
            else
            {
                using var geometry = CreateRotatedRectGeometry(ds, pos, halfW, halfH, obj.RotZ);
                ds.FillGeometry(geometry, WithAlpha(color, 60));
                ds.DrawGeometry(geometry, color, lineWidth);
            }
        }
        else
        {
            // No OBND - fallback circle
            var radius = 12f / _zoom;
            ds.FillCircle(pos, radius, WithAlpha(color, 80));
            ds.DrawCircle(pos, radius, color, lineWidth);
        }

        // Click-point circle at center (visible selection target)
        var clickRadius = 6f / _zoom;
        ds.FillCircle(pos, clickRadius, color);
        ds.DrawCircle(pos, clickRadius, Colors.White, 1f / _zoom);
    }

    private void DrawPlacedObjectDot(CanvasDrawingSession ds, PlacedReference obj)
    {
        if (_data == null)
        {
            return;
        }

        var category = obj.RecordType switch
        {
            "ACHR" => PlacedObjectCategory.Npc,
            "ACRE" => PlacedObjectCategory.Creature,
            _ => _data.CategoryIndex.GetValueOrDefault(obj.BaseFormId, PlacedObjectCategory.Unknown)
        };
        var color = GetCategoryColor(category);
        var pos = new Vector2(obj.X, -obj.Y);
        var radius = 4f / _zoom;
        ds.FillCircle(pos, radius, color);
    }

    private void DrawPlacedObjectHighlight(CanvasDrawingSession ds, PlacedReference obj)
    {
        DrawObjectOutline(ds, obj, Colors.Yellow, 3f, 12f);
    }

    private void DrawSelectedObjectHighlight(CanvasDrawingSession ds, PlacedReference obj)
    {
        DrawObjectOutline(ds, obj, Color.FromArgb(255, 0, 200, 255), 4f, 14f);
    }

    /// <summary>
    ///     Draws a rotated rectangle outline (or fallback circle) around a placed object.
    /// </summary>
    private void DrawObjectOutline(
        CanvasDrawingSession ds, PlacedReference obj, Color color, float strokeWidth, float fallbackRadius)
    {
        if (_data == null)
        {
            return;
        }

        var pos = new Vector2(obj.X, -obj.Y);

        if (_data.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds))
        {
            var halfW = (bounds.X2 - bounds.X1) * 0.5f * obj.Scale;
            var halfH = (bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale;

            if (halfW >= 1f || halfH >= 1f)
            {
                using var geometry = CreateRotatedRectGeometry(ds, pos, halfW, halfH, obj.RotZ);
                ds.DrawGeometry(geometry, color, strokeWidth / _zoom);
                return;
            }
        }

        ds.DrawCircle(pos, fallbackRadius / _zoom, color, strokeWidth / _zoom);
    }

    /// <summary>
    ///     Creates a rotated rectangle CanvasGeometry from center, half-extents, and rotation.
    /// </summary>
    private static CanvasGeometry CreateRotatedRectGeometry(
        ICanvasResourceCreator resourceCreator, Vector2 center, float halfW, float halfH, float rotZ)
    {
        var rotation = Matrix3x2.CreateRotation(-rotZ, center);
        Span<Vector2> corners = stackalloc Vector2[4];
        corners[0] = Vector2.Transform(new Vector2(center.X - halfW, center.Y - halfH), rotation);
        corners[1] = Vector2.Transform(new Vector2(center.X + halfW, center.Y - halfH), rotation);
        corners[2] = Vector2.Transform(new Vector2(center.X + halfW, center.Y + halfH), rotation);
        corners[3] = Vector2.Transform(new Vector2(center.X - halfW, center.Y + halfH), rotation);

        var pathBuilder = new CanvasPathBuilder(resourceCreator);
        pathBuilder.BeginFigure(corners[0]);
        pathBuilder.AddLine(corners[1]);
        pathBuilder.AddLine(corners[2]);
        pathBuilder.AddLine(corners[3]);
        pathBuilder.EndFigure(CanvasFigureLoop.Closed);

        return CanvasGeometry.CreatePath(pathBuilder);
    }

    private void DrawSpawnOverlay(CanvasDrawingSession ds, PlacedReference selectedObj)
    {
        if (_data?.SpawnIndex == null)
        {
            return;
        }

        var spawnIndex = _data.SpawnIndex;
        var isAchr = selectedObj.RecordType == "ACHR";
        var isAcre = selectedObj.RecordType == "ACRE";
        if (!isAchr && !isAcre)
        {
            return;
        }

        // Color: green for NPC sources, red for Creature sources
        var overlayColor = isAchr
            ? Color.FromArgb(50, 0, 200, 0)
            : Color.FromArgb(50, 220, 50, 50);
        var overlayBorder = isAchr
            ? Color.FromArgb(120, 0, 200, 0)
            : Color.FromArgb(120, 220, 50, 50);

        // Resolve actors from leveled list or direct placement
        var actorFormIds = new List<uint>();
        if (spawnIndex.LeveledListEntries.TryGetValue(selectedObj.BaseFormId, out var resolved))
        {
            actorFormIds.AddRange(resolved.Distinct());
        }
        else
        {
            actorFormIds.Add(selectedObj.BaseFormId);
        }

        // Draw InCell package highlights
        foreach (var actorFid in actorFormIds)
        {
            if (!spawnIndex.ActorToPackageCells.TryGetValue(actorFid, out var cells))
            {
                continue;
            }

            foreach (var cellFid in cells.Distinct())
            {
                if (_data.CellByFormId.TryGetValue(cellFid, out var cell) &&
                    cell.GridX.HasValue && cell.GridY.HasValue)
                {
                    var originX = cell.GridX.Value * CellWorldSize;
                    var originY = -(cell.GridY.Value + 1) * CellWorldSize;
                    ds.FillRectangle(
                        new Rect(originX, originY, CellWorldSize, CellWorldSize),
                        overlayColor);
                    ds.DrawRectangle(
                        new Rect(originX, originY, CellWorldSize, CellWorldSize),
                        overlayBorder, 2f / _zoom);
                }
            }
        }

        // Draw NearRef package circles
        if (_data.RefPositionIndex != null)
        {
            foreach (var actorFid in actorFormIds)
            {
                if (!spawnIndex.ActorToPackageRefs.TryGetValue(actorFid, out var refs))
                {
                    continue;
                }

                foreach (var refLoc in refs)
                {
                    if (_data.RefPositionIndex.TryGetValue(refLoc.RefFormId, out var refPos))
                    {
                        var center = new Vector2(refPos.X, -refPos.Y);
                        var radius = refLoc.Radius > 0 ? (float)refLoc.Radius : 500f;
                        ds.FillCircle(center, radius, overlayColor);
                        ds.DrawCircle(center, radius, overlayBorder, 2f / _zoom);
                    }
                }
            }
        }
    }

    // ========================================================================
    // Heightmap Bitmap Building
    // ========================================================================

    private void BuildWorldHeightmapBitmap(CanvasControl canvas)
    {
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;

        // Use pre-computed grayscale/waterMask from background thread when available
        if (_cachedGrayscale != null && _cachedHmWidth > 0 &&
            _selectedWorldspace != null && _data?.Worldspaces.Count > 0 &&
            ReferenceEquals(_selectedWorldspace, _data.Worldspaces[0]))
        {
            var pixels = ApplyTintAndWater(
                _cachedGrayscale, _cachedWaterMask!, _cachedHmWidth, _cachedHmHeight,
                _currentColorScheme, _showWater);
            _worldHeightmapBitmap = CanvasBitmap.CreateFromBytes(
                canvas, pixels, _cachedHmWidth, _cachedHmHeight,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            _worldHmMinX = _data.HeightmapMinCellX;
            _worldHmMaxY = _data.HeightmapMaxCellY;
            _worldHmPixelWidth = _cachedHmWidth;
            _worldHmPixelHeight = _cachedHmHeight;
            return;
        }

        // Fallback: compute on-the-fly for non-default worldspaces or unlinked cells
        var activeCells = GetActiveCells();
        if (activeCells.Count == 0)
        {
            return;
        }

        var result = ComputeHeightmapData(activeCells, _currentDefaultWaterHeight);
        if (result == null)
        {
            return;
        }

        var (grayscale, waterMask, imgW, imgH, minX, maxY) = result.Value;
        var tintedPixels = ApplyTintAndWater(grayscale, waterMask, imgW, imgH,
            _currentColorScheme, _showWater);
        _worldHeightmapBitmap = CanvasBitmap.CreateFromBytes(
            canvas, tintedPixels, imgW, imgH,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
        _worldHmMinX = minX;
        _worldHmMaxY = maxY;
        _worldHmPixelWidth = imgW;
        _worldHmPixelHeight = imgH;
    }

    /// <summary>
    ///     Computes grayscale heightmap and water mask from a list of cells.
    ///     Stage 1 of the two-stage pipeline. Can be called from a background thread.
    /// </summary>
    internal static (byte[] Grayscale, byte[] WaterMask, int Width, int Height, int MinCellX, int MaxCellY)?
        ComputeHeightmapData(List<CellRecord> cellSource, float? defaultWaterHeight = null)
    {
        var cells = cellSource
            .Where(c => c.Heightmap != null && c.GridX.HasValue && c.GridY.HasValue)
            .ToList();

        if (cells.Count == 0)
        {
            return null;
        }

        var minX = cells.Min(c => c.GridX!.Value);
        var maxX = cells.Max(c => c.GridX!.Value);
        var minY = cells.Min(c => c.GridY!.Value);
        var maxY = cells.Max(c => c.GridY!.Value);
        var gridW = maxX - minX + 1;
        var gridH = maxY - minY + 1;
        var imgW = gridW * HmGridSize;
        var imgH = gridH * HmGridSize;

        // Compute global height range
        var globalMin = float.MaxValue;
        var globalMax = float.MinValue;
        var heightCache = new Dictionary<CellRecord, float[,]>();

        foreach (var cell in cells)
        {
            var heights = cell.Heightmap!.CalculateHeights();
            heightCache[cell] = heights;
            for (var y = 0; y < HmGridSize; y++)
            {
                for (var x = 0; x < HmGridSize; x++)
                {
                    var h = heights[y, x];
                    if (h < globalMin)
                    {
                        globalMin = h;
                    }

                    if (h > globalMax)
                    {
                        globalMax = h;
                    }
                }
            }
        }

        var globalRange = globalMax - globalMin;
        if (globalRange < 0.001f)
        {
            globalRange = 1f;
        }

        // Compute grayscale and water mask
        var grayscale = new byte[imgW * imgH];
        var waterMask = new byte[imgW * imgH];

        foreach (var cell in cells)
        {
            var heights = heightCache[cell];
            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;

            // Determine effective water height: cell-specific or worldspace default
            var waterH = cell.WaterHeight;
            if (!waterH.HasValue || waterH.Value is not (> -1e6f and < 1e6f))
            {
                waterH = defaultWaterHeight;
            }

            for (var py = 0; py < HmGridSize; py++)
            {
                for (var px = 0; px < HmGridSize; px++)
                {
                    var height = heights[HmGridSize - 1 - py, px];
                    var normalized = (height - globalMin) / globalRange;
                    var gray = (byte)(Math.Clamp(normalized, 0f, 1f) * 255);

                    var imgX = imgCellX * HmGridSize + px;
                    var imgY = imgCellY * HmGridSize + py;
                    var idx = imgY * imgW + imgX;
                    grayscale[idx] = gray;

                    // Solid water: binary below/above
                    if (waterH.HasValue && waterH.Value is > -1e6f and < 1e6f &&
                        height < waterH.Value)
                    {
                        waterMask[idx] = 180;
                    }
                }
            }
        }

        BlurWaterMask(waterMask, imgW, imgH);

        return (grayscale, waterMask, imgW, imgH, minX, maxY);
    }

    /// <summary>
    ///     Applies color tint and water overlay to grayscale heightmap data.
    ///     Stage 2 of the two-stage pipeline. Fast enough for UI thread (no height recalculation).
    /// </summary>
    internal static byte[] ApplyTintAndWater(
        byte[] grayscale, byte[] waterMask, int width, int height,
        HeightmapColorScheme scheme, bool showWater, byte alpha = 255)
    {
        var pixelCount = width * height;
        var rgba = new byte[pixelCount * 4];

        // Pre-compute tint multipliers (0..1 range)
        var tR = scheme.R / 255f;
        var tG = scheme.G / 255f;
        var tB = scheme.B / 255f;

        // Water color (untinted)
        const byte waterR = 30, waterG = 55, waterB = 120;

        for (var i = 0; i < pixelCount; i++)
        {
            var gray = grayscale[i];

            // Apply tint: grayscale * tint color
            var r = (byte)(gray * tR);
            var g = (byte)(gray * tG);
            var b = (byte)(gray * tB);

            // Apply water overlay (untinted, proportional blend from blurred mask)
            if (showWater && waterMask[i] > 0)
            {
                var waterFactor = waterMask[i] / 255f;
                r = (byte)(r + (waterR - r) * waterFactor);
                g = (byte)(g + (waterG - g) * waterFactor);
                b = (byte)(b + (waterB - b) * waterFactor);
            }

            var idx = i * 4;
            rgba[idx] = r;
            rgba[idx + 1] = g;
            rgba[idx + 2] = b;
            rgba[idx + 3] = alpha;
        }

        return rgba;
    }

    /// <summary>
    ///     Applies a 3x3 box blur to the water mask to smooth hard binary edges.
    /// </summary>
    internal static void BlurWaterMask(byte[] mask, int width, int height)
    {
        var blurred = new byte[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                var count = 0;
                var y0 = Math.Max(0, y - 1);
                var y1 = Math.Min(height - 1, y + 1);
                var x0 = Math.Max(0, x - 1);
                var x1 = Math.Min(width - 1, x + 1);

                for (var ny = y0; ny <= y1; ny++)
                {
                    for (var nx = x0; nx <= x1; nx++)
                    {
                        sum += mask[ny * width + nx];
                        count++;
                    }
                }

                blurred[y * width + x] = (byte)(sum / count);
            }
        }

        Array.Copy(blurred, mask, mask.Length);
    }

    private void BuildCellHeightmapBitmap(CanvasControl canvas, CellRecord cell)
    {
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;

        if (cell.Heightmap == null)
        {
            return;
        }

        var heights = cell.Heightmap.CalculateHeights();
        var minH = float.MaxValue;
        var maxH = float.MinValue;
        for (var y = 0; y < HmGridSize; y++)
        {
            for (var x = 0; x < HmGridSize; x++)
            {
                var h = heights[y, x];
                if (h < minH)
                {
                    minH = h;
                }

                if (h > maxH)
                {
                    maxH = h;
                }
            }
        }

        var range = maxH - minH;
        if (range < 0.001f)
        {
            range = 1f;
        }

        // Determine effective water height
        var waterH = cell.WaterHeight;
        if (!waterH.HasValue || waterH.Value is not (> -1e6f and < 1e6f))
        {
            waterH = _currentDefaultWaterHeight;
        }

        var grayscale = new byte[HmGridSize * HmGridSize];
        var waterMask = new byte[HmGridSize * HmGridSize];

        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var height = heights[HmGridSize - 1 - py, px];
                var normalized = (height - minH) / range;
                var idx = py * HmGridSize + px;
                grayscale[idx] = (byte)(Math.Clamp(normalized, 0f, 1f) * 255);

                if (waterH.HasValue && waterH.Value is > -1e6f and < 1e6f &&
                    height < waterH.Value)
                {
                    waterMask[idx] = 180;
                }
            }
        }

        BlurWaterMask(waterMask, HmGridSize, HmGridSize);

        var pixels = ApplyTintAndWater(grayscale, waterMask, HmGridSize, HmGridSize,
            _currentColorScheme, _showWater, alpha: 200);

        _cellHeightmapBitmap = CanvasBitmap.CreateFromBytes(
            canvas, pixels, HmGridSize, HmGridSize,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
    }

    // ========================================================================
    // Pan/Zoom
    // ========================================================================

    private Matrix3x2 GetViewTransform()
    {
        return Matrix3x2.CreateScale(_zoom) * Matrix3x2.CreateTranslation(_panOffset);
    }

    private Vector2 ScreenToWorld(Vector2 screen)
    {
        Matrix3x2.Invert(GetViewTransform(), out var inverse);
        return Vector2.Transform(screen, inverse);
    }

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

        // Update coordinates display
        var worldPos = ScreenToWorld(currentScreen);
        CoordsText.Text = $"X: {worldPos.X:F0}  Y: {-worldPos.Y:F0}";

        if (_isPanning)
        {
            var delta = currentScreen - _panStartScreen;
            if (delta.Length() > 3f)
            {
                _pointerWasDragged = true;
            }

            _panOffset = _panOffsetAtStart + delta;
            MapCanvas.Invalidate();
        }
        else if (_mode == ViewMode.CellDetail && _selectedCell != null)
        {
            // Hover detection
            var hitObj = HitTestPlacedObject(worldPos);
            if (hitObj != _hoveredObject)
            {
                _hoveredObject = hitObj;
                if (hitObj != null)
                {
                    var name = hitObj.BaseEditorId ?? $"0x{hitObj.BaseFormId:X8}";
                    HoverInfoText.Text =
                        $"{hitObj.RecordType}: {name} at ({hitObj.X:F0}, {hitObj.Y:F0}, {hitObj.Z:F0})";
                }
                else
                {
                    HoverInfoText.Text = "";
                }

                MapCanvas.Invalidate();
            }

            SetInteractiveCursor(hitObj != null);
        }
        else if (_mode == ViewMode.WorldOverview)
        {
            // Check map marker hover first
            var marker = HitTestMapMarker(worldPos);
            PlacedReference? overviewHitObj = null;

            if (marker != null)
            {
                var markerName = marker.MarkerName ?? "Unknown";
                var markerType = marker.MarkerType?.ToString() ?? "";
                HoverInfoText.Text = $"Marker: {markerName} ({markerType})";
                SetInteractiveCursor(true);
            }
            else
            {
                // Check placed objects (when zoomed in enough)
                overviewHitObj = HitTestPlacedObjectInOverview(worldPos);
                if (overviewHitObj != null)
                {
                    var name = overviewHitObj.BaseEditorId ?? $"0x{overviewHitObj.BaseFormId:X8}";
                    HoverInfoText.Text =
                        $"{overviewHitObj.RecordType}: {name} at ({overviewHitObj.X:F0}, {overviewHitObj.Y:F0}, {overviewHitObj.Z:F0})";
                    SetInteractiveCursor(true);
                }
                else
                {
                    // Show cell info on hover
                    var cellX = (int)Math.Floor(worldPos.X / CellWorldSize);
                    var cellY = (int)Math.Floor(-worldPos.Y / CellWorldSize);

                    if (_cellGridLookup != null &&
                        _cellGridLookup.TryGetValue((cellX, cellY), out var cell))
                    {
                        var name = cell.EditorId ?? cell.FullName ?? "";
                        HoverInfoText.Text =
                            $"Cell [{cellX}, {cellY}] {name} \u2014 {cell.PlacedObjects.Count} objects";
                        SetInteractiveCursor(true);
                    }
                    else
                    {
                        HoverInfoText.Text = $"Cell [{cellX}, {cellY}]";
                        SetInteractiveCursor(false);
                    }
                }
            }

            // Track hover for highlight rendering
            if (overviewHitObj != _hoveredObject)
            {
                _hoveredObject = overviewHitObj;
                MapCanvas.Invalidate();
            }
        }

        e.Handled = true;
    }

    private void MapCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        MapCanvas.ReleasePointerCapture(e.Pointer);

        if (_isPanning && !_pointerWasDragged)
        {
            HandleClick(_pointerDownScreen);
        }

        _isPanning = false;
        e.Handled = true;
    }

    private void MapCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MapCanvas);
        var screenPos = new Vector2((float)point.Position.X, (float)point.Position.Y);
        var delta = point.Properties.MouseWheelDelta;

        // Zoom centered on cursor
        var worldBeforeZoom = ScreenToWorld(screenPos);
        var zoomFactor = delta > 0 ? 1.15f : 1f / 1.15f;
        _zoom = Math.Clamp(_zoom * zoomFactor, MinZoom, MaxZoom);

        // Adjust pan to keep the world point under the cursor
        var newTransform = Matrix3x2.CreateScale(_zoom);
        var worldAfterZoom = Vector2.Transform(worldBeforeZoom, newTransform);
        _panOffset = screenPos - worldAfterZoom;

        MapCanvas.Invalidate();
        e.Handled = true;
    }

    // ========================================================================
    // Navigation
    // ========================================================================

    private void HandleClick(Vector2 screenPos)
    {
        var worldPos = ScreenToWorld(screenPos);

        if (_mode == ViewMode.WorldOverview)
        {
            // Check map markers first (they're drawn on top)
            var marker = HitTestMapMarker(worldPos);
            if (marker != null)
            {
                InspectObject?.Invoke(this, marker);
                return;
            }

            // Check placed objects (when zoomed in enough to see them)
            var obj = HitTestPlacedObjectInOverview(worldPos);
            if (obj != null)
            {
                InspectObject?.Invoke(this, obj);
                return;
            }

            // Find cell at click position
            if (GetActiveCells().Count > 0)
            {
                var cellX = (int)Math.Floor(worldPos.X / CellWorldSize);
                var cellY = (int)Math.Floor(-worldPos.Y / CellWorldSize);

                if (_cellGridLookup != null &&
                    _cellGridLookup.TryGetValue((cellX, cellY), out var cell))
                {
                    InspectCell?.Invoke(this, cell);
                }
            }
        }
        else if (_mode == ViewMode.CellDetail && _selectedCell != null)
        {
            var hitObj = HitTestPlacedObject(worldPos);
            if (hitObj != null)
            {
                InspectObject?.Invoke(this, hitObj);
            }
            else
            {
                // Click on empty space: deselect object, show cell info
                _selectedObject = null;
                InspectCell?.Invoke(this, _selectedCell);
                MapCanvas.Invalidate();
            }
        }
    }

    public void NavigateToCell(CellRecord cell)
    {
        NotifyBeforeNavigate();

        _selectedCell = cell;
        _mode = ViewMode.CellDetail;
        _hoveredObject = null;

        // Ensure canvas is visible (may be hidden if coming from browser)
        SetCanvasMode(true);

        // Build cell heightmap
        BuildCellHeightmapBitmap(MapCanvas, cell);

        ZoomToFitCell(cell);
        MapCanvas.Invalidate();
    }

    // ========================================================================
    // Hit Testing
    // ========================================================================

    /// <summary>
    ///     Tests whether worldPos hits the object's visual bounds (rotated AABB or circle fallback).
    ///     Returns distance to object center if hit, float.MaxValue otherwise.
    /// </summary>
    private float HitTestObjectBounds(Vector2 worldPos, PlacedReference obj)
    {
        var pos = new Vector2(obj.X, -obj.Y);

        if (_data?.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds) == true)
        {
            var halfW = (bounds.X2 - bounds.X1) * 0.5f * obj.Scale;
            var halfH = (bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale;

            if (halfW >= 1f || halfH >= 1f)
            {
                // Transform test point into object's local (unrotated) space
                // Drawing uses CreateRotation(-obj.RotZ, pos), so inverse is +obj.RotZ
                var inverseRotation = Matrix3x2.CreateRotation(obj.RotZ, pos);
                var localPoint = Vector2.Transform(worldPos, inverseRotation);

                // Add small padding for usability (5 screen pixels)
                var pad = 5f / _zoom;
                if (localPoint.X >= pos.X - halfW - pad && localPoint.X <= pos.X + halfW + pad &&
                    localPoint.Y >= pos.Y - halfH - pad && localPoint.Y <= pos.Y + halfH + pad)
                {
                    return Vector2.Distance(worldPos, pos);
                }

                return float.MaxValue;
            }
        }

        // No valid OBND — fallback to circle (matches drawn circle radius)
        var dist = Vector2.Distance(worldPos, pos);
        return dist <= 12f / _zoom ? dist : float.MaxValue;
    }

    private PlacedReference? HitTestPlacedObject(Vector2 worldPos)
    {
        if (_selectedCell == null || _data == null)
        {
            return null;
        }

        PlacedReference? closest = null;
        var closestDist = float.MaxValue;

        foreach (var obj in _selectedCell.PlacedObjects)
        {
            if (_hiddenCategories.Contains(GetObjectCategory(obj)))
            {
                continue;
            }

            if (_hideDisabledActors && obj.IsInitiallyDisabled)
            {
                continue;
            }

            var dist = HitTestObjectBounds(worldPos, obj);
            if (dist < closestDist)
            {
                closestDist = dist;
                closest = obj;
            }
        }

        return closest;
    }

    private PlacedReference? HitTestPlacedObjectInOverview(Vector2 worldPos)
    {
        if (_data == null || _zoom < 0.02f || GetActiveCells().Count == 0)
        {
            return null;
        }

        var useBounds = _zoom > 0.07f; // Matches rendering threshold (boxes vs dots)
        var hitRadius = 30f / _zoom;
        PlacedReference? closest = null;
        var closestDist = float.MaxValue;

        // Only check cells near the cursor (3x3 grid around cursor cell)
        var cellX = (int)Math.Floor(worldPos.X / CellWorldSize);
        var cellY = (int)Math.Floor(-worldPos.Y / CellWorldSize);

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (_cellGridLookup?.TryGetValue((cellX + dx, cellY + dy), out var cell) != true)
                {
                    continue;
                }

                foreach (var obj in cell!.PlacedObjects)
                {
                    if (_hiddenCategories.Contains(GetObjectCategory(obj)))
                    {
                        continue;
                    }

                    if (_hideDisabledActors && obj.IsInitiallyDisabled)
                    {
                        continue;
                    }

                    // At low zoom, only actors and map markers are rendered, skip other types
                    if (_zoom < 0.05f && obj.RecordType is not ("ACHR" or "ACRE") && !obj.IsMapMarker)
                    {
                        continue;
                    }

                    float dist;
                    if (useBounds)
                    {
                        dist = HitTestObjectBounds(worldPos, obj);
                    }
                    else
                    {
                        var objPos = new Vector2(obj.X, -obj.Y);
                        dist = Vector2.Distance(worldPos, objPos);
                        if (dist >= hitRadius)
                        {
                            dist = float.MaxValue;
                        }
                    }

                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closest = obj;
                    }
                }
            }
        }

        // Also check persistent cells — these may have grid coords (e.g., 0,0) but
        // hold objects scattered across the entire worldspace.  Match both null-grid
        // cells and cells flagged as having persistent objects.
        foreach (var cell in GetActiveCells())
        {
            if (cell.GridX.HasValue && cell.GridY.HasValue && !cell.HasPersistentObjects)
            {
                continue;
            }

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.IsMapMarker || _hiddenCategories.Contains(GetObjectCategory(obj)))
                {
                    continue;
                }

                if (_hideDisabledActors && obj.IsInitiallyDisabled)
                {
                    continue;
                }

                // At low zoom, only actors are rendered via DrawActorDots, skip other types
                if (_zoom < 0.05f && obj.RecordType is not ("ACHR" or "ACRE"))
                {
                    continue;
                }

                float dist;
                if (useBounds)
                {
                    dist = HitTestObjectBounds(worldPos, obj);
                }
                else
                {
                    var objPos = new Vector2(obj.X, -obj.Y);
                    dist = Vector2.Distance(worldPos, objPos);
                    if (dist >= hitRadius)
                    {
                        dist = float.MaxValue;
                    }
                }

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = obj;
                }
            }
        }

        return closest;
    }

    private PlacedReference? HitTestMapMarker(Vector2 worldPos)
    {
        if (_filteredMarkers.Count == 0 || _hiddenCategories.Contains(PlacedObjectCategory.MapMarker))
        {
            return null;
        }

        PlacedReference? closest = null;
        var closestDist = float.MaxValue;
        var hitRadius = 20f / _zoom;

        foreach (var marker in _filteredMarkers)
        {
            var markerPos = new Vector2(marker.X, -marker.Y);
            var dist = Vector2.Distance(worldPos, markerPos);

            if (dist < hitRadius && dist < closestDist)
            {
                closestDist = dist;
                closest = marker;
            }
        }

        return closest;
    }

    /// <summary>
    ///     Returns the active cell list: worldspace cells, unlinked exterior cells, or empty.
    /// </summary>
    private List<CellRecord> GetActiveCells()
    {
        if (_selectedWorldspace != null)
        {
            return _selectedWorldspace.Cells;
        }

        if (_unlinkedCells != null)
        {
            return _unlinkedCells;
        }

        return [];
    }

    private void BuildCellGridLookup()
    {
        _cellGridLookup = null;
        var cells = GetActiveCells();
        if (cells.Count == 0)
        {
            return;
        }

        _cellGridLookup = new Dictionary<(int x, int y), CellRecord>();
        foreach (var cell in cells)
        {
            if (cell.GridX.HasValue && cell.GridY.HasValue)
            {
                _cellGridLookup.TryAdd((cell.GridX.Value, cell.GridY.Value), cell);
            }
        }
    }

    internal void NavigateToCellPublic(CellRecord cell) => NavigateToCell(cell);

    public void NavigateToWorldspaceAndCell(int worldspaceIndex, CellRecord cell)
    {
        // Switch worldspace (triggers mode change, grid rebuild, etc.)
        WorldspaceComboBox.SelectedIndex = worldspaceIndex;
        // Center on cell in overview instead of entering cell detail mode
        NavigateToCellInOverview(cell);
    }

    public void NavigateToCellInOverview(CellRecord cell)
    {
        // Switch to overview mode if in cell detail
        if (_mode == ViewMode.CellDetail)
        {
            _mode = ViewMode.WorldOverview;
            _selectedCell = null;
            _cellHeightmapBitmap?.Dispose();
            _cellHeightmapBitmap = null;
        }

        SetCanvasMode(true);

        if (!cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            return;
        }

        // Center on cell's world position (each grid cell = 4096 units)
        var cellCenterX = (cell.GridX.Value + 0.5f) * 4096f;
        var cellCenterY = -(cell.GridY.Value + 0.5f) * 4096f; // Y inverted in view

        var canvasW = (float)MapCanvas.ActualWidth;
        var canvasH = (float)MapCanvas.ActualHeight;
        if (canvasW < 1)
        {
            canvasW = 800;
        }

        if (canvasH < 1)
        {
            canvasH = 600;
        }

        // Zoom to show ~3x3 cells around the target
        _zoom = Math.Min(canvasW, canvasH) / (4096f * 3f);
        _panOffset = new Vector2(
            canvasW / 2f - cellCenterX * _zoom,
            canvasH / 2f - cellCenterY * _zoom);

        MapCanvas.Invalidate();
    }

    public void NavigateToWorldspace(int worldspaceIndex)
    {
        if (worldspaceIndex >= 0 && worldspaceIndex < WorldspaceComboBox.Items.Count)
        {
            WorldspaceComboBox.SelectedIndex = worldspaceIndex;
        }
    }

    public void NavigateToObjectInOverview(PlacedReference obj)
    {
        // Ensure we're in overview mode
        if (_mode == ViewMode.CellDetail)
        {
            _mode = ViewMode.WorldOverview;
            _selectedCell = null;

            _cellHeightmapBitmap?.Dispose();
            _cellHeightmapBitmap = null;
        }

        var objCenter = new Vector2(obj.X, -obj.Y);
        float viewRadius = 2048f; // ~half a cell

        if (_data?.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds) == true)
        {
            var size = Math.Max(bounds.X2 - bounds.X1, bounds.Y2 - bounds.Y1) * obj.Scale;
            viewRadius = Math.Max(size * 3f, 1024f);
        }

        var canvasW = (float)MapCanvas.ActualWidth;
        var canvasH = (float)MapCanvas.ActualHeight;
        if (canvasW < 1)
        {
            canvasW = 800;
        }

        if (canvasH < 1)
        {
            canvasH = 600;
        }

        var minDim = Math.Min(canvasW, canvasH);
        _zoom = minDim / (viewRadius * 4f);

        _panOffset = new Vector2(
            canvasW / 2f - objCenter.X * _zoom,
            canvasH / 2f - objCenter.Y * _zoom);

        _selectedObject = obj;
        MapCanvas.Visibility = Visibility.Visible;
        CellBrowserPanel.Visibility = Visibility.Collapsed;
        MapCanvas.Invalidate();
    }

    // ========================================================================
    // Cell Browser
    // ========================================================================

    private void PopulateCellBrowser()
    {
        if (_data == null)
        {
            return;
        }

        _allCellItems = BuildCellListItems(_data.AllCells, groupInteriors: true);
        CellSearchBox.Text = "";
        RebuildCellListFromItems(_allCellItems);
    }

    private void PopulateInteriorCellBrowser()
    {
        if (_data == null)
        {
            return;
        }

        _allCellItems = BuildCellListItems(_data.InteriorCells, groupInteriors: false);
        CellSearchBox.Text = "";
        RebuildCellListFromItems(_allCellItems);
    }

    private List<CellListItem> BuildCellListItems(List<CellRecord> cells, bool groupInteriors)
    {
        var items = new List<CellListItem>();
        foreach (var cell in cells)
        {
            string group;
            if (cell.IsInterior)
            {
                if (groupInteriors)
                {
                    group = "Interior";
                }
                else
                {
                    // Group alphabetically by first letter of name
                    var name = cell.EditorId ?? cell.FullName ?? "";
                    group = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "#";
                }
            }
            else if (cell.WorldspaceFormId is > 0)
            {
                var wsEditorId = _data!.Resolver.GetEditorId(cell.WorldspaceFormId.Value);
                var wsDisplayName = _data.Resolver.GetDisplayName(cell.WorldspaceFormId.Value);

                if (!string.IsNullOrEmpty(wsDisplayName) && !string.IsNullOrEmpty(wsEditorId) &&
                    !string.Equals(wsDisplayName, wsEditorId, StringComparison.OrdinalIgnoreCase))
                {
                    group = $"{wsDisplayName} ({wsEditorId})";
                }
                else
                {
                    group = wsEditorId ?? wsDisplayName ?? $"Worldspace 0x{cell.WorldspaceFormId.Value:X8}";
                }
            }
            else
            {
                group = "Unknown";
            }

            var gridLabel = cell.GridX.HasValue && cell.GridY.HasValue
                ? $"[{cell.GridX.Value},{cell.GridY.Value}]"
                : "";
            var displayName = cell.EditorId ?? cell.FullName ?? $"0x{cell.FormId:X8}";
            var objectCount = $"{cell.PlacedObjects.Count} obj";

            items.Add(new CellListItem
            {
                Group = group,
                GridLabel = gridLabel,
                DisplayName = displayName,
                ObjectCount = objectCount,
                Cell = cell
            });
        }

        return items;
    }

    private void RebuildCellListFromItems(List<CellListItem> items)
    {
        // Sort by group, then by grid coordinates
        var sorted = items
            .OrderBy(i => GetGroupSortOrder(i.Group))
            .ThenBy(i => i.Group)
            .ThenBy(i => i.Cell.GridX ?? int.MaxValue)
            .ThenBy(i => i.Cell.GridY ?? int.MaxValue);

        var grouped = sorted.GroupBy(i => i.Group);
        var source = new List<CellListGroup>();
        foreach (var group in grouped)
        {
            source.Add(new CellListGroup(group.Key, group.ToList()));
        }

        var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource
        {
            IsSourceGrouped = true,
            Source = source
        };
        CellListView.ItemsSource = cvs.View;

        HoverInfoText.Text = $"{items.Count} cells";
        ZoomLevelText.Text = "";
        CoordsText.Text = "";
    }

    private void CellSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyCellFilters();
    }

    private void CellListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CellListView.SelectedItem is CellListItem item)
        {
            InspectCell?.Invoke(this, item.Cell);
            ViewCellButton.Visibility = Visibility.Visible;
        }
        else
        {
            ViewCellButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ViewCellButton_Click(object sender, RoutedEventArgs e)
    {
        if (CellListView.SelectedItem is CellListItem item)
        {
            NavigateToCell(item.Cell);
        }
    }

    // ========================================================================
    // Zoom Helpers
    // ========================================================================

    private void ZoomToFitWorldspace()
    {
        var cells = GetActiveCells();
        if (cells.Count == 0)
        {
            return;
        }

        var cellsWithGrid = cells
            .Where(c => c.GridX.HasValue && c.GridY.HasValue)
            .ToList();

        if (cellsWithGrid.Count == 0)
        {
            return;
        }

        var minX = cellsWithGrid.Min(c => c.GridX!.Value) * CellWorldSize;
        var maxX = (cellsWithGrid.Max(c => c.GridX!.Value) + 1) * CellWorldSize;
        var minY = -(cellsWithGrid.Max(c => c.GridY!.Value) + 1) * CellWorldSize;
        var maxY = -cellsWithGrid.Min(c => c.GridY!.Value) * CellWorldSize;

        ZoomToFitBounds(minX, minY, maxX, maxY);
    }

    private void ZoomToFitCell(CellRecord cell)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            // Interior cell - zoom based on placed object extent
            if (cell.PlacedObjects.Count > 0)
            {
                var minX = cell.PlacedObjects.Min(o => o.X) - 200;
                var maxX = cell.PlacedObjects.Max(o => o.X) + 200;
                var minY = -cell.PlacedObjects.Max(o => o.Y) - 200;
                var maxY = -cell.PlacedObjects.Min(o => o.Y) + 200;
                ZoomToFitBounds(minX, minY, maxX, maxY);
            }

            return;
        }

        var cx = cell.GridX.Value;
        var cy = cell.GridY.Value;
        var worldMinX = cx * CellWorldSize - 200;
        var worldMaxX = (cx + 1) * CellWorldSize + 200;
        var worldMinY = -(cy + 1) * CellWorldSize - 200;
        var worldMaxY = -cy * CellWorldSize + 200;

        ZoomToFitBounds(worldMinX, worldMinY, worldMaxX, worldMaxY);
    }

    private void ZoomToFitBounds(float worldMinX, float worldMinY, float worldMaxX, float worldMaxY)
    {
        var canvasW = (float)MapCanvas.ActualWidth;
        var canvasH = (float)MapCanvas.ActualHeight;
        if (canvasW < 1 || canvasH < 1)
        {
            canvasW = 800;
            canvasH = 600;
        }

        var worldW = worldMaxX - worldMinX;
        var worldH = worldMaxY - worldMinY;
        if (worldW < 1)
        {
            worldW = 1;
        }

        if (worldH < 1)
        {
            worldH = 1;
        }

        _zoom = Math.Min(canvasW / worldW, canvasH / worldH) * 0.9f;
        _zoom = Math.Clamp(_zoom, MinZoom, MaxZoom);

        var centerWorldX = (worldMinX + worldMaxX) * 0.5f;
        var centerWorldY = (worldMinY + worldMaxY) * 0.5f;
        _panOffset = new Vector2(
            canvasW * 0.5f - centerWorldX * _zoom,
            canvasH * 0.5f - centerWorldY * _zoom);
    }

    // ========================================================================
    // Viewport Helpers
    // ========================================================================

    private (Vector2 topLeft, Vector2 bottomRight) GetVisibleWorldBounds()
    {
        var canvasW = (float)MapCanvas.ActualWidth;
        var canvasH = (float)MapCanvas.ActualHeight;
        if (canvasW < 1)
        {
            canvasW = 800;
        }

        if (canvasH < 1)
        {
            canvasH = 600;
        }

        var tl = ScreenToWorld(Vector2.Zero);
        var br = ScreenToWorld(new Vector2(canvasW, canvasH));
        return (tl, br);
    }

    private static bool IsCellVisible(CellRecord cell, Vector2 tlWorld, Vector2 brWorld)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            return false;
        }

        var cellMinX = cell.GridX.Value * CellWorldSize;
        var cellMaxX = cellMinX + CellWorldSize;
        var cellMinY = -(cell.GridY.Value + 1) * CellWorldSize;
        var cellMaxY = cellMinY + CellWorldSize;

        var viewMinX = Math.Min(tlWorld.X, brWorld.X);
        var viewMaxX = Math.Max(tlWorld.X, brWorld.X);
        var viewMinY = Math.Min(tlWorld.Y, brWorld.Y);
        var viewMaxY = Math.Max(tlWorld.Y, brWorld.Y);

        return cellMaxX >= viewMinX && cellMinX <= viewMaxX &&
               cellMaxY >= viewMinY && cellMinY <= viewMaxY;
    }

    private static bool IsPointInView(float x, float y, Vector2 tlWorld, Vector2 brWorld, float margin)
    {
        var viewMinX = Math.Min(tlWorld.X, brWorld.X) - margin;
        var viewMaxX = Math.Max(tlWorld.X, brWorld.X) + margin;
        var viewMinY = Math.Min(tlWorld.Y, brWorld.Y) - margin;
        var viewMaxY = Math.Max(tlWorld.Y, brWorld.Y) + margin;

        return x >= viewMinX && x <= viewMaxX && y >= viewMinY && y <= viewMaxY;
    }

    private float GetObjectViewMargin(PlacedReference obj)
    {
        if (_data?.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds) == true)
        {
            var maxExtent = Math.Max(
                Math.Max(Math.Abs(bounds.X2 - bounds.X1), Math.Abs(bounds.Y2 - bounds.Y1)),
                Math.Abs(bounds.Z2 - bounds.Z1)) * obj.Scale;
            return Math.Max(maxExtent, 500f);
        }

        return 500f;
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

    // ========================================================================
    // Category Helpers
    // ========================================================================

    private PlacedObjectCategory GetObjectCategory(PlacedReference obj)
    {
        if (obj.IsMapMarker)
        {
            return PlacedObjectCategory.MapMarker;
        }

        return obj.RecordType switch
        {
            "ACHR" => PlacedObjectCategory.Npc,
            "ACRE" => PlacedObjectCategory.Creature,
            _ => _data?.CategoryIndex.GetValueOrDefault(obj.BaseFormId, PlacedObjectCategory.Unknown)
                 ?? PlacedObjectCategory.Unknown
        };
    }

    internal static string GetCategoryDisplayName(PlacedObjectCategory category) => category switch
    {
        PlacedObjectCategory.Npc => "NPC",
        PlacedObjectCategory.MapMarker => "Map Marker",
        PlacedObjectCategory.Landscape => "Landscape",
        PlacedObjectCategory.Plants => "Plants",
        PlacedObjectCategory.Effects => "Effects",
        PlacedObjectCategory.Vehicles => "Vehicles",
        PlacedObjectCategory.Traps => "Traps",
        _ => category.ToString()
    };

    private static Color GetCategoryColor(PlacedObjectCategory category) =>
        CategoryColors.GetValueOrDefault(category, Color.FromArgb(255, 80, 80, 80));

    /// <summary>
    ///     Builds perceptually-uniform OKLCH colors for world map categories.
    ///     Pins 3 semantic hues (Creature=red, Plants=green, NPC=blue),
    ///     keeps MapMarker=white and Unknown=dark gray, then distributes the
    ///     remaining 15 categories across 3 hue arcs with lightness cycling.
    ///     Landscape is auto-generated (not pinned) because the default amber
    ///     heightmap tint would make a pinned amber/yellow Landscape blend in.
    /// </summary>
    private static FrozenDictionary<PlacedObjectCategory, Color> BuildWorldCategoryColors()
    {
        ReadOnlySpan<double> lightnessTiers = [0.62, 0.72, 0.78];
        const double chroma = 0.22;
        const double creatureHue = 25.0;
        const double plantsHue = 140.0;
        const double npcHue = 220.0;

        var colors = new Dictionary<PlacedObjectCategory, Color>
        {
            [PlacedObjectCategory.Creature] = ArgbToColor(FormatRegistry.OklchToArgb(0.72, chroma, creatureHue)),
            [PlacedObjectCategory.Plants] = ArgbToColor(FormatRegistry.OklchToArgb(0.72, chroma, plantsHue)),
            [PlacedObjectCategory.Npc] = ArgbToColor(FormatRegistry.OklchToArgb(0.72, chroma, npcHue)),
            [PlacedObjectCategory.MapMarker] = Color.FromArgb(255, 255, 255, 255),
            [PlacedObjectCategory.Unknown] = Color.FromArgb(255, 80, 80, 80)
        };

        // 15 remaining categories distributed across 3 hue arcs between pinned hues.
        // Arc 1: 25°→140° (115°, 5 slots), Arc 2: 140°→220° (80°, 3 slots),
        // Arc 3: 220°→385° (165°, 7 slots). ~20° step in each arc.
        // Ordering based on WastelandNV counts: top categories (Landscape 48%,
        // Clutter 12%, Architecture 10%, Static 4%) placed in separate arcs.
        PlacedObjectCategory[] remaining =
        [
            // Arc 1 (25°→140°): warm orange → yellow-green
            PlacedObjectCategory.Architecture, PlacedObjectCategory.Effects,
            PlacedObjectCategory.Dungeon, PlacedObjectCategory.Furniture,
            PlacedObjectCategory.Vehicles,
            // Arc 2 (140°→220°): teal → blue
            PlacedObjectCategory.Clutter, PlacedObjectCategory.Static,
            PlacedObjectCategory.Sound,
            // Arc 3 (220°→385°): blue → purple → magenta
            PlacedObjectCategory.Landscape, PlacedObjectCategory.Item,
            PlacedObjectCategory.Activator, PlacedObjectCategory.Container,
            PlacedObjectCategory.Door, PlacedObjectCategory.Light,
            PlacedObjectCategory.Traps
        ];

        (double start, double end, int count)[] arcs =
        [
            (creatureHue, plantsHue, 5),
            (plantsHue, npcHue, 3),
            (npcHue, creatureHue + 360, 7)
        ];

        var idx = 0;
        foreach (var (start, end, count) in arcs)
        {
            var step = (end - start) / (count + 1);
            for (var i = 1; i <= count; i++)
            {
                var hue = (start + step * i) % 360.0;
                var lightness = lightnessTiers[idx % lightnessTiers.Length];
                colors[remaining[idx]] = ArgbToColor(FormatRegistry.OklchToArgb(lightness, chroma, hue));
                idx++;
            }
        }

        return colors.ToFrozenDictionary();
    }

    private static Color ArgbToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static string GetMarkerGlyph(MapMarkerType? markerType) =>
        MapExportLayoutEngine.GetMarkerGlyph(markerType);

    private static Color GetMarkerColor(MapMarkerType? markerType)
    {
        var (r, g, b) = MapExportLayoutEngine.GetMarkerColor(markerType);
        return Color.FromArgb(255, r, g, b);
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Load embedded map marker icon PNGs into CanvasBitmaps (once per device).</summary>
    private void EnsureMarkerIcons(ICanvasResourceCreator resourceCreator)
    {
        if (_markerIconBitmaps != null)
        {
            return;
        }

        _markerIconBitmaps = new Dictionary<MapMarkerType, CanvasBitmap>();
        foreach (var type in Enum.GetValues<MapMarkerType>())
        {
            if (type == MapMarkerType.None)
            {
                continue;
            }

            var png = MapMarkerIconProvider.GetIconPng(type);
            if (png == null)
            {
                continue;
            }

            using var ms = new MemoryStream(png);
            // LoadAsync returns IAsyncOperation; .GetAwaiter().GetResult() is safe on the UI render thread
            // because Win2D render callbacks are already synchronous.
            var bitmap = CanvasBitmap.LoadAsync(resourceCreator, ms.AsRandomAccessStream()).GetAwaiter().GetResult();
            _markerIconBitmaps[type] = bitmap;
        }
    }

    /// <summary>Draw a white-on-transparent icon tinted to the given color.</summary>
    private static void DrawTintedIcon(CanvasDrawingSession ds, CanvasBitmap icon, Rect destRect, Color tint)
    {
        using var tintEffect = new ColorMatrixEffect
        {
            Source = icon,
            ColorMatrix = new Matrix5x4
            {
                // Multiply RGB by tint (white → tint color), preserve alpha
                M11 = tint.R / 255f, M22 = tint.G / 255f, M33 = tint.B / 255f, M44 = 1f
            }
        };
        var sourceRect = new Rect(0, 0, icon.SizeInPixels.Width, icon.SizeInPixels.Height);
        ds.DrawImage(tintEffect, destRect, sourceRect);
    }

    private void DisposeMarkerIcons()
    {
        if (_markerIconBitmaps != null)
        {
            foreach (var bmp in _markerIconBitmaps.Values)
            {
                bmp.Dispose();
            }

            _markerIconBitmaps = null;
        }
    }

    private static int GetGroupSortOrder(string group) => group switch
    {
        "Unknown" => 2,
        "Interior" => 1,
        _ => 0
    };

    /// <summary>
    ///     Format worldspace name as "Display Name (EditorId)" when both are available
    ///     and different, otherwise just the best available name.
    /// </summary>
    private static string FormatWorldspaceName(WorldspaceRecord ws)
    {
        var fullName = ws.FullName;
        var editorId = ws.EditorId;

        if (!string.IsNullOrEmpty(fullName) && !string.IsNullOrEmpty(editorId) &&
            !string.Equals(fullName, editorId, StringComparison.OrdinalIgnoreCase))
        {
            return $"{fullName} ({editorId})";
        }

        return fullName ?? editorId ?? $"0x{ws.FormId:X8}";
    }

    // ========================================================================
    // Export Worldspace as PNG
    // ========================================================================

    private async Task ExportWorldspacePngAsync(
        string filePath, int imageW, int imageH, int pixelsPerCell,
        int minGridX, int maxGridX, int minGridY, int maxGridY)
    {
        // Ensure heightmap bitmap is built (it's lazily created on first draw)
        if (_worldHeightmapDirty && GetActiveCells().Count > 0)
        {
            BuildWorldHeightmapBitmap(MapCanvas);
            _worldHeightmapDirty = false;
        }

        // Use MapCanvas as resource creator to guarantee same device as heightmap bitmap
        using var renderTarget = new CanvasRenderTarget(MapCanvas, imageW, imageH, 96);
        var device = renderTarget.Device;

        // Proportional sizing via shared engine
        var longEdge = Math.Max(imageW, imageH);
        var sizing = MapExportLayoutEngine.ComputeSizing(longEdge);

        var pixelsPerWorldUnit = (float)pixelsPerCell / CellWorldSize;
        var worldOriginX = minGridX * CellWorldSize;
        var worldOriginY = -(maxGridY + 1) * CellWorldSize;
        var worldMaxX = (maxGridX + 1) * CellWorldSize;
        var worldMinY = minGridY * CellWorldSize;
        var worldMaxY = (maxGridY + 1) * CellWorldSize;

        using (var ds = renderTarget.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 20, 20, 25));

            // World-space transform: pixel = (world - origin) * scale
            ds.Transform = Matrix3x2.CreateTranslation(-worldOriginX, -worldOriginY)
                           * Matrix3x2.CreateScale(pixelsPerWorldUnit);

            // 1. Heightmap
            if (_worldHeightmapBitmap != null)
            {
                var pixelScale = CellWorldSize / HmGridSize;
                var bitmapWorldW = _worldHmPixelWidth * pixelScale;
                var bitmapWorldH = _worldHmPixelHeight * pixelScale;
                var bitmapX = _worldHmMinX * CellWorldSize;
                var bitmapY = -(_worldHmMaxY + 1) * CellWorldSize;
                ds.DrawImage(_worldHeightmapBitmap,
                    new Rect(bitmapX, bitmapY, bitmapWorldW, bitmapWorldH));
            }

            // 2. Cell grid (no viewport culling)
            DrawExportCellGrid(ds, minGridX, maxGridX, minGridY, maxGridY, pixelsPerWorldUnit);

            // 3. Map markers (circles + glyphs in world space, labels in pixel space)
            DrawExportMapMarkers(ds, device, pixelsPerWorldUnit, imageW, imageH,
                worldOriginX, worldMaxX, worldMinY, worldMaxY, sizing);
        }

        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        await renderTarget.SaveAsync(stream.AsRandomAccessStream(), CanvasBitmapFileFormat.Png);
    }

    private static void DrawExportCellGrid(CanvasDrawingSession ds,
        int minGridX, int maxGridX, int minGridY, int maxGridY, float pixelsPerWorldUnit)
    {
        var gridColor = Color.FromArgb(40, 255, 255, 255);
        var lineWidth = 0.5f / pixelsPerWorldUnit;

        for (var cx = minGridX; cx <= maxGridX + 1; cx++)
        {
            var worldX = cx * CellWorldSize;
            var yStart = -(maxGridY + 1) * CellWorldSize;
            var yEnd = -minGridY * CellWorldSize;
            ds.DrawLine(worldX, yStart, worldX, yEnd, gridColor, lineWidth);
        }

        for (var cy = minGridY; cy <= maxGridY + 1; cy++)
        {
            var worldY = -cy * CellWorldSize;
            var xStart = minGridX * CellWorldSize;
            var xEnd = (maxGridX + 1) * CellWorldSize;
            ds.DrawLine(xStart, worldY, xEnd, worldY, gridColor, lineWidth);
        }
    }

    private void DrawExportMapMarkers(CanvasDrawingSession ds, CanvasDevice device,
        float pixelsPerWorldUnit, int imageW, int imageH,
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        MapExportSizing sizing)
    {
        if (_filteredMarkers.Count == 0 ||
            _hiddenCategories.Contains(PlacedObjectCategory.MapMarker))
        {
            return;
        }

        // Project markers to engine input
        var inputs = _filteredMarkers
            .Select(m => new MapMarkerInput(m.X, m.Y, m.MarkerType, m.MarkerName))
            .ToList();

        // Compute layout via shared engine (precise Win2D text measurement)
        var layout = MapExportLayoutEngine.ComputeLayout(
            inputs, imageW, imageH,
            worldMinX, worldMaxX, worldMinY, worldMaxY,
            pixelsPerWorldUnit, sizing,
            (text, fontSize) =>
            {
                using var tl = new CanvasTextLayout(device, text,
                    new CanvasTextFormat { FontSize = fontSize, FontFamily = "Segoe UI" },
                    float.MaxValue, float.MaxValue);
                return ((float)tl.LayoutBounds.Width, (float)tl.LayoutBounds.Height);
            });

        // Draw marker icons (tinted to scheme color) in world space (transform is still active)
        var markerWorldRadius = sizing.MarkerRadius / pixelsPerWorldUnit;
        var tint = Color.FromArgb(255, _currentColorScheme.R, _currentColorScheme.G, _currentColorScheme.B);

        foreach (var m in layout.Markers)
        {
            var marker = _filteredMarkers[m.OriginalIndex];
            DrawExportMarkerIcon(ds, marker, markerWorldRadius, tint,
                sizing.LabelFontSize, pixelsPerWorldUnit);
        }

        // Switch to pixel space for leader lines + labels
        ds.Transform = Matrix3x2.Identity;

        // Leader lines (drawn first, behind label pills)
        var leaderColor = Color.FromArgb(150, 255, 255, 255);
        var leaderWidth = Math.Max(1f, sizing.MarkerRadius * 0.1f);

        foreach (var lp in layout.Labels)
        {
            if (!lp.NeedsLeader)
            {
                continue;
            }

            var labelCenter = new Vector2(
                lp.LabelX + lp.PillWidth / 2,
                lp.LabelY + lp.PillHeight / 2);
            var markerPixel = new Vector2(lp.MarkerPixelX, lp.MarkerPixelY);
            var direction = Vector2.Normalize(labelCenter - markerPixel);
            var lineStart = markerPixel + direction * (sizing.MarkerRadius + 1f);

            ds.DrawLine(lineStart, labelCenter, leaderColor, leaderWidth);
        }

        // Label pills + text
        using var labelFormat = new CanvasTextFormat
        {
            FontSize = sizing.LabelFontSize,
            FontFamily = "Segoe UI"
        };

        foreach (var lp in layout.Labels)
        {
            // Black rounded pill background
            using var pillGeometry = CanvasGeometry.CreateRoundedRectangle(
                device, lp.LabelX, lp.LabelY, lp.PillWidth, lp.PillHeight, 3f, 3f);
            ds.FillGeometry(pillGeometry, Color.FromArgb(220, 0, 0, 0));
            ds.DrawGeometry(pillGeometry, Color.FromArgb(100, 255, 255, 255), 0.5f);

            // White text
            ds.DrawText(lp.Text, lp.LabelX + lp.PadH, lp.LabelY + lp.PadV,
                Colors.White, labelFormat);
        }
    }

    private void DrawExportMarkerIcon(CanvasDrawingSession ds, PlacedReference marker,
        float worldRadius, Color tint, float labelFontSize, float pixelsPerWorldUnit)
    {
        var pos = new Vector2(marker.X, -marker.Y);
        var destRect = new Rect(
            pos.X - worldRadius, pos.Y - worldRadius,
            worldRadius * 2, worldRadius * 2);

        if (marker.MarkerType.HasValue &&
            _markerIconBitmaps?.TryGetValue(marker.MarkerType.Value, out var icon) == true)
        {
            DrawTintedIcon(ds, icon, destRect, tint);
        }
        else
        {
            // Fallback: colored circle + glyph for unmapped marker types
            var color = GetMarkerColor(marker.MarkerType);
            ds.FillCircle(pos, worldRadius, WithAlpha(color, 200));
            ds.DrawCircle(pos, worldRadius, Colors.White, 1f / pixelsPerWorldUnit);
            var glyph = GetMarkerGlyph(marker.MarkerType);
            using var glyphFormat = new CanvasTextFormat
            {
                FontSize = labelFontSize / pixelsPerWorldUnit,
                FontFamily = "Segoe MDL2 Assets",
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            ds.DrawText(glyph, destRect, Colors.White, glyphFormat);
        }
    }

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

    internal record WorldNavState(
        ViewMode Mode,
        BrowserMode Browser,
        int WorldspaceComboIndex,
        uint? CellFormId);

    /// <summary>View model for a cell in the cell browser list.</summary>
    internal sealed class CellListItem
    {
        public string Group { get; init; } = "";
        public string GridLabel { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string ObjectCount { get; init; } = "";
        public required CellRecord Cell { get; init; }
    }

    /// <summary>Group of cells for the grouped ListView.</summary>
    internal sealed class CellListGroup(string key, List<CellListItem> items) : List<CellListItem>(items)
    {
        public string Key { get; } = key;
    }
}
