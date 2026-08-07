using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>Atomic UI-thread snapshot of the rendered-models overlay lifecycle and last applied pixels.</summary>
internal readonly record struct TopDownProfilerSnapshot(
    bool ProviderReady,
    bool Enabled,
    bool OverlayPresent,
    bool CoversViewport,
    bool InFlight,
    bool Pending,
    bool Incomplete,
    bool Settled,
    long RequestsStarted,
    long RequestsCompleted,
    long LastCompletedRequestId,
    long OverlayRequestId,
    double LastDurationMs,
    bool RenderComplete,
    bool RenderFullySettled,
    ulong PixelHash,
    int PixelCount,
    int NonTransparentPixels,
    int NonZeroPixels,
    double MeanRed,
    double MeanGreen,
    double MeanBlue,
    double MeanLuma,
    int ReferenceInstances,
    int ReferenceDrawn,
    int SpeedTreeBranchInstances,
    int SpeedTreeLeafInstances,
    int SpeedTreeBillboardInstances);

/// <summary>Worldspace / layer / color-scheme picker handlers and the profiler-driving surface.</summary>
public sealed partial class WorldMapControl
{
    /// <summary>
    ///     The viewer's settings panel (Lighting / Layers / Visibility / Shading expanders),
    ///     constructed in the ctor and displayed by the host's right-panel Settings tab. Owned here
    ///     so standalone hosts (the profiler apps) keep working without a SingleFileTab.
    /// </summary>
    internal WorldMapSettingsPanel SettingsPanel { get; }

    // Accessor shims for the controls that moved from the old toolbar dropdowns into the settings
    // panel — Reset(), the marker/dangling plumbing, and the shading/canvas gates keep compiling
    // against the original x:Name identifiers.
    private LightingControlsPanel LightingPanel => SettingsPanel.Lighting;
    private CheckBox CellGridCheckBox => SettingsPanel.CellGridCheckBox;
    private CheckBox NavMeshCheckBox => SettingsPanel.NavMeshCheckBox;
    private CheckBox WaterCheckBox => SettingsPanel.WaterCheckBox;
    private CheckBox MapMarkersCheckBox => SettingsPanel.MapMarkersCheckBox;
    private CheckBox DisabledCheckBox => SettingsPanel.DisabledCheckBox;
    private CheckBox RenderedObjectsCheckBox => SettingsPanel.RenderedObjectsCheckBox;
    private ComboBox DanglingRefsComboBox => SettingsPanel.DanglingRefsComboBox;
    private TextBlock DanglingRefsLabel => SettingsPanel.DanglingRefsLabel;
    private CheckBox ShadeVertexColorsCheckBox => SettingsPanel.ShadeVertexColorsCheckBox;
    private CheckBox ShadeHillshadeCheckBox => SettingsPanel.ShadeHillshadeCheckBox;
    private Expander ShadingMenu => SettingsPanel.ShadingExpander;

    /// <summary>
    ///     Subscribes every settings-panel control to its handler. Runs in the ctor while
    ///     <c>_initializing</c> is still true, replacing the XAML event attributes the controls had
    ///     on the old toolbar (the panel XAML is purely declarative).
    /// </summary>
    private void WireSettingsPanel()
    {
        var p = SettingsPanel;

        p.Lighting.LightingToggled += LightingPanel_LightingToggled;
        p.Lighting.TimeChanged += LightingPanel_TimeChanged;

        Wire(p.CellGridCheckBox, CellGridCheckBox_Changed);
        Wire(p.NavMeshCheckBox, NavMeshCheckBox_Changed);
        Wire(p.WaterCheckBox, WaterCheckBox_Changed);
        Wire(p.MapMarkersCheckBox, MapMarkersCheckBox_Changed);
        Wire(p.DisabledCheckBox, DisabledCheckBox_Changed);
        Wire(p.RenderedObjectsCheckBox, RenderedObjectsCheckBox_Changed);
        Wire(p.ShadeVertexColorsCheckBox, ShadeVertexColorsCheckBox_Changed);
        Wire(p.ShadeHillshadeCheckBox, ShadeHillshadeCheckBox_Changed);

        p.DanglingRefsComboBox.SelectionChanged += DanglingRefsComboBox_SelectionChanged;

        static void Wire(CheckBox box, RoutedEventHandler handler)
        {
            box.Checked += handler;
            box.Unchecked += handler;
        }
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

        // Color scheme only applies to the heightmap layer (user-confirmed); the terrain-shading
        // menu only to the textured layer. They're mutually exclusive by layer.
        // Null-guard because the SelectionChanged event can fire during XAML load before
        // sibling fields are assigned (see winui3_selectionchanged_early_fire memory).
        if (ColorSchemeComboBox is not null)
        {
            ColorSchemeComboBox.Visibility = _currentLayer == WorldMapLayer.Heightmap
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (ShadingMenu is not null)
        {
            ShadingMenu.Visibility = _currentLayer == WorldMapLayer.TerrainTextures
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

    private void ShadeVertexColorsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _shadeVertexColors = ShadeVertexColorsCheckBox.IsChecked == true;
        RebuildForShadingChange();
    }

    private void ShadeHillshadeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _shadeHillshade = ShadeHillshadeCheckBox.IsChecked == true;
        RebuildForShadingChange();
    }

    /// <summary>
    ///     Re-bakes the terrain-textures layer after a shading-checkbox change. Shading is baked into the
    ///     terrain-texture cell bitmaps + aggregate, so a change must drop the texture caches and
    ///     re-stream. keepCurrentBitmap:false → CancelTerrainStream + bump _layerCellBitmapsCacheGen
    ///     (drops in-flight pre-change cells) + reset the aggregate.
    /// </summary>
    private void RebuildForShadingChange()
    {
        // dropWorldWater:false — shading is baked into the TERRAIN caches only. The world-water layer
        // is worldspace-keyed and built colour-complete regardless of the water toggle, so dropping it
        // here re-kicked a full-worldspace water pass alongside the terrain aggregate's own, for a
        // change with no water dependency. The ShadingMenu is visible only on Terrain Textures, so
        // this is the first thing a user clicks after switching to that layer.
        InvalidateWorldBitmap(keepCurrentBitmap: false, dropWorldWater: false);
        if (_cellHeightmapBitmap is not null && _state.SelectedCell is not null)
        {
            RebuildCellDetailBitmaps(_state.SelectedCell);
        }
        EnsureViewportTimerRunning();
        MapCanvas?.Invalidate();
    }

    /// <summary>Current terrain-texture shading selection (independent VCLR + hillshade) plus the
    /// hillshade light direction (from the lighting control; null = the renderer's NW default).</summary>
    private TerrainShadingOptions CurrentTerrainShading() => new(
        _shadeVertexColors,
        _shadeHillshade,
        _shadeHillshade ? CurrentHillshadeLightDir() : null,
        CurrentHillshadeZScale());

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
    internal float Profiler_CellWorldSize => _cellSize;

    /// <summary>
    ///     Overlay request counters, convergence flags, coverage, and deterministic metrics from the
    ///     last APPLIED readback. Read as one value on the UI thread so scenario assertions cannot mix
    ///     fields from opposite sides of an async request completion.
    /// </summary>
    internal TopDownProfilerSnapshot Profiler_TopDownSnapshot() => new(
        ProviderReady: _topDownProvider?.CanRenderTopDown == true,
        Enabled: _showRenderedObjects,
        OverlayPresent: _topDownOverlay is not null,
        CoversViewport: DoesTopDownOverlayCoverViewport(),
        InFlight: _topDownInFlight,
        Pending: _topDownRequestPending,
        Incomplete: _topDownIncomplete,
        Settled: IsTopDownOverlaySettled(),
        RequestsStarted: _topDownRequestsStarted,
        RequestsCompleted: _topDownRequestsCompleted,
        LastCompletedRequestId: _topDownLastCompletedRequestId,
        OverlayRequestId: _topDownOverlayRequestId,
        LastDurationMs: _topDownLastRequestDurationMs,
        RenderComplete: _topDownLastRenderComplete,
        RenderFullySettled: _topDownLastRenderFullySettled,
        PixelHash: _topDownLastPixelHash,
        PixelCount: _topDownLastPixelCount,
        NonTransparentPixels: _topDownLastNonTransparentPixels,
        NonZeroPixels: _topDownLastNonZeroPixels,
        MeanRed: _topDownLastMeanRed,
        MeanGreen: _topDownLastMeanGreen,
        MeanBlue: _topDownLastMeanBlue,
        MeanLuma: _topDownLastMeanLuma,
        ReferenceInstances: _topDownLastReferenceInstances,
        ReferenceDrawn: _topDownLastReferenceDrawn,
        SpeedTreeBranchInstances: _topDownLastSpeedTreeBranchInstances,
        SpeedTreeLeafInstances: _topDownLastSpeedTreeLeafInstances,
        SpeedTreeBillboardInstances: _topDownLastSpeedTreeBillboardInstances);

    /// <summary>Navigate through the real cell-detail path; false means the FormID is not loaded.</summary>
    internal bool Profiler_NavigateToCell(uint formId)
    {
        var cell = _state.FindCellByFormId(formId);
        if (cell is null) return false;
        NavigateToCell(cell);
        return true;
    }

    /// <summary>
    ///     Select the loaded interior with the most placed references and enter its real cell-detail
    ///     view. Returns its FormID, or null when the input contains no interiors.
    /// </summary>
    internal uint? Profiler_NavigateToDenseInterior()
    {
        var cell = _data?.InteriorCells
            .OrderByDescending(candidate => candidate.PlacedObjects.Count)
            .ThenBy(candidate => candidate.FormId)
            .FirstOrDefault();
        if (cell is null) return null;
        NavigateToCell(cell);
        return cell.FormId;
    }

    /// <summary>
    ///     Drive lighting through the shared UI control so its normal event handlers invalidate both
    ///     terrain hillshade and the rendered-models overlay exactly as an interactive change does.
    /// </summary>
    internal void Profiler_SetLighting(bool enabled, float gameHour)
    {
        LightingPanel.LightingEnabled = enabled;
        LightingPanel.GameHour = Math.Clamp(gameHour, 0f, 24f);
    }

    /// <summary>
    ///     Save the exact currently-applied, settled readback. Unlike the opt-in diagnostic dump in
    ///     the request path, this cannot capture an early streaming frame and is therefore suitable
    ///     as a deterministic visual-verification artifact.
    /// </summary>
    internal async Task<string> Profiler_SaveTopDownOverlayAsync(string path)
    {
        if (_topDownOverlay is null || !IsTopDownOverlaySettled())
        {
            throw new InvalidOperationException(
                "Cannot save the top-down profiler artifact before the overlay has settled.");
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await _topDownOverlay.SaveAsync(
            fullPath, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
        return fullPath;
    }

    /// <summary>
    ///     Find the loaded exterior cell containing the most placed .spt refs, select its owning
    ///     worldspace (including the unlinked-exterior tail), and center on one actual tree position.
    ///     Returns null only when no loaded exterior has a SpeedTree placement.
    /// </summary>
    internal (int WorldspaceIndex, uint CellFormId, uint ReferenceFormId, int SpeedTreeReferences)?
        Profiler_CenterOnSpeedTreeReference(float zoom)
    {
        if (_data is null) return null;

        (int WorldspaceIndex, CellRecord Cell, List<PlacedReference> References)?
            best = null;

        for (var worldspaceIndex = 0; worldspaceIndex < _data.Worldspaces.Count; worldspaceIndex++)
        {
            foreach (var cell in _data.Worldspaces[worldspaceIndex].Cells)
            {
                ConsiderCell(worldspaceIndex, cell);
            }
        }

        if (_data.UnlinkedExteriorCells.Count > 0)
        {
            var unlinkedIndex = _data.Worldspaces.Count;
            foreach (var cell in _data.UnlinkedExteriorCells)
            {
                ConsiderCell(unlinkedIndex, cell);
            }
        }

        if (best is not { } target) return null;
        if (WorldspaceComboBox.SelectedIndex != target.WorldspaceIndex)
        {
            WorldspaceComboBox.SelectedIndex = target.WorldspaceIndex;
        }
        EnsureOverviewMode();

        var reference = target.References
            .OrderBy(placed => placed.FormId)
            .ThenBy(placed => placed.X)
            .ThenBy(placed => placed.Y)
            .First();
        var center = new Vector2(reference.X, -reference.Y);
        var screenCenter = new Vector2(
            (float)MapCanvas.ActualWidth * 0.5f,
            (float)MapCanvas.ActualHeight * 0.5f);
        _zoom = Math.Clamp(zoom, 0.001f, 50f);
        _panOffset = screenCenter - center * _zoom;
        _panVelocity = Vector2.Zero;
        MapCanvas.Invalidate();
        return (target.WorldspaceIndex, target.Cell.FormId, reference.FormId, target.References.Count);

        void ConsiderCell(int worldspaceIndex, CellRecord cell)
        {
            if (cell.GridX is null || cell.GridY is null) return;

            var speedTrees = new List<PlacedReference>();
            foreach (var placed in cell.PlacedObjects)
            {
                if (placed.IsInitiallyDisabled || _data.XespDisabledRefs.Contains(placed.FormId))
                {
                    continue;
                }

                var modelPath = placed.ModelPath;
                if (string.IsNullOrWhiteSpace(modelPath)
                    && _data.ModelPathIndex.TryGetValue(placed.BaseFormId, out var indexedPath))
                {
                    modelPath = indexedPath;
                }

                if (!string.IsNullOrWhiteSpace(modelPath) && SpeedTreeModelPath.IsSpt(modelPath))
                {
                    speedTrees.Add(placed);
                }
            }

            if (speedTrees.Count == 0) return;
            if (best is null
                || speedTrees.Count > best.Value.References.Count
                || (speedTrees.Count == best.Value.References.Count
                    && worldspaceIndex < best.Value.WorldspaceIndex)
                || (speedTrees.Count == best.Value.References.Count
                    && worldspaceIndex == best.Value.WorldspaceIndex
                    && cell.FormId < best.Value.Cell.FormId))
            {
                best = (worldspaceIndex, cell, speedTrees);
            }
        }
    }

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
