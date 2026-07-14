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
        Log.Info(
            "WorldView3DControl: building cell grid for worldspace[{0}] 0x{1:X8} — {2} gridded cells.",
            WorldspaceComboBox.SelectedIndex, activeWorldspaceFormId ?? 0, cellList.Count);
        _spatialIndex = WorldSpatialIndex.BuildFor3D(_data, cellList, defaultWaterHeight);

        _cellGridLookup = _spatialIndex.CellsByGrid.ToDictionary(kv => kv.Key, kv => kv.Value);
        var appearance = GetSelectedWaterAppearance(_data);
        var noiseIndex = _textureResolver12?.ResolveNormalMapBindlessIndex(appearance?.NoiseTexture);
        _cellGrid?.LoadData(cellList, _spatialIndex);
        _worldZExtent = ComputeGridZExtent(cellList);
        if (_worldZExtent is { } zext) _cellGrid?.SetWorldZExtent(zext.zMin, zext.zMax);
        _terrain?.LoadData(_cellGridLookup, _spatialIndex, _data.RenderCache);
        _water?.SetGame(_data.Game);
        _water?.SetMorrowindSurfaceFrames(ResolveMorrowindWaterFrames());
        _water?.LoadData(_cellGridLookup, defaultWaterHeight, _spatialIndex, appearance, noiseIndex);
        _references?.LoadData(_data.RenderCache, _cellGridLookup, _spatialIndex);
        _navMesh?.LoadData(_data.NavMeshesByCell, _cellGridLookup, _spatialIndex);
        if (_collisionDebug is not null) { _collisionDebug.LoadData(_spatialIndex); _collisionDebug.ShowDisabled = _showDisabled; }
    }

    /// <summary>
    ///     Resolves the bindless indices of Morrowind's 32 animated water-surface frames
    ///     (<c>textures\water\water00–31.dds</c>, the <c>[Water] SurfaceTexture/SurfaceFrameCount</c>
    ///     cycle) for <c>WaterShaderVariant.MorrowindWater</c>. Null for every other game — the
    ///     renderer ignores the frames unless the Morrowind profile is active. GetOrUpload returns a
    ///     valid placeholder-backed index immediately, so missing frames simply drop out of the cycle.
    /// </summary>
    private uint[]? ResolveMorrowindWaterFrames()
    {
        if (_data?.Game != BethesdaMultitool.Core.Games.BethesdaGame.Morrowind || _textureResolver12 is null)
        {
            return null;
        }

        const int frameCount = 32; // [Water] SurfaceFrameCount
        var frames = new List<uint>(frameCount);
        for (var i = 0; i < frameCount; i++)
        {
            if (_textureResolver12.ResolveDiffuseBindlessIndex($@"textures\water\water{i:D2}.dds") is uint idx)
            {
                frames.Add(idx);
            }
        }

        return frames.Count > 0 ? frames.ToArray() : null;
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

    // ── Interior cell browser (shared CellListControl) ───────────────────────────────────────

    private async void InteriorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _data.InteriorCells.Count == 0) return;
        CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        // groupInteriors (Interiors mode) → group by first letter (A..Z), like the 2D viewer.
        await CellList.PopulateAsync(_data.InteriorCells, CellListControl.CellListMode.Interiors, _data);
    }

    private void CellBrowserCloseButton_Click(object sender, RoutedEventArgs e) => HideInteriorBrowser();

    private void HideInteriorBrowser() => CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

    private void CellList_CellActivated(object? sender, CellRecord cell)
    {
        _selectedInterior = cell;
        // Drop the combo selection so re-picking the same worldspace later still returns to exterior.
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.SelectedIndex = -1;
        _suppressWorldspaceSelectionEvent = false;

        HideInteriorBrowser();
        TryBuildCellGrid();
        ResetCameraToInteriorBounds(cell);
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
        _worldZExtent = ComputeGridZExtent(cellList);
        if (_worldZExtent is { } zext) _cellGrid?.SetWorldZExtent(zext.zMin, zext.zMax);
        _terrain?.LoadData(_cellGridLookup, _spatialIndex, _data.RenderCache);
        _water?.SetGame(_data.Game);
        _water?.SetMorrowindSurfaceFrames(ResolveMorrowindWaterFrames());
        _water?.LoadData(_cellGridLookup, worldspaceDefaultWaterHeight: null, _spatialIndex);
        _references?.LoadData(_data.RenderCache, _cellGridLookup, _spatialIndex);
        _navMesh?.LoadData(_data.NavMeshesByCell, _cellGridLookup, _spatialIndex);
        if (_collisionDebug is not null) { _collisionDebug.LoadData(_spatialIndex); _collisionDebug.ShowDisabled = _showDisabled; }
    }

    /// <summary>
    ///     Z extent of the loaded grid (from <see cref="ComputeGridZExtent" />), captured at grid build
    ///     (exterior AND interior paths, so it can't go stale switching between them) for the ortho cull
    ///     radius's terrain-relief parallax term (<see cref="BuildProjectionViewProj" />). Null when the
    ///     grid has no finite placed-object Z — the relief term then drops to zero.
    /// </summary>
    private (float zMin, float zMax)? _worldZExtent;

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

    /// <summary>
    ///     Ensures the active view is the interior cell <paramref name="interiorFormId" /> so the
    ///     top-down overlay renders the interior the 2D map is showing. Mirrors
    ///     <see cref="EnsureActiveExteriorWorldspace" /> for the interior case (the 2D map's
    ///     cell-detail overlay path). Switches + rebuilds the single-cell grid only when it differs.
    ///     Suppresses the worldspace selection event so the live camera isn't disturbed. Returns false
    ///     when no interior cell with that FormID exists.
    /// </summary>
    private bool EnsureActiveInteriorCell(uint interiorFormId)
    {
        if (_data is null) return false;
        if (_selectedInterior?.FormId == interiorFormId) return true; // already active

        CellRecord? interior = null;
        foreach (var c in _data.InteriorCells)
        {
            if (c.FormId == interiorFormId) { interior = c; break; }
        }

        if (interior is null) return false;

        _selectedInterior = interior;
        // Drop the combo selection so the grid builder takes the interior path (TryBuildCellGrid checks
        // _selectedInterior first); suppress the event so the live camera isn't reset under the overlay.
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.SelectedIndex = -1;
        _suppressWorldspaceSelectionEvent = false;
        TryBuildCellGrid();
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
