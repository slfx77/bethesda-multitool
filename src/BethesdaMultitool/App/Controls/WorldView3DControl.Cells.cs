using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private async void WorldspaceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorldspaceSelectionEvent || _data is null) return;
        if (WorldspaceComboBox.SelectedIndex < 0) return;

        // Picking a worldspace leaves interior mode and shows that exterior. Building the spatial
        // index + loading the renderers for a large worldspace briefly blocks the UI thread, which
        // used to read as a frozen blank with no feedback. Show a loading overlay first and yield so
        // it actually paints before the blocking work, then hide it once the grid is built (mesh /
        // terrain streaming continues asynchronously afterwards while the controls stay responsive).
        _selectedInterior = null;
        HideInteriorBrowser();
        ShowStatus("Loading worldspace…");
        try
        {
            await System.Threading.Tasks.Task.Yield();
            TryBuildCellGrid();
            if (_pendingNavigateCell is { } target)
            {
                _pendingNavigateCell = null;
                var pose = _pendingNavigateWarpPose;
                _pendingNavigateWarpPose = null;
                if (pose is { } p) ApplyWarpPose(p);
                else CenterCameraOnCell(target);
            }
            else
            {
                ResetCameraToDataCentroid();
            }
            ApplyStressSceneBookmarkIfRequested();
        }
        finally
        {
            HideStatus();
        }

        RefreshAtmosphereForCurrentWorldspace();
    }

    private void TryBuildCellGrid()
    {
        if (_data is null) return;

        // Adopt this worldspace's cell size (4096 / 8192) before building the spatial index, camera,
        // and cull cylinder so all of them agree with the geometry's absolute coordinates. Reset the
        // render distance to the cell-count default at the new scale.
        if (!_cellSize.Equals(_data.CellWorldSize))
        {
            _cellSize = _data.CellWorldSize;
            SetRenderDistance(DefaultRenderDistanceCells * _cellSize);
        }

        // The prior selection belongs to the set we're replacing (different worldspace/cell, and
        // FormIDs can be reused), so drop it on every rebuild.
        ClearSelection3D();

        if (_selectedInterior is { } interior)
        {
            BuildInteriorCellGrid(interior);
            return;
        }

        var (cells, defaultWaterHeight) = GetSelectedWorldspaceCells(_data);
        var cellList = cells.ToList();

        var activeWorldspaceFormId = GetSelectedWorldspaceFormId(_data);
        var markers = GetSelectedWorldspaceMarkers(_data, activeWorldspaceFormId);
        _spatialIndex = WorldSpatialIndex.Build(
            _data, cellList, markers, activeWorldspaceFormId, defaultWaterHeight);

        _cellGridLookup = _spatialIndex.CellsByGrid.ToDictionary(kv => kv.Key, kv => kv.Value);
        var appearance = GetSelectedWaterAppearance(_data);
        var noiseIndex = _textureResolver12?.ResolveNormalMapBindlessIndex(appearance?.NoiseTexture);
        _cellGrid?.LoadData(cellList, _spatialIndex);
        if (ComputeGridZExtent(cellList) is { } zext) _cellGrid?.SetWorldZExtent(zext.zMin, zext.zMax);
        _terrain?.LoadData(_cellGridLookup, _spatialIndex, _data.RenderCache);
        _water?.LoadData(_cellGridLookup, defaultWaterHeight, _spatialIndex, appearance, noiseIndex);
        _references?.LoadData(_data.RenderCache, _cellGridLookup, _spatialIndex);
        _navMesh?.LoadData(_data.NavMeshesByCell, _cellGridLookup, _spatialIndex);
        if (_collisionDebug is not null) { _collisionDebug.LoadData(_spatialIndex); _collisionDebug.ShowDisabled = _showDisabled; }
    }

    /// <summary>
    ///     Resolves the WATR appearance (DNAM Shallow/Deep/Reflection colors) for the selected
    ///     worldspace's default water — mirrors the 2D viewer, which colors the whole worldspace
    ///     from its single <c>WaterFormId</c>. Null (unlinked-exterior / no WATR) lets the
    ///     renderer fall back to a default tint.
    /// </summary>
    private BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? GetSelectedWaterAppearance(WorldViewData data)
    {
        var index = WorldspaceComboBox.SelectedIndex;
        if (index < 0 || index >= data.Worldspaces.Count) return null;
        if (data.Worldspaces[index].WaterFormId is not uint waterFormId) return null;
        return data.WatersByFormId.TryGetValue(waterFormId, out var water)
            ? BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance.FromWaterRecord(water)
            : null;
    }

    // ── Interior cell browser (shared 2D WorldMapCellBrowser logic) ──────────────────────────

    private void InteriorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _data.InteriorCells.Count == 0) return;
        PopulateInteriorBrowser();
        CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void CellBrowserCloseButton_Click(object sender, RoutedEventArgs e) => HideInteriorBrowser();

    private void HideInteriorBrowser() => CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

    private void PopulateInteriorBrowser()
    {
        if (_data is null) return;
        // groupInteriors:false → group by first letter (A..Z), like the 2D viewer's Interiors browser.
        _allInteriorItems = WorldMapCellBrowser.BuildCellListItems(_data.InteriorCells, groupInteriors: false, _data);
        CellSearchBox.Text = "";
        FilterHasObjects.IsChecked = false;
        FilterNamedOnly.IsChecked = false;
        RebuildInteriorList(_allInteriorItems);
    }

    private void CellSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyInteriorFilters();

    private void CellFilter_Changed(object sender, RoutedEventArgs e) => ApplyInteriorFilters();

    private void ApplyInteriorFilters()
    {
        var query = CellSearchBox.Text?.Trim() ?? "";
        var hasObjects = FilterHasObjects.IsChecked == true;
        var namedOnly = FilterNamedOnly.IsChecked == true;
        RebuildInteriorList(WorldMapCellBrowser.ApplyFilters(_allInteriorItems, query, hasObjects, namedOnly));
    }

    private void CellSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _interiorSortMode = CellSortCombo.SelectedIndex == 1 ? CellSortMode.ObjectCount : CellSortMode.Grid;
        // Re-apply the active filters so the list re-sorts in place (no-op before the browser populates).
        if (_allInteriorItems.Count > 0) ApplyInteriorFilters();
    }

    private void RebuildInteriorList(List<WorldMapControl.CellListItem> items)
    {
        var source = WorldMapCellBrowser.BuildGroupedSource(items, _interiorSortMode);
        var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = source };
        CellListView.ItemsSource = cvs.View;
        CellBrowserCountText.Text = $"{items.Count} cells";
    }

    private void CellListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        // ItemClick (not SelectionChanged) so ONLY a real user click loads a cell. Reassigning
        // ItemsSource on open/filter auto-selects item 0 and fired SelectionChanged, which used to
        // load the first interior on open and hijack every search keystroke — ItemClick never fires
        // for programmatic selection or type-ahead, so both symptoms are gone.
        if (e.ClickedItem is not WorldMapControl.CellListItem item) return;

        _selectedInterior = item.Cell;
        // Drop the combo selection so re-picking the same worldspace later still returns to exterior.
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.SelectedIndex = -1;
        _suppressWorldspaceSelectionEvent = false;

        HideInteriorBrowser();
        TryBuildCellGrid();
        ResetCameraToInteriorBounds(item.Cell);
        RefreshAtmosphereForCurrentWorldspace();
    }

    /// <summary>
    ///     Single-cell load path for an interior: builds a synthetic-grid spatial index
    ///     (<see cref="WorldSpatialIndex.BuildInterior" />) and loads the renderers against it.
    ///     Terrain has no LAND so it renders nothing; references/water/navmesh resolve via the
    ///     synthetic key. Water uses the interior's own XCLW (no worldspace default), default tint.
    /// </summary>
    private void BuildInteriorCellGrid(CellRecord interior)
    {
        if (_data is null) return;

        _spatialIndex = WorldSpatialIndex.BuildInterior(_data, interior);
        _cellGridLookup = _spatialIndex.CellsByGrid.ToDictionary(kv => kv.Key, kv => kv.Value);
        var cellList = new List<CellRecord> { interior };

        _cellGrid?.LoadData(cellList, _spatialIndex);
        if (ComputeGridZExtent(cellList) is { } zext) _cellGrid?.SetWorldZExtent(zext.zMin, zext.zMax);
        _terrain?.LoadData(_cellGridLookup, _spatialIndex, _data.RenderCache);
        _water?.LoadData(_cellGridLookup, worldspaceDefaultWaterHeight: null, _spatialIndex);
        _references?.LoadData(_data.RenderCache, _cellGridLookup, _spatialIndex);
        _navMesh?.LoadData(_data.NavMeshesByCell, _cellGridLookup, _spatialIndex);
        if (_collisionDebug is not null) { _collisionDebug.LoadData(_spatialIndex); _collisionDebug.ShowDisabled = _showDisabled; }
    }

    /// <summary>
    ///     Computes the vertical (Z) extent the cell-grid walls should span for the loaded cells,
    ///     from the placed-object Z range (objects rest on the terrain, so this brackets the relief
    ///     and tall structures) plus a one-cell margin, with a minimum span so flat worldspaces still
    ///     show tall-enough walls. Returns null when no finite placed-object Z exists (grid keeps its
    ///     default extent).
    /// </summary>
    private (float zMin, float zMax)? ComputeGridZExtent(IReadOnlyCollection<CellRecord> cells)
    {
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var cell in cells)
        {
            foreach (var o in cell.PlacedObjects)
            {
                if (!float.IsFinite(o.Z)) continue;
                if (o.Z < minZ) minZ = o.Z;
                if (o.Z > maxZ) maxZ = o.Z;
            }
        }
        if (minZ > maxZ) return null;

        var margin = _cellSize;
        var zMin = minZ - margin;
        var zMax = maxZ + margin;
        var minSpan = _cellSize * 2f;
        if (zMax - zMin < minSpan)
        {
            var mid = (zMin + zMax) * 0.5f;
            zMin = mid - minSpan * 0.5f;
            zMax = mid + minSpan * 0.5f;
        }
        return (zMin, zMax);
    }

    /// <summary>
    ///     Returns the exterior-cell list + default water height for whatever entry is currently
    ///     selected in <c>WorldspaceComboBox</c>. ComboBox layout: indices 0..N-1 map to
    ///     <c>_data.Worldspaces</c>; an optional final entry maps to <c>_data.UnlinkedExteriorCells</c>.
    ///     Returns empty when nothing is selected (e.g. an empty file).
    /// </summary>
    private (IEnumerable<CellRecord> Cells, float? DefaultWaterHeight) GetSelectedWorldspaceCells(WorldViewData data)
    {
        var index = WorldspaceComboBox.SelectedIndex;
        if (index < 0) return (Enumerable.Empty<CellRecord>(), null);

        if (index < data.Worldspaces.Count)
        {
            var ws = data.Worldspaces[index];
            return (ws.Cells.Where(c => c.GridX is int && c.GridY is int), ws.DefaultWaterHeight);
        }

        // Tail entry: unlinked exterior cells. No worldspace → no DefaultWaterHeight fallback.
        return (data.UnlinkedExteriorCells.Where(c => c.GridX is int && c.GridY is int), null);
    }

    private uint? GetSelectedWorldspaceFormId(WorldViewData data)
    {
        var index = WorldspaceComboBox.SelectedIndex;
        return index >= 0 && index < data.Worldspaces.Count
            ? data.Worldspaces[index].FormId
            : null;
    }

    /// <summary>
    ///     Ensures the active worldspace matches <paramref name="worldspaceFormId" /> (null = the
    ///     unlinked-exterior set) so the top-down overlay renders the worldspace the 2D map is showing.
    ///     Switches + rebuilds the cell grid only when it differs (worldspace switches are infrequent).
    ///     Suppresses the selection event so the camera isn't reset. Returns false when no matching
    ///     exterior worldspace exists (the caller then skips the overlay).
    /// </summary>
    private bool EnsureActiveExteriorWorldspace(uint? worldspaceFormId)
    {
        if (_data is null) return false;

        // Already on the requested exterior worldspace (and not in interior mode)?
        if (_selectedInterior is null && GetSelectedWorldspaceFormId(_data) == worldspaceFormId)
        {
            return true;
        }

        var targetIndex = -1;
        if (worldspaceFormId is uint ws)
        {
            for (var i = 0; i < _data.Worldspaces.Count; i++)
            {
                if (_data.Worldspaces[i].FormId == ws) { targetIndex = i; break; }
            }
        }
        else if (_data.UnlinkedExteriorCells.Count > 0)
        {
            targetIndex = _data.Worldspaces.Count; // the unlinked-exterior tail entry
        }

        if (targetIndex < 0) return false;

        _selectedInterior = null; // switching to an exterior worldspace leaves interior mode
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.SelectedIndex = targetIndex;
        _suppressWorldspaceSelectionEvent = false;
        TryBuildCellGrid();
        RefreshAtmosphereForCurrentWorldspace();
        return true;
    }

    private static List<PlacedReference> GetSelectedWorldspaceMarkers(WorldViewData data, uint? worldspaceFormId)
    {
        if (worldspaceFormId is uint ws &&
            data.MarkersByWorldspace.TryGetValue(ws, out var markers))
        {
            return markers;
        }

        return worldspaceFormId is null ? data.UnlinkedMapMarkers : [];
    }
}
