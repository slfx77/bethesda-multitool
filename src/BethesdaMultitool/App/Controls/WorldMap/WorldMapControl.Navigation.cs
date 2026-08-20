using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.WorldData;
using Microsoft.UI.Xaml;

namespace BethesdaMultitool;

/// <summary>Navigation between worldspaces / cells / placed objects, and view-focus sharing with the 3D viewer.</summary>
public sealed partial class WorldMapControl
{
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

    // Toolbar Zoom-to-Fit and the R keybind share one framing path (WorldMapControl.Input).
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => ResetViewToActiveExtent();

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

        var cellCenterX = (cell.GridX.Value + 0.5f) * _cellSize;
        var cellCenterY = -(cell.GridY.Value + 0.5f) * _cellSize;
        var canvasW = Math.Max((float)MapCanvas.ActualWidth, 800f);
        var canvasH = Math.Max((float)MapCanvas.ActualHeight, 600f);
        _zoom = Math.Min(canvasW, canvasH) / (_cellSize * 3f);
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
            return new WorldViewFocus(-1, true, interior, 0f, 0f, selected, CellList.SortMode);
        }

        var canvasW = Math.Max((float)MapCanvas.ActualWidth, 800f);
        var canvasH = Math.Max((float)MapCanvas.ActualHeight, 600f);
        var center2D = WorldMapViewportHelper.ScreenToWorld(
            new Vector2(canvasW / 2f, canvasH / 2f), _zoom, _panOffset);
        // The 3D viewer teleports its camera straight to this point, so it must land INSIDE the
        // authored cells: a zoomed-out overview centre (or one panned past the data) would otherwise
        // drop the camera outside the worldspace with nothing around it. Clamped to the same
        // occupied-cell bounds ZoomToFitWorldspace frames on, so the two views agree.
        if (WorldMapViewportMath.TryGetOccupiedCellBounds(GetActiveCells()) is { } dataBounds)
        {
            center2D = dataBounds.Clamp(center2D);
        }

        // 2D-map Y is the negative of the shared (3D / PlacedReference) Y.
        return new WorldViewFocus(
            WorldspaceComboBox.SelectedIndex, false, null, center2D.X, -center2D.Y, selected, CellList.SortMode);
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

    // The CellListControl owns the sort state + combo; its setter syncs both and re-sorts in place.
    private void ApplyCellSortMode(CellSortMode mode) => CellList.SortMode = mode;

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
}
