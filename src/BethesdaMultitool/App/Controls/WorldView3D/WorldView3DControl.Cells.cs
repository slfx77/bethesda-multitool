using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.WorldData;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WaterRenderer12 = BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.WaterRenderer12;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     Z extent of the loaded grid (from <see cref="ComputeGridZExtent" />), captured at grid build
    ///     (exterior AND interior paths, so it can't go stale switching between them) for the ortho cull
    ///     radius's terrain-relief parallax term (<see cref="BuildProjectionViewProj" />). Null when the
    ///     grid has no finite placed-object Z — the relief term then drops to zero.
    /// </summary>
    private (float zMin, float zMax)? _worldZExtent;

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

        // Adopt the world's UNIT scale too. Separate from the cell size on purpose: Starfield's cell is
        // 40.96× smaller than Fallout's while its unit is 70× bigger, so walk-mode eye height, step
        // height and movement speed have to follow the unit, not the grid.
        _unitScale = GameProfiles.HumanScaleFactor(_data.Game);
        _controller.SetUnitScale(_unitScale);
        // The near plane is a human-scale distance too: 16 classic units is ~23 cm, but 16 METRES in
        // Starfield, which slices the scene away just in front of the camera.
        _camera.NearPlane = CameraState.DefaultNearPlane * _unitScale;

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
            watersByFormId: _data.WatersByFormId,
            game: _data.Game,
            isInterior: false);
        var appearance = WaterAppearance.FromWaterRecord(initialWaterSelection.Water);
        var starfieldApproximation = _data.Game == BethesdaGame.Starfield
            ? StarfieldWaterApproximation.FromWaterRecord(initialWaterSelection.Water)
            : null;
        var normalIndices = ResolveWaterNormalIndices(appearance, starfieldApproximation);
        var oblivionDetailIndex = ResolveWatrDetailTextureIndex(initialWaterSelection.Water);
        _cellGrid?.LoadData(cellList, _spatialIndex);
        _worldZExtent = ComputeGridZExtent(cellList);
        if (_worldZExtent is { } zext) _cellGrid?.SetWorldZExtent(zext.zMin, zext.zMax);
        _terrain?.LoadData(_cellGridLookup, _spatialIndex, _data.RenderCache);
        _water?.SetGame(_data.Game);
        _water?.SetFnvWaterMaterialCatalog(ResolveFnvWaterMaterialCatalog());
        _water?.SetLegacyAnimatedFrames(ResolveLegacyAnimatedWaterFrames());
        _water?.SetOblivionDetailTexture(oblivionDetailIndex);
        if (_water is not null)
        {
            _water.DefaultWaterRequiresCellHasWater = waterFromParent;
            // Video-settings "Water ripples": re-seed the persisted toggle and the flat-normal
            // substitute it swaps in (the ripples-off calm sheet) on every worldspace load.
            _water.RipplesEnabled = _waterRipplesEnabled;
            if (_textureResolver12 is not null)
            {
                _water.FlatNormalBindlessIndex = _textureResolver12.FlatNormalFallback.BindlessIndex;
            }
        }

        _water?.LoadData(_cellGridLookup, defaultWaterHeight, _spatialIndex, appearance, normalIndices);
        _water?.SetStarfieldApproximation(starfieldApproximation);
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
            _collisionDebug.DayNightStates = _dayNightStates;
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
            var synthesized = BethesdaMultitool.Core.Formats.Nif.Rendering.Water
                .OblivionWaterSurfaceSynthesizer.GenerateFrames();
            for (var i = 0; i < synthesized.Length; i++)
            {
                frames.Add(_textureResolver12.GetOrCreateSyntheticBindlessIndex(
                    $"synthetic:oblivion-water-surface:{i:D2}",
                    BethesdaMultitool.Core.Formats.Nif.Rendering.Water
                        .OblivionWaterSurfaceSynthesizer.TextureSize,
                    BethesdaMultitool.Core.Formats.Nif.Rendering.Water
                        .OblivionWaterSurfaceSynthesizer.TextureSize,
                    synthesized[i]));
            }
        }

        return frames.Count > 0 ? frames.ToArray() : null;
    }

    private uint?[]? ResolveWaterNormalIndices(
        WaterAppearance? appearance,
        StarfieldWaterApproximation? starfieldApproximation = null)
    {
        if (starfieldApproximation is not null)
        {
            return StarfieldWaterApproximation.InferredGlobalTexturePaths
                .Select(path => _textureResolver12?.ResolveNormalMapBindlessIndex(path))
                .ToArray();
        }

        if (appearance?.NormalTextures is { Count: > 0 } textures)
        {
            return textures.Select(path => _textureResolver12?.ResolveNormalMapBindlessIndex(path)).ToArray();
        }

        return appearance?.NoiseTexture is { } noise
            ? new uint?[] { _textureResolver12?.ResolveNormalMapBindlessIndex(noise) }
            : null;
    }

    /// <summary>
    ///     Resolves TES4 WATR TNAM as the per-water detail texture. It is deliberately separate
    ///     from the shared water00..31 normal animation and must follow every XCWT material rebind.
    /// </summary>
    private uint? ResolveWatrDetailTextureIndex(WaterRecord? water)
    {
        return _data is not null && WaterProfile.ForGame(_data.Game).UsesWatrDetailTexture &&
               water?.SurfaceTexture is { Length: > 0 } detailPath
            ? _textureResolver12?.ResolveDiffuseBindlessIndex(detailPath)
            : null;
    }

    private Dictionary<uint, WaterRenderer12.FnvWaterMaterialBinding>?
        ResolveFnvWaterMaterialCatalog()
    {
        // FO3 parity 2026-08-10: widened from FNV-only. The catalog is pure WATR record data;
        // FO3 shares the record layout and the shader path (both use the classic Fnv profile),
        // and retail Fallout3.esm authors 124 XCWT cell overrides that need per-FormID bindings.
        if (_data?.Game is not (BethesdaMultitool.Core.Games.BethesdaGame.FalloutNewVegas
            or BethesdaMultitool.Core.Games.BethesdaGame.Fallout3))
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
    ///     Selects the current camera CELL's XCWT material for the games whose retail data authors
    ///     per-cell overrides, falling back to WRLD NAM2 when that override is absent or unresolved.
    ///     Only the material binding changes when the selected WATR changes; water instances and
    ///     their spatial index remain resident.
    /// </summary>
    private void RefreshWaterAppearanceForCurrentCell(bool force = false)
    {
        // FO3 parity 2026-08-10: widened from FNV-only. Starfield parity 2026-08-31: retail has six
        // authored CELL XCWT overrides, including New Atlantis where WRLD NAM2 is absent. Before
        // this, Starfield stayed on the world-load NAM2 (or unavailable) while crossing those cells.
        // Oblivion uses the same XCWT -> WRLD NAM2 selection and additionally rebinds the selected
        // WATR's TNAM detail map; otherwise both color/flags and WATER000 DetailMap stayed world-global.
        // Record semantics only: XCWT → NAM2, with the engine-default tier still scoped to FO3/FNV.
        // The FNV-only WATER001 route is unaffected (its contract hard-fails non-FNV input).
        if (_data is null || _water is null ||
            _data.Game is not (BethesdaMultitool.Core.Games.BethesdaGame.FalloutNewVegas
                or BethesdaMultitool.Core.Games.BethesdaGame.Fallout3
                or BethesdaMultitool.Core.Games.BethesdaGame.Oblivion
                or BethesdaMultitool.Core.Games.BethesdaGame.Starfield))
        {
            return;
        }

        var cellContext = CurrentImageSpaceCellContext();
        var worldspace = _selectedInterior is null ? CurrentSelectedExteriorWorldspace() : null;
        var selection = WaterAppearanceSelectionResolver.Resolve(
            cellContext.Cell,
            worldspace,
            _data.WatersByFormId,
            _data.Game,
            isInterior: _selectedInterior is not null);
        _waterAppearanceSelection = selection;

        if (!force && _hasBoundWaterAppearance &&
            _boundWaterAppearanceFormId == selection.WaterFormId)
        {
            return;
        }

        var appearance = WaterAppearance.FromWaterRecord(selection.Water);
        var starfieldApproximation = _data.Game == BethesdaGame.Starfield
            ? StarfieldWaterApproximation.FromWaterRecord(selection.Water)
            : null;
        _water.SetAppearance(
            appearance,
            ResolveWaterNormalIndices(appearance, starfieldApproximation));
        _water.SetOblivionDetailTexture(ResolveWatrDetailTextureIndex(selection.Water));
        _water.SetStarfieldApproximation(starfieldApproximation);
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
        ShowCellBrowser("Interior cells");
        // groupInteriors (Interiors mode) → group by first letter (A..Z), like the 2D viewer.
        await CellList.PopulateAsync(_data.InteriorCells, CellListControl.CellListMode.Interiors, _data);
    }

    /// <summary>
    ///     The 3D counterpart of the 2D map's All Cells browser
    ///     (<see cref="WorldMapControl.AllCellsButton_Click" />). Activation routes through
    ///     <see cref="NavigateToCell" />, which already handles all three outcomes: interior →
    ///     single-cell scene, exterior in another worldspace → switch and re-frame, exterior in the
    ///     current one → just centre the camera.
    /// </summary>
    private async void AllCellsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _data.AllCells.Count == 0) return;
        ShowCellBrowser("All cells");
        await CellList.PopulateAsync(_data.AllCells, CellListControl.CellListMode.AllCells, _data);
    }

    /// <summary>
    ///     Raises the cell browser in <paramref name="header" /> mode. Both viewport browsers are
    ///     full-bleed overlays over the SAME SwapChainPanel, so they MUST be mutually exclusive: the
    ///     worldspace browser is declared after the cell browser and therefore stacks above it, so
    ///     opening a cell list while it was up used to look like the button did nothing, and closing the
    ///     worldspace browser then revealed the cell list instead of the 3D view — which read as "the
    ///     browser won't close unless I pick a worldspace" (a worldspace pick is the one path that
    ///     collapses both).
    /// </summary>
    private void ShowCellBrowser(string header)
    {
        HideWorldspaceBrowser();
        CellBrowserHeader.Text = header;
        CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        // Keyboard focus is on the toolbar button that opened this; move it into the search box so
        // typing filters the list instead of driving the flythrough camera.
        CellList.FocusSearch();
    }

    private void CellBrowserCloseButton_Click(object sender, RoutedEventArgs e) => HideInteriorBrowser();

    private void HideInteriorBrowser() => CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

    // ── Worldspace browser (searchable counterpart of the toolbar ComboBox) ──────────────────

    /// <summary>
    ///     Opens the searchable worldspace list. The toolbar ComboBox stays the authoritative
    ///     selection — this only offers a better way to find an entry in it, which matters once a game
    ///     ships hundreds (Starfield has ~750). Mutually exclusive with the cell browser (see
    ///     <see cref="ShowCellBrowser" />): in 3D the scene is always underneath, so closing either
    ///     overlay has to land back on the viewport.
    /// </summary>
    private void WorldspacesButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorldspaceComboBox.Items.Count == 0) return;
        HideInteriorBrowser();
        WorldspaceBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        WorldspaceList.FocusSearch();
    }

    private void WorldspaceBrowserCloseButton_Click(object sender, RoutedEventArgs e) =>
        HideWorldspaceBrowser();

    private void HideWorldspaceBrowser() =>
        WorldspaceBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    ///     Applies a pick from the worldspace browser by driving the ComboBox, so the existing
    ///     SelectionChanged path does the actual load (cell grid, camera framing, atmosphere) and this
    ///     feature adds no second way to switch worlds.
    /// </summary>
    private void WorldspaceList_WorldspaceActivated(object? sender, int comboIndex)
    {
        HideWorldspaceBrowser();
        if (comboIndex < 0 || comboIndex >= WorldspaceComboBox.Items.Count) return;
        // Re-picking the active worldspace is a no-op for the combo (SelectionChanged won't fire), which
        // is the right outcome: the user is already there. No HideStatus() after this: the
        // SelectionChanged handler raises the "Loading worldspace…" overlay synchronously and hides it
        // itself when the load completes — hiding here would collapse that overlay the moment it
        // appears, leaving the big-worldspace load looking like an unexplained UI freeze.
        WorldspaceComboBox.SelectedIndex = comboIndex;
    }

    /// <summary>
    ///     Handles a pick from either browser mode. This deliberately does NOT assume the cell is an
    ///     interior: it used to set <c>_selectedInterior</c> and clear the worldspace combo
    ///     unconditionally, which was harmless while only the Interiors list existed but would load an
    ///     exterior as a synthetic single-cell interior — no terrain neighbours, no worldspace, no sky.
    ///     <see cref="NavigateToCell" /> dispatches on the cell's own grid coordinates instead.
    /// </summary>
    private void CellList_CellActivated(object? sender, CellRecord cell)
    {
        HideInteriorBrowser();
        NavigateToCell(cell);
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
        var waterSelection = WaterAppearanceSelectionResolver.Resolve(
            cell: interior,
            worldspace: null,
            watersByFormId: _data.WatersByFormId,
            game: _data.Game,
            isInterior: true);
        var appearance = WaterAppearance.FromWaterRecord(waterSelection.Water);
        var starfieldApproximation = _data.Game == BethesdaGame.Starfield
            ? StarfieldWaterApproximation.FromWaterRecord(waterSelection.Water)
            : null;
        _water?.SetOblivionDetailTexture(ResolveWatrDetailTextureIndex(waterSelection.Water));
        if (_water is not null) _water.DefaultWaterRequiresCellHasWater = false;
        _water?.LoadData(
            _cellGridLookup,
            worldspaceDefaultWaterHeight: null,
            _spatialIndex,
            appearance,
            ResolveWaterNormalIndices(appearance, starfieldApproximation));
        _water?.SetStarfieldApproximation(starfieldApproximation);
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
            _collisionDebug.DayNightStates = _dayNightStates;
        }
    }

    /// <summary>
    ///     Computes the vertical (Z) extent the cell-grid line cage should span for the loaded cells,
    ///     from the placed-object Z range (objects rest on the terrain, so this brackets the relief
    ///     and tall structures) plus a one-cell margin, with a minimum span so flat worldspaces still
    ///     show a tall-enough cage. Returns null when no finite placed-object Z exists (grid keeps its
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
                if (_data.Worldspaces[i].FormId == ws)
                {
                    targetIndex = i;
                    break;
                }
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
            if (c.FormId == interiorFormId)
            {
                interior = c;
                break;
            }
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
