using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace FalloutXbox360Utils;

public sealed partial class WorldMapControl : UserControl, IDisposable
{
    private enum ViewMode { WorldOverview, CellDetail, CellBrowser }

    // --- State ---
    private ViewMode _mode = ViewMode.WorldOverview;
    private WorldViewData? _data;
    private WorldspaceRecord? _selectedWorldspace;
    private CellRecord? _selectedCell;

    // --- Pan/Zoom ---
    private float _zoom = 0.05f;
    private Vector2 _panOffset;
    private bool _isPanning;
    private Vector2 _panStartScreen;
    private Vector2 _panOffsetAtStart;
    private const float MinZoom = 0.001f;
    private const float MaxZoom = 50f;

    // --- Cached Heightmap Bitmaps ---
    private CanvasBitmap? _worldHeightmapBitmap;
    private int _worldHmMinX, _worldHmMaxY;
    private int _worldHmPixelWidth, _worldHmPixelHeight;
    private CanvasBitmap? _cellHeightmapBitmap;
    private bool _worldHeightmapDirty = true;
    private float? _currentDefaultWaterHeight;

    // --- Hover / Selection ---
    private PlacedReference? _hoveredObject;
    private PlacedReference? _selectedObject;

    // --- Unlinked exterior cells (DMP files without WRLD records) ---
    private List<CellRecord>? _unlinkedCells;

    // --- Cell grid lookup (built when worldspace changes) ---
    private Dictionary<(int x, int y), CellRecord>? _cellGridLookup;

    // --- Filtered markers for the selected worldspace ---
    private List<PlacedReference> _filteredMarkers = [];

    // --- Cell browser search ---
    private List<CellListItem> _allCellItems = [];

    // --- Click detection ---
    private Vector2 _pointerDownScreen;
    private bool _pointerWasDragged;

    // --- Constants ---
    private const float CellWorldSize = 4096f;
    private const int HmGridSize = 33;

    // --- Events ---
    public event EventHandler<PlacedReference>? InspectObject;
    public event EventHandler<CellRecord>? InspectCell;

    public WorldMapControl()
    {
        InitializeComponent();
    }

    internal void LoadData(WorldViewData data)
    {
        _data = data;
        _worldHeightmapDirty = true;

        WorldspaceComboBox.Items.Clear();
        foreach (var ws in data.Worldspaces)
        {
            var name = ws.FullName ?? ws.EditorId ?? $"0x{ws.FormId:X8}";
            var cellCount = ws.Cells.Count;
            WorldspaceComboBox.Items.Add($"{name} ({cellCount} cells)");
        }

        if (data.UnlinkedExteriorCells.Count > 0)
        {
            WorldspaceComboBox.Items.Add($"Unlinked Exterior ({data.UnlinkedExteriorCells.Count} cells)");
        }

        if (data.InteriorCells.Count > 0)
        {
            WorldspaceComboBox.Items.Add($"Interior Cells ({data.InteriorCells.Count})");
        }

        WorldspaceComboBox.Items.Add($"All Cells ({data.AllCells.Count})");

        if (data.Worldspaces.Count > 0)
        {
            WorldspaceComboBox.SelectedIndex = 0;
        }
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
        _worldHeightmapDirty = true;
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        WorldspaceComboBox.Items.Clear();
        BackButton.Visibility = Visibility.Collapsed;
        MapCanvas.Invalidate();
    }

    public void Dispose()
    {
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
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
        var nextIdx = _data.Worldspaces.Count;

        // Compute dynamic indices based on which optional entries exist
        var unlinkedIdx = _data.UnlinkedExteriorCells.Count > 0 ? nextIdx++ : -1;
        var interiorIdx = _data.InteriorCells.Count > 0 ? nextIdx++ : -1;
        var allCellsIdx = nextIdx;

        if (idx < _data.Worldspaces.Count)
        {
            // Worldspace selected
            _selectedWorldspace = _data.Worldspaces[idx];
            _unlinkedCells = null;
            _mode = ViewMode.WorldOverview;
            _selectedCell = null;
            BackButton.Visibility = Visibility.Collapsed;
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
            MapCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            ZoomToFitWorldspace();
            MapCanvas.Invalidate();
        }
        else if (idx == unlinkedIdx)
        {
            // Unlinked exterior cells (DMP files without WRLD records)
            _selectedWorldspace = null;
            _unlinkedCells = _data.UnlinkedExteriorCells;
            _mode = ViewMode.WorldOverview;
            _selectedCell = null;
            BackButton.Visibility = Visibility.Collapsed;
            _worldHeightmapBitmap?.Dispose();
            _worldHeightmapBitmap = null;
            _worldHeightmapDirty = true;
            _worldHmMinX = _worldHmMaxY = _worldHmPixelWidth = _worldHmPixelHeight = 0;
            _currentDefaultWaterHeight = null;
            _cellHeightmapBitmap?.Dispose();
            _cellHeightmapBitmap = null;
            _filteredMarkers = [];
            BuildCellGridLookup();
            MapCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            ZoomToFitWorldspace();
            MapCanvas.Invalidate();
        }
        else if (idx == interiorIdx)
        {
            // Interior cells — show as searchable list (interiors are isolated, map is useless)
            _selectedWorldspace = null;
            _unlinkedCells = null;
            _mode = ViewMode.CellBrowser;
            _selectedCell = null;
            BackButton.Visibility = Visibility.Collapsed;
            _filteredMarkers = [];
            MapCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            PopulateInteriorCellBrowser();
        }
        else if (idx == allCellsIdx)
        {
            // All Cells browser mode
            _selectedWorldspace = null;
            _unlinkedCells = null;
            _mode = ViewMode.CellBrowser;
            _selectedCell = null;
            BackButton.Visibility = Visibility.Collapsed;
            _filteredMarkers = [];
            MapCanvas.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            PopulateCellBrowser();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        _mode = ViewMode.WorldOverview;
        _selectedCell = null;
        BackButton.Visibility = Visibility.Collapsed;
        _cellHeightmapBitmap?.Dispose();
        _cellHeightmapBitmap = null;
        HoverInfoText.Text = "";

        if (GetActiveCells().Count > 0)
        {
            ZoomToFitWorldspace();
        }

        MapCanvas.Invalidate();
    }

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
            foreach (var cell in activeCells)
            {
                if (!IsCellVisible(cell, tlWorld, brWorld))
                {
                    continue;
                }

                foreach (var obj in cell.PlacedObjects)
                {
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

            // 3b. Persistent cell objects (NPCs/Creatures in cells without grid coords)
            foreach (var cell in activeCells)
            {
                if (cell.GridX.HasValue && cell.GridY.HasValue)
                {
                    continue; // Already drawn above
                }

                foreach (var obj in cell.PlacedObjects)
                {
                    if (obj.IsMapMarker || !IsPointInView(obj.X, -obj.Y, tlWorld, brWorld, GetObjectViewMargin(obj)))
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

        // 5. Selected object highlight
        if (_selectedObject != null)
        {
            DrawSelectedObjectHighlight(ds, _selectedObject);
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
        if (_data == null || _filteredMarkers.Count == 0)
        {
            return;
        }

        var (tlWorld, brWorld) = GetVisibleWorldBounds();
        var markerRadius = 8f / _zoom;
        var outlineWidth = 1f / _zoom;

        using var labelFormat = new CanvasTextFormat
        {
            FontSize = 10f / _zoom,
            FontFamily = "Segoe UI"
        };

        using var glyphFormat = new CanvasTextFormat
        {
            FontSize = 12f / _zoom,
            FontFamily = "Segoe MDL2 Assets",
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };

        foreach (var marker in _filteredMarkers)
        {
            var pos = new Vector2(marker.X, -marker.Y);

            if (!IsPointInView(pos.X, pos.Y, tlWorld, brWorld, markerRadius * 4))
            {
                continue;
            }

            var color = GetMarkerColor(marker.MarkerType);
            ds.FillCircle(pos, markerRadius, WithAlpha(color, 200));
            ds.DrawCircle(pos, markerRadius, Colors.White, outlineWidth);

            // Draw glyph icon centered on marker
            var glyph = GetMarkerGlyph(marker.MarkerType);
            var glyphRect = new Rect(
                pos.X - markerRadius, pos.Y - markerRadius,
                markerRadius * 2, markerRadius * 2);
            ds.DrawText(glyph, glyphRect, Colors.White, glyphFormat);

            // Label at sufficient zoom
            if (_zoom > 0.05f && !string.IsNullOrEmpty(marker.MarkerName))
            {
                var labelPos = new Vector2(pos.X + markerRadius * 2f, pos.Y - markerRadius);
                ds.DrawText(marker.MarkerName, labelPos, Colors.White, labelFormat);
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

            // Build rotated rectangle corners
            var rotation = Matrix3x2.CreateRotation(-obj.RotZ, pos);
            Span<Vector2> corners = stackalloc Vector2[4];
            corners[0] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y - halfH), rotation);
            corners[1] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y - halfH), rotation);
            corners[2] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y + halfH), rotation);
            corners[3] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y + halfH), rotation);

            if (outlineOnly)
            {
                // Fast path: 4 line draws, no geometry allocation
                ds.DrawLine(corners[0], corners[1], color, lineWidth);
                ds.DrawLine(corners[1], corners[2], color, lineWidth);
                ds.DrawLine(corners[2], corners[3], color, lineWidth);
                ds.DrawLine(corners[3], corners[0], color, lineWidth);
            }
            else
            {
                using var pathBuilder = new CanvasPathBuilder(ds);
                pathBuilder.BeginFigure(corners[0]);
                pathBuilder.AddLine(corners[1]);
                pathBuilder.AddLine(corners[2]);
                pathBuilder.AddLine(corners[3]);
                pathBuilder.EndFigure(CanvasFigureLoop.Closed);

                using var geometry = CanvasGeometry.CreatePath(pathBuilder);
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
        if (_data == null)
        {
            return;
        }

        var pos = new Vector2(obj.X, -obj.Y);
        var highlightColor = Colors.Yellow;

        if (_data.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds))
        {
            var halfW = (bounds.X2 - bounds.X1) * 0.5f * obj.Scale;
            var halfH = (bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale;

            if (halfW >= 1f || halfH >= 1f)
            {
                var rotation = Matrix3x2.CreateRotation(-obj.RotZ, pos);
                Span<Vector2> corners = stackalloc Vector2[4];
                corners[0] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y - halfH), rotation);
                corners[1] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y - halfH), rotation);
                corners[2] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y + halfH), rotation);
                corners[3] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y + halfH), rotation);

                using var pathBuilder = new CanvasPathBuilder(ds);
                pathBuilder.BeginFigure(corners[0]);
                pathBuilder.AddLine(corners[1]);
                pathBuilder.AddLine(corners[2]);
                pathBuilder.AddLine(corners[3]);
                pathBuilder.EndFigure(CanvasFigureLoop.Closed);

                using var geometry = CanvasGeometry.CreatePath(pathBuilder);
                ds.DrawGeometry(geometry, highlightColor, 3f / _zoom);
                return;
            }
        }

        ds.DrawCircle(pos, 12f / _zoom, highlightColor, 3f / _zoom);
    }

    private void DrawSelectedObjectHighlight(CanvasDrawingSession ds, PlacedReference obj)
    {
        if (_data == null)
        {
            return;
        }

        var pos = new Vector2(obj.X, -obj.Y);
        var selectColor = Color.FromArgb(255, 0, 200, 255); // Cyan

        if (_data.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds))
        {
            var halfW = (bounds.X2 - bounds.X1) * 0.5f * obj.Scale;
            var halfH = (bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale;

            if (halfW >= 1f || halfH >= 1f)
            {
                var rotation = Matrix3x2.CreateRotation(-obj.RotZ, pos);
                Span<Vector2> corners = stackalloc Vector2[4];
                corners[0] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y - halfH), rotation);
                corners[1] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y - halfH), rotation);
                corners[2] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y + halfH), rotation);
                corners[3] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y + halfH), rotation);

                using var pathBuilder = new CanvasPathBuilder(ds);
                pathBuilder.BeginFigure(corners[0]);
                pathBuilder.AddLine(corners[1]);
                pathBuilder.AddLine(corners[2]);
                pathBuilder.AddLine(corners[3]);
                pathBuilder.EndFigure(CanvasFigureLoop.Closed);

                using var geometry = CanvasGeometry.CreatePath(pathBuilder);
                ds.DrawGeometry(geometry, selectColor, 4f / _zoom);
                return;
            }
        }

        ds.DrawCircle(pos, 14f / _zoom, selectColor, 4f / _zoom);
    }

    // ========================================================================
    // Heightmap Bitmap Building
    // ========================================================================

    private void BuildWorldHeightmapBitmap(CanvasControl canvas)
    {
        _worldHeightmapBitmap?.Dispose();
        _worldHeightmapBitmap = null;

        // Use pre-computed pixel data from background thread when available
        if (_data?.HeightmapPixels != null && _data.HeightmapPixelWidth > 0 &&
            _selectedWorldspace != null && _data.Worldspaces.Count > 0 &&
            ReferenceEquals(_selectedWorldspace, _data.Worldspaces[0]))
        {
            _worldHeightmapBitmap = CanvasBitmap.CreateFromBytes(
                canvas, _data.HeightmapPixels, _data.HeightmapPixelWidth, _data.HeightmapPixelHeight,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            _worldHmMinX = _data.HeightmapMinCellX;
            _worldHmMaxY = _data.HeightmapMaxCellY;
            _worldHmPixelWidth = _data.HeightmapPixelWidth;
            _worldHmPixelHeight = _data.HeightmapPixelHeight;
            return;
        }

        // Fallback: compute on-the-fly for non-default worldspaces or unlinked cells
        var activeCells = GetActiveCells();
        if (activeCells.Count == 0)
        {
            return;
        }

        var result = ComputeHeightmapPixels(activeCells, _currentDefaultWaterHeight);
        if (result == null)
        {
            return;
        }

        var (pixels, imgW, imgH, minX, maxY) = result.Value;
        _worldHeightmapBitmap = CanvasBitmap.CreateFromBytes(
            canvas, pixels, imgW, imgH,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
        _worldHmMinX = minX;
        _worldHmMaxY = maxY;
        _worldHmPixelWidth = imgW;
        _worldHmPixelHeight = imgH;
    }

    /// <summary>
    ///     Computes RGBA heightmap pixel data from a list of cells. Can be called from a background thread.
    /// </summary>
    internal static (byte[] Pixels, int Width, int Height, int MinCellX, int MaxCellY)?
        ComputeHeightmapPixels(List<CellRecord> cellSource, float? defaultWaterHeight = null)
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
                    if (h < globalMin) { globalMin = h; }
                    if (h > globalMax) { globalMax = h; }
                }
            }
        }

        var globalRange = globalMax - globalMin;
        if (globalRange < 0.001f) { globalRange = 1f; }

        // Render RGBA pixels
        var pixels = new byte[imgW * imgH * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 20;
            pixels[i + 1] = 20;
            pixels[i + 2] = 25;
            pixels[i + 3] = 255;
        }

        foreach (var cell in cells)
        {
            var heights = heightCache[cell];
            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;

            for (var py = 0; py < HmGridSize; py++)
            {
                for (var px = 0; px < HmGridSize; px++)
                {
                    var height = heights[HmGridSize - 1 - py, px];
                    var normalized = (height - globalMin) / globalRange;
                    var (r, g, b) = HeightToColor(normalized);

                    // Use cell water height; fall back to worldspace default for sentinel values
                    var waterH = cell.WaterHeight;
                    if (!waterH.HasValue || waterH.Value is not (> -1e6f and < 1e6f))
                    {
                        waterH = defaultWaterHeight;
                    }

                    if (waterH.HasValue && waterH.Value is > -1e6f and < 1e6f &&
                        height < waterH.Value)
                    {
                        (r, g, b) = ApplyWaterTint(r, g, b, height, waterH.Value);
                    }

                    var imgX = imgCellX * HmGridSize + px;
                    var imgY = imgCellY * HmGridSize + py;
                    var idx = (imgY * imgW + imgX) * 4;
                    pixels[idx] = r;
                    pixels[idx + 1] = g;
                    pixels[idx + 2] = b;
                    pixels[idx + 3] = 255;
                }
            }
        }

        return (pixels, imgW, imgH, minX, maxY);
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
                if (h < minH) { minH = h; }
                if (h > maxH) { maxH = h; }
            }
        }

        var range = maxH - minH;
        if (range < 0.001f) { range = 1f; }

        var pixels = new byte[HmGridSize * HmGridSize * 4];
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var height = heights[HmGridSize - 1 - py, px];
                var normalized = (height - minH) / range;
                var (r, g, b) = HeightToColor(normalized);

                // Use cell water height; fall back to worldspace default for sentinel values
                var waterH = cell.WaterHeight;
                if (!waterH.HasValue || waterH.Value is not (> -1e6f and < 1e6f))
                {
                    waterH = _currentDefaultWaterHeight;
                }

                if (waterH.HasValue && waterH.Value is > -1e6f and < 1e6f &&
                    height < waterH.Value)
                {
                    (r, g, b) = ApplyWaterTint(r, g, b, height, waterH.Value);
                }

                var idx = (py * HmGridSize + px) * 4;
                pixels[idx] = r;
                pixels[idx + 1] = g;
                pixels[idx + 2] = b;
                pixels[idx + 3] = 200; // Slightly transparent so objects are more visible
            }
        }

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
                    HoverInfoText.Text = $"{hitObj.RecordType}: {name} at ({hitObj.X:F0}, {hitObj.Y:F0}, {hitObj.Z:F0})";
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
                    HoverInfoText.Text = $"{overviewHitObj.RecordType}: {name} at ({overviewHitObj.X:F0}, {overviewHitObj.Y:F0}, {overviewHitObj.Z:F0})";
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
        }
    }

    private void NavigateToCell(CellRecord cell)
    {
        _selectedCell = cell;
        _mode = ViewMode.CellDetail;
        BackButton.Visibility = Visibility.Visible;
        _hoveredObject = null;

        // Build cell heightmap
        BuildCellHeightmapBitmap(MapCanvas, cell);

        ZoomToFitCell(cell);
        MapCanvas.Invalidate();
    }

    // ========================================================================
    // Hit Testing
    // ========================================================================

    private PlacedReference? HitTestPlacedObject(Vector2 worldPos)
    {
        if (_selectedCell == null || _data == null)
        {
            return null;
        }

        PlacedReference? closest = null;
        var closestDist = float.MaxValue;
        var hitRadius = 50f / _zoom; // Screen-size-relative hit radius

        foreach (var obj in _selectedCell.PlacedObjects)
        {
            var objPos = new Vector2(obj.X, -obj.Y);
            var dist = Vector2.Distance(worldPos, objPos);

            if (dist < hitRadius && dist < closestDist)
            {
                closestDist = dist;
                closest = obj;
            }
        }

        return closest;
    }

    private PlacedReference? HitTestPlacedObjectInOverview(Vector2 worldPos)
    {
        if (_data == null || _zoom < 0.05f || GetActiveCells().Count == 0)
        {
            return null;
        }

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
                    var objPos = new Vector2(obj.X, -obj.Y);
                    var dist = Vector2.Distance(worldPos, objPos);

                    if (dist < hitRadius && dist < closestDist)
                    {
                        closestDist = dist;
                        closest = obj;
                    }
                }
            }
        }

        // Also check persistent cells (no grid coords — NPCs, creatures, etc.)
        foreach (var cell in GetActiveCells())
        {
            if (cell.GridX.HasValue && cell.GridY.HasValue)
            {
                continue;
            }

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.IsMapMarker)
                {
                    continue;
                }

                var objPos = new Vector2(obj.X, -obj.Y);
                var dist = Vector2.Distance(worldPos, objPos);

                if (dist < hitRadius && dist < closestDist)
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
        if (_filteredMarkers.Count == 0)
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
        if (_selectedWorldspace != null) { return _selectedWorldspace.Cells; }
        if (_unlinkedCells != null) { return _unlinkedCells; }
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
        // Then navigate to the specific cell
        NavigateToCell(cell);
    }

    public void NavigateToObjectInOverview(PlacedReference obj)
    {
        // Ensure we're in overview mode
        if (_mode == ViewMode.CellDetail)
        {
            _mode = ViewMode.WorldOverview;
            _selectedCell = null;
            BackButton.Visibility = Visibility.Collapsed;
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
        if (canvasW < 1) { canvasW = 800; }
        if (canvasH < 1) { canvasH = 600; }
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
                if (_data!.FormIdToEditorId.TryGetValue(cell.WorldspaceFormId.Value, out var wsName))
                {
                    group = wsName;
                }
                else if (_data.FormIdToDisplayName.TryGetValue(cell.WorldspaceFormId.Value, out var wsDisplayName))
                {
                    group = wsDisplayName;
                }
                else
                {
                    group = $"Worldspace 0x{cell.WorldspaceFormId.Value:X8}";
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
        var query = CellSearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            RebuildCellListFromItems(_allCellItems);
            return;
        }

        var filtered = _allCellItems
            .Where(i =>
                i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.GridLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Group.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        RebuildCellListFromItems(filtered);
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
            MapCanvas.Visibility = Visibility.Visible;
            CellBrowserPanel.Visibility = Visibility.Collapsed;
            NavigateToCell(item.Cell);
        }
    }

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
        if (worldW < 1) { worldW = 1; }
        if (worldH < 1) { worldH = 1; }

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
        if (canvasW < 1) { canvasW = 800; }
        if (canvasH < 1) { canvasH = 600; }

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

    // ========================================================================
    // Cursor Helpers
    // ========================================================================

    private InputSystemCursorShape _currentCursorShape = InputSystemCursorShape.Arrow;

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
    // Color Helpers
    // ========================================================================

    private static Color GetCategoryColor(PlacedObjectCategory category) => category switch
    {
        PlacedObjectCategory.Static => Color.FromArgb(255, 160, 160, 160),
        PlacedObjectCategory.Plant => Color.FromArgb(255, 60, 140, 40),
        PlacedObjectCategory.Door => Color.FromArgb(255, 255, 165, 0),
        PlacedObjectCategory.Activator => Color.FromArgb(255, 0, 200, 200),
        PlacedObjectCategory.Light => Color.FromArgb(255, 255, 255, 100),
        PlacedObjectCategory.Furniture => Color.FromArgb(255, 139, 90, 43),
        PlacedObjectCategory.Npc => Color.FromArgb(255, 0, 200, 0),
        PlacedObjectCategory.Creature => Color.FromArgb(255, 220, 50, 50),
        PlacedObjectCategory.Container => Color.FromArgb(255, 160, 80, 200),
        PlacedObjectCategory.Item => Color.FromArgb(255, 80, 140, 255),
        PlacedObjectCategory.MapMarker => Color.FromArgb(255, 255, 255, 255),
        _ => Color.FromArgb(255, 80, 80, 80)
    };

    private static string GetMarkerGlyph(MapMarkerType? markerType) => markerType switch
    {
        MapMarkerType.City => "\uE80F",        // Home
        MapMarkerType.Settlement => "\uE825",   // Map
        MapMarkerType.Encampment => "\uE7C1",   // Globe
        MapMarkerType.Cave => "\uE774",         // Mountain/pin
        MapMarkerType.Factory => "\uE8B1",      // Settings/gear
        MapMarkerType.Monument => "\uE734",     // Star (favorite)
        MapMarkerType.Military => "\uE7C8",     // Shield
        MapMarkerType.Vault => "\uE72E",        // Lock
        _ => "\uE81D"                           // Flag
    };

    private static Color GetMarkerColor(MapMarkerType? markerType) => markerType switch
    {
        MapMarkerType.City => Color.FromArgb(255, 255, 215, 0),
        MapMarkerType.Settlement => Color.FromArgb(255, 200, 170, 80),
        MapMarkerType.Encampment => Color.FromArgb(255, 180, 140, 60),
        MapMarkerType.Cave => Color.FromArgb(255, 120, 100, 80),
        MapMarkerType.Factory => Color.FromArgb(255, 180, 180, 180),
        MapMarkerType.Monument => Color.FromArgb(255, 220, 200, 160),
        MapMarkerType.Military => Color.FromArgb(255, 200, 60, 60),
        MapMarkerType.Vault => Color.FromArgb(255, 80, 140, 255),
        _ => Color.FromArgb(255, 200, 200, 200)
    };

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static int GetGroupSortOrder(string group) => group switch
    {
        "Unknown" => 2,
        "Interior" => 1,
        _ => 0
    };

    // ========================================================================
    // HeightToColor — ported from HeightmapPngExporter
    // ========================================================================

    internal static (byte r, byte g, byte b) HeightToColor(float normalizedHeight)
    {
        normalizedHeight = Math.Clamp(normalizedHeight, 0f, 1f);

        float h, s, l;

        if (normalizedHeight < 0.30f)
        {
            // Low: very dark brown/black → dark brown
            var t = normalizedHeight / 0.30f;
            h = 30f;
            s = 0.30f + t * 0.05f;
            l = 0.05f + t * 0.15f;
        }
        else if (normalizedHeight < 0.65f)
        {
            // Mid: dark brown → warm tan
            var t = (normalizedHeight - 0.30f) / 0.35f;
            h = 30f + t * 5f;
            s = 0.35f - t * 0.15f;
            l = 0.20f + t * 0.35f;
        }
        else
        {
            // High: warm tan → near-white
            var t = (normalizedHeight - 0.65f) / 0.35f;
            h = 35f + t * 5f;
            s = 0.20f - t * 0.15f;
            l = 0.55f + t * 0.40f;
        }

        return HslToRgb(h, s, l);
    }

    private static (byte r, byte g, byte b) ApplyWaterTint(
        byte r, byte g, byte b, float height, float waterHeight)
    {
        var depth = waterHeight - height;
        var waterBlend = Math.Clamp(depth / 500f, 0f, 0.85f);
        return (
            (byte)(r + (30 - r) * waterBlend),
            (byte)(g + (55 - g) * waterBlend),
            (byte)(b + (120 - b) * waterBlend));
    }

    internal static (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
    {
        if (s < 0.001f)
        {
            var gray = (byte)(l * 255);
            return (gray, gray, gray);
        }

        h /= 360f;
        var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        var r = HueToRgb(p, q, h + 1f / 3f);
        var g = HueToRgb(p, q, h);
        var b = HueToRgb(p, q, h - 1f / 3f);

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0) { t += 1; }
        if (t > 1) { t -= 1; }
        if (t < 1f / 6f) { return p + (q - p) * 6 * t; }
        if (t < 1f / 2f) { return q; }
        if (t < 2f / 3f) { return p + (q - p) * (2f / 3f - t) * 6; }
        return p;
    }

}
