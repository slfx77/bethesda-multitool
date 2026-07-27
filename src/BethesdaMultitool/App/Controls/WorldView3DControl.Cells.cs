using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WaterRenderer12 = BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.WaterRenderer12;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private async void WorldspaceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorldspaceSelectionEvent || _data is null) return;
        if (WorldspaceComboBox.SelectedIndex < 0) return;

        var selectionGeneration = BeginSceneSelection();
        var selectionCompleted = false;

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
            // A newer worldspace/interior request may have arrived while this async-void handler was
            // yielded. Never let the stale continuation rebuild the old grid or reset the new camera.
            if (!IsCurrentSceneSelection(selectionGeneration)) return;

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
            RefreshAtmosphereForCurrentWorldspace();
            RefreshExportBounds(); // the export tab's bounds/output-size follow the active worldspace
            selectionCompleted = true;
        }
        finally
        {
            if (IsCurrentSceneSelection(selectionGeneration))
            {
                if (selectionCompleted)
                {
                    MarkSceneSelectionReady(selectionGeneration);
                }
                HideStatus();
            }
        }
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

        var (cells, defaultWaterHeight, waterFromParent) = GetSelectedWorldspaceCells(_data);
        var cellList = cells.ToList();

        var activeWorldspaceFormId = GetSelectedWorldspaceFormId(_data);
        Log.Info(
            "WorldView3DControl: building cell grid for worldspace[{0}] 0x{1:X8} — {2} gridded cells.",
            WorldspaceComboBox.SelectedIndex, activeWorldspaceFormId ?? 0, cellList.Count);
        _spatialIndex = WorldSpatialIndex.BuildFor3D(_data, cellList, defaultWaterHeight, waterFromParent);

        // The grass water-cull bake reads the render cache's default; keep it tracking the ACTIVE
        // worldspace (LoadData seeds it from Worldspaces[0] only).
        _data.RenderCache.DefaultWaterHeight = defaultWaterHeight;
        _data.RenderCache.DefaultWaterRequiresCellHasWater = waterFromParent;

        _cellGridLookup = _spatialIndex.CellsByGrid.ToDictionary(kv => kv.Key, kv => kv.Value);
        var activeWorldspace = CurrentSelectedExteriorWorldspace();
        var initialWaterSelection = WaterAppearanceSelectionResolver.Resolve(
            cell: null,
            worldspace: activeWorldspace,
            watersByFormId: _data.WatersByFormId);
        var appearance = WaterAppearance.FromWaterRecord(initialWaterSelection.Water);
        var normalIndices = ResolveWaterNormalIndices(appearance);
        var oblivionDetailIndex = WaterProfile.ForGame(_data.Game).UsesWatrDetailTexture &&
                                   appearance?.SurfaceTexture is { Length: > 0 } detailPath
            ? _textureResolver12?.ResolveDiffuseBindlessIndex(detailPath)
            : null;
        _cellGrid?.LoadData(cellList, _spatialIndex);
        _worldZExtent = ComputeGridZExtent(cellList);
        if (_worldZExtent is { } zext) _cellGrid?.SetWorldZExtent(zext.zMin, zext.zMax);
        _terrain?.LoadData(_cellGridLookup, _spatialIndex, _data.RenderCache);
        _water?.SetGame(_data.Game);
        _water?.SetFnvWaterMaterialCatalog(ResolveFnvWaterMaterialCatalog());
        _water?.SetLegacyAnimatedFrames(ResolveLegacyAnimatedWaterFrames());
        _water?.SetOblivionDetailTexture(oblivionDetailIndex);
        if (_water is not null) _water.DefaultWaterRequiresCellHasWater = waterFromParent;
        _water?.LoadData(_cellGridLookup, defaultWaterHeight, _spatialIndex, appearance, normalIndices);
        _water?.SetFnvWater001WaterTypeContext(
            initialWaterSelection.WaterFormId,
            activeWorldspace?.WaterFormId);
        _waterAppearanceSelection = initialWaterSelection;
        _boundWaterAppearanceFormId = initialWaterSelection.WaterFormId;
        _hasBoundWaterAppearance = true;
        _references?.LoadData(_data.RenderCache, _cellGridLookup, _spatialIndex);
        _navMesh?.LoadData(_data.NavMeshesByCell, _cellGridLookup, _spatialIndex);
        if (_collisionDebug is not null)
        {
            _collisionDebug.LoadData(_spatialIndex, _data.CategoryIndex, _data.Game);
            _collisionDebug.ShowDisabled = _showDisabled;
            _collisionDebug.XespDisabledRefs = _data.XespDisabledRefs;
        }
    }

    /// <summary>
    ///     Resolves the shared 32-frame <c>textures\water\water00–31.dds</c> animation. Morrowind
    ///     samples it as a diffuse surface, while Oblivion WATER000 samples it as the global animated
    ///     normal input; using WATR TNAM for that slot was incorrect because TNAM is per-water detail
    ///     content (and DefaultWater's TNAM is empty). Null for every other game.
    /// </summary>
    private uint[]? ResolveLegacyAnimatedWaterFrames()
    {
        if (_data is null || _textureResolver12 is null)
        {
            return null;
        }

        var frameRole = WaterProfile.ForGame(_data.Game).LegacyFrames;
        if (frameRole == LegacySurfaceFrameRole.None)
        {
            return null;
        }

        var frames = new List<uint>(LegacyWaterAnimation.FrameCount);
        // Probe EXISTENCE, not the resolved index: the texture cache returns a valid, permanently
        // placeholder bindless index for a path it can never resolve, so index-based probing can
        // never report "this game ships no frames" (see LegacyWaterAnimation.ExistingFramePaths).
        foreach (var path in LegacyWaterAnimation.ExistingFramePaths(_textureResolver12.TextureExists))
        {
            var index = frameRole == LegacySurfaceFrameRole.Diffuse
                ? _textureResolver12.ResolveDiffuseBindlessIndex(path)
                : _textureResolver12.ResolveNormalMapBindlessIndex(path);
            if (index is uint idx)
            {
                frames.Add(idx);
            }
        }

        // Retail Oblivion ships NO water00-31.dds — the engine GENERATES its 32-frame surface
        // animation at runtime from ini [Water] settings (uSurfaceFrameCount=32, uSurfaceFPS=12,
        // uSurfaceTextureSize=128). With zero disk/BSA frames the surface stayed on the static
        // procedural ripple ("water isn't animated"). Synthesize the seamless loop instead and
        // feed it through the same plumbing; disk frames (mod replacers) still win when present.
        // Morrowind (Diffuse role) always resolves its BSA-shipped frames above and never gets here.
        if (frames.Count == 0 &&
            _data.Game == BethesdaMultitool.Core.Games.BethesdaGame.Oblivion &&
            frameRole == LegacySurfaceFrameRole.GlobalNormal)
        {
            var synthesized = BethesdaMultitool.Core.Formats.Nif.Rendering.Camera
                .OblivionWaterSurfaceSynthesizer.GenerateFrames();
            for (var i = 0; i < synthesized.Length; i++)
            {
                frames.Add(_textureResolver12.GetOrCreateSyntheticBindlessIndex(
                    $"synthetic:oblivion-water-surface:{i:D2}",
                    BethesdaMultitool.Core.Formats.Nif.Rendering.Camera
                        .OblivionWaterSurfaceSynthesizer.TextureSize,
                    BethesdaMultitool.Core.Formats.Nif.Rendering.Camera
                        .OblivionWaterSurfaceSynthesizer.TextureSize,
                    synthesized[i]));
            }
        }

        return frames.Count > 0 ? frames.ToArray() : null;
    }

    private uint?[]? ResolveWaterNormalIndices(WaterAppearance? appearance)
    {
        if (appearance?.NormalTextures is { Count: > 0 } textures)
        {
            return textures.Select(path => _textureResolver12?.ResolveNormalMapBindlessIndex(path)).ToArray();
        }

        return appearance?.NoiseTexture is { } noise
            ? new uint?[] { _textureResolver12?.ResolveNormalMapBindlessIndex(noise) }
            : null;
    }

    private Dictionary<uint, WaterRenderer12.FnvWaterMaterialBinding>?
        ResolveFnvWaterMaterialCatalog()
    {
        if (_data?.Game != BethesdaMultitool.Core.Games.BethesdaGame.FalloutNewVegas)
        {
            return null;
        }

        var result = new Dictionary<uint, WaterRenderer12.FnvWaterMaterialBinding>(
            _data.WatersByFormId.Count);
        foreach (var (formId, water) in _data.WatersByFormId)
        {
            if (formId == 0) continue;
            var appearance = WaterAppearance.FromWaterRecord(water);
            result[formId] = new WaterRenderer12.FnvWaterMaterialBinding(
                appearance, ResolveWaterNormalIndices(appearance));
        }

        return result;
    }

    /// <summary>
    ///     Selects the current FNV camera CELL's XCWT material, falling back to WRLD NAM2 when that
    ///     override is absent or unresolved. Only the material binding changes when the selected
    ///     WATR changes; water instances and their spatial index remain resident.
    /// </summary>
    private void RefreshWaterAppearanceForCurrentCell(bool force = false)
    {
        if (_data is null || _water is null ||
            _data.Game != BethesdaMultitool.Core.Games.BethesdaGame.FalloutNewVegas)
        {
            return;
        }

        var cellContext = CurrentImageSpaceCellContext();
        var worldspace = _selectedInterior is null ? CurrentSelectedExteriorWorldspace() : null;
        var selection = WaterAppearanceSelectionResolver.Resolve(
            cellContext.Cell,
            worldspace,
            _data.WatersByFormId);
        _waterAppearanceSelection = selection;

        if (!force && _hasBoundWaterAppearance &&
            _boundWaterAppearanceFormId == selection.WaterFormId)
        {
            return;
        }

        var appearance = WaterAppearance.FromWaterRecord(selection.Water);
        _water.SetAppearance(appearance, ResolveWaterNormalIndices(appearance));
        _water.SetFnvWater001WaterTypeContext(selection.WaterFormId, worldspace?.WaterFormId);
        _boundWaterAppearanceFormId = selection.WaterFormId;
        _hasBoundWaterAppearance = true;
        Log.Info(
            "[Water] selected {0} WATR {1} for CELL {2}.",
            selection.SourceTelemetry,
            selection.WaterFormId is { } waterFormId ? $"0x{waterFormId:X8}" : "unavailable",
            selection.CellFormId is { } cellFormId ? $"0x{cellFormId:X8}" : "unavailable");
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
        var selectionGeneration = BeginSceneSelection();
        _selectedInterior = cell;
        // Drop the combo selection so re-picking the same worldspace later still returns to exterior.
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.SelectedIndex = -1;
        _suppressWorldspaceSelectionEvent = false;

        HideInteriorBrowser();
        TryBuildCellGrid();
        ResetCameraToInteriorBounds(cell);
        RefreshAtmosphereForCurrentWorldspace();
        MarkSceneSelectionReady(selectionGeneration);
        HideStatus();
    }

    /// <summary>
    ///     Single-cell load path for an interior: builds a synthetic-grid spatial index
    ///     (<see cref="WorldSpatialIndex.BuildInterior" />) and loads the renderers against it.
    ///     Terrain has no LAND so it renders nothing; references/water/navmesh resolve via the
    ///     synthetic key. Water uses the interior's own XCLW and XCWT (no worldspace fallback).
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
        _water?.SetFnvWaterMaterialCatalog(ResolveFnvWaterMaterialCatalog());
        _water?.SetLegacyAnimatedFrames(ResolveLegacyAnimatedWaterFrames());
        _water?.SetOblivionDetailTexture(null);
        var waterSelection = WaterAppearanceSelectionResolver.Resolve(
            cell: interior,
            worldspace: null,
            watersByFormId: _data.WatersByFormId);
        var appearance = WaterAppearance.FromWaterRecord(waterSelection.Water);
        if (_water is not null) _water.DefaultWaterRequiresCellHasWater = false;
        _water?.LoadData(
            _cellGridLookup,
            worldspaceDefaultWaterHeight: null,
            _spatialIndex,
            appearance,
            ResolveWaterNormalIndices(appearance));
        _water?.SetFnvWater001WaterTypeContext(waterSelection.WaterFormId, null);
        _waterAppearanceSelection = waterSelection;
        _boundWaterAppearanceFormId = waterSelection.WaterFormId;
        _hasBoundWaterAppearance = true;
        _references?.LoadData(_data.RenderCache, _cellGridLookup, _spatialIndex);
        _navMesh?.LoadData(_data.NavMeshesByCell, _cellGridLookup, _spatialIndex);
        if (_collisionDebug is not null)
        {
            _collisionDebug.LoadData(_spatialIndex, _data.CategoryIndex, _data.Game);
            _collisionDebug.ShowDisabled = _showDisabled;
            _collisionDebug.XespDisabledRefs = _data.XespDisabledRefs;
        }
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
    private (IEnumerable<CellRecord> Cells, float? DefaultWaterHeight, bool WaterFromParent)
        GetSelectedWorldspaceCells(WorldViewData data)
    {
        var index = WorldspaceComboBox.SelectedIndex;
        if (index < 0) return (Enumerable.Empty<CellRecord>(), null, false);

        if (index < data.Worldspaces.Count)
        {
            var ws = data.Worldspaces[index];
            return (ws.Cells.Where(c => c.GridX is int && c.GridY is int), ws.DefaultWaterHeight,
                ws.WaterFromParentWorldspace);
        }

        // Tail entry: unlinked exterior cells. No worldspace → no DefaultWaterHeight fallback.
        return (data.UnlinkedExteriorCells.Where(c => c.GridX is int && c.GridY is int), null, false);
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

        var selectionGeneration = BeginSceneSelection();
        _selectedInterior = null; // switching to an exterior worldspace leaves interior mode
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.SelectedIndex = targetIndex;
        _suppressWorldspaceSelectionEvent = false;
        TryBuildCellGrid();
        RefreshAtmosphereForCurrentWorldspace();
        MarkSceneSelectionReady(selectionGeneration);
        HideStatus();
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

        var selectionGeneration = BeginSceneSelection();
        _selectedInterior = interior;
        // Drop the combo selection so the grid builder takes the interior path (TryBuildCellGrid checks
        // _selectedInterior first); suppress the event so the live camera isn't reset under the overlay.
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.SelectedIndex = -1;
        _suppressWorldspaceSelectionEvent = false;
        TryBuildCellGrid();
        RefreshAtmosphereForCurrentWorldspace();
        MarkSceneSelectionReady(selectionGeneration);
        HideStatus();
        return true;
    }
}
