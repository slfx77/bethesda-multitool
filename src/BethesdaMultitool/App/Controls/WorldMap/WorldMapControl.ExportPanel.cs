using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using BethesdaMultitool.Core.WorldData;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>
///     Options collected by the 2D map's export tab. Formerly the return value of the modal
///     <c>MapExportDialog</c>; the panel is persistent, so this is now just the immutable snapshot the
///     run takes of the panel's controls.
/// </summary>
internal sealed record MapExportRequest(
    WorldMapLayer Layer,
    int LongEdgePx,
    bool IncludeMarkers,
    bool IncludeNavMesh,
    bool IncludeWater,
    bool IncludeGrid,
    bool Tiled,
    bool IncludeRenderedMeshes);

/// <summary>
///     The right-panel "Export" tab for the 2D map (Layer / Include / Output), replacing the old modal
///     <c>MapExportDialog</c> + toolbar button and mirroring <c>WorldView3DControl.ExportPanel.cs</c>.
///     The viewer owns <see cref="ExportPanel" /> and wires it here; the include toggles are an
///     INDEPENDENT copy (seeded once from the live map, then separate), and the "Export…" button drives
///     the same tile pipeline (<see cref="RunExportAsync" />) writing to the folder/name fields instead
///     of a save picker.
///     <para>
///         Resolution is expressed both as a px long-edge value and as a "px per cell" scale relative
///         to the selected layer's NATIVE source resolution — 33 px/cell for the heightmap family, 132
///         (up to 528) for the texture layer — so switching layers updates the meaningful scale.
///     </para>
/// </summary>
public sealed partial class WorldMapControl
{
    /// <summary>Layers offered for export, in display order (matches the viewer's layer dropdown).</summary>
    private static readonly WorldMapLayer[] s_exportLayers =
    [
        WorldMapLayer.Heightmap,
        WorldMapLayer.VertexColors,
        WorldMapLayer.TerrainRegions,
        WorldMapLayer.TerrainTextures,
        WorldMapLayer.Slope
    ];

    private bool _exportBoundsValid;

    private WorldMapExportGridBounds _exportGrid;
    private string? _exportLastFolder;
    private bool _exportRunning;

    private bool _exportSeeded;

    // Guards the long-edge NumberBox against the readout refresh that the scale presets trigger.
    private bool _suspendExportLongEdge;

    /// <summary>
    ///     The viewer's export panel, constructed in the ctor and displayed by the host's right-panel
    ///     Export tab. Owned here so the run action reaches the live canvas + layer caches.
    /// </summary>
    internal WorldMapExportPanel ExportPanel { get; }

    private WorldMapLayer SelectedExportLayer =>
        ExportPanel.LayerComboBox.SelectedIndex >= 0
        && ExportPanel.LayerComboBox.SelectedIndex < s_exportLayers.Length
            ? s_exportLayers[ExportPanel.LayerComboBox.SelectedIndex]
            : WorldMapLayer.Heightmap;

    /// <summary>Native source px/cell for a layer: 132 for the texture layer, 33 otherwise.</summary>
    private static int NativeExportPxPerCell(WorldMapLayer layer) =>
        layer == WorldMapLayer.TerrainTextures
            ? WorldMapLayerRenderer.TexturePixelsPerCell
            : WorldMapLayerRenderer.HeightmapPixelsPerCell;

    /// <summary>Ceiling on real source detail: 528 for texture, 4× native (132) for others.</summary>
    private static int MaxExportPxPerCell(WorldMapLayer layer) =>
        layer == WorldMapLayer.TerrainTextures
            ? WorldMapLayerRenderer.MaxTexturePixelsPerCell
            : WorldMapLayerRenderer.HeightmapPixelsPerCell * 4;

    /// <summary>
    ///     Subscribes the export panel's controls, mirroring <see cref="WireSettingsPanel" />. The five
    ///     include checkboxes are intentionally NOT wired to live-view handlers — they are an
    ///     export-only copy read at export time.
    /// </summary>
    private void WireExportPanel()
    {
        var p = ExportPanel;

        foreach (var layer in s_exportLayers)
        {
            p.LayerComboBox.Items.Add(layer.DisplayName());
        }

        p.LayerComboBox.SelectionChanged += ExportLayer_SelectionChanged;

        p.TiledCheckBox.Checked += ExportTiled_Changed;
        p.TiledCheckBox.Unchecked += ExportTiled_Changed;

        p.Scale025Button.Click += ExportScalePreset_Click;
        p.Scale05Button.Click += ExportScalePreset_Click;
        p.Scale1Button.Click += ExportScalePreset_Click;
        p.Scale2Button.Click += ExportScalePreset_Click;
        p.Scale4Button.Click += ExportScalePreset_Click;

        p.LongEdgeNumberBox.ValueChanged += ExportLongEdge_ValueChanged;
        p.BrowseFolderButton.Click += ExportBrowseFolder_Click;
        p.ExportRunButton.Click += ExportRun_Click;
        p.FolderTextBox.TextChanged += ExportPath_TextChanged;
        p.FileNameTextBox.TextChanged += ExportPath_TextChanged;

        _suspendExportLongEdge = true;
        p.LongEdgeNumberBox.Value = ExportLongEdge;
        _suspendExportLongEdge = false;
        // Fires SelectionChanged → UpdateExportOutputSize (which no-ops until bounds are valid).
        p.LayerComboBox.SelectedIndex = 0;
    }

    private void ExportLayer_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateExportOutputSize();

    private void ExportTiled_Changed(object sender, RoutedEventArgs e) => UpdateExportOutputSize();

    private void ExportLongEdge_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suspendExportLongEdge) return;
        UpdateExportOutputSize();
    }

    private void ExportPath_TextChanged(object sender, TextChangedEventArgs e) => UpdateExportRunEnabled();

    private void ExportScalePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var scale)) return;
        if (!_exportBoundsValid) return;

        var layer = SelectedExportLayer;
        var maxCells = _exportGrid.MaxGridDimension;
        var pxPerCell = Math.Clamp(
            (int)Math.Round(NativeExportPxPerCell(layer) * scale), 1, MaxExportPxPerCell(layer));
        var longEdge = Math.Clamp(
            (long)pxPerCell * maxCells,
            32L,
            (long)ExportPanel.LongEdgeNumberBox.Maximum);

        _suspendExportLongEdge = true;
        ExportPanel.LongEdgeNumberBox.Value = longEdge;
        _suspendExportLongEdge = false;
        UpdateExportOutputSize();
    }

    private async void ExportBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*"); // WinRT requires at least one filter entry.
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ExportPanel.FolderTextBox.Text = folder.Path;
            UpdateExportRunEnabled();
        }
    }

    /// <summary>
    ///     Recomputes the export's cell rectangle from the active worldspace and refreshes the
    ///     output-size readout, the Export-enabled state, and the folder/name defaults. Must be called
    ///     on data load and on every worldspace / browser-mode change — a persistent tab can't capture
    ///     bounds once the way the old dialog did.
    /// </summary>
    internal void RefreshExportBounds()
    {
        if (ExportPanel is null) return;

        _exportBoundsValid = false;
        var gridded = GetActiveCells().Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
        if (gridded.Count > 0)
        {
            if (WorldMapExportPlan.TryCreateGridBounds(
                    gridded.Min(c => c.GridX!.Value), gridded.Max(c => c.GridX!.Value),
                    gridded.Min(c => c.GridY!.Value), gridded.Max(c => c.GridY!.Value),
                    out var bounds, out var boundsError))
            {
                _exportGrid = bounds;
                _exportBoundsValid = true;
            }
            else
            {
                Logger.Instance.Warn("Map export rejected invalid grid bounds: {0}", boundsError);
            }
        }

        // The rendered-meshes overlay needs the 3D top-down provider; it comes up after the 3D viewer
        // initializes, so re-evaluate on every refresh rather than once at construction.
        var canRenderMeshes = _topDownProvider?.CanRenderTopDown == true;
        ExportPanel.RenderedMeshesCheckBox.IsEnabled = canRenderMeshes;
        if (!canRenderMeshes) ExportPanel.RenderedMeshesCheckBox.IsChecked = false;

        if (_exportBoundsValid && !_exportSeeded)
        {
            _exportSeeded = true;
            ExportPanel.LayerComboBox.SelectedIndex =
                Math.Max(0, Array.IndexOf(s_exportLayers, _currentLayer));
            ExportPanel.MarkersCheckBox.IsChecked =
                !_hiddenCategories.Contains(PlacedObjectCategory.MapMarker);
            ExportPanel.NavMeshCheckBox.IsChecked = _showNavMesh;
            ExportPanel.WaterCheckBox.IsChecked = _showWater;
            ExportPanel.GridCheckBox.IsChecked = _showCellGrid;
        }

        if (_exportBoundsValid)
        {
            if (string.IsNullOrWhiteSpace(ExportPanel.FolderTextBox.Text))
            {
                ExportPanel.FolderTextBox.Text =
                    _exportLastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            if (string.IsNullOrWhiteSpace(ExportPanel.FileNameTextBox.Text))
            {
                ExportPanel.FileNameTextBox.Text = $"{ExportWorldspaceName()}_map";
            }
        }

        UpdateExportOutputSize();
        UpdateExportRunEnabled();
    }

    private string ExportWorldspaceName() =>
        _state.SelectedWorldspace?.EditorId ?? _state.SelectedWorldspace?.FullName ?? "worldspace";

    private void UpdateExportRunEnabled()
    {
        if (ExportPanel.ExportRunButton is null) return;
        ExportPanel.ExportRunButton.IsEnabled =
            _exportBoundsValid
            && !_exportRunning
            && !string.IsNullOrWhiteSpace(ExportPanel.FolderTextBox.Text)
            && !string.IsNullOrWhiteSpace(ExportPanel.FileNameTextBox.Text);
    }

    private void UpdateExportOutputSize()
    {
        // SelectionChanged fires while the panel's document is still being parsed; bail until ready.
        if (ExportPanel?.OutputSizeText is null || ExportPanel.LongEdgeNumberBox is null) return;

        if (!_exportBoundsValid)
        {
            ExportPanel.WorldSizeText.Text = "";
            ExportPanel.OutputSizeText.Text = "Select a worldspace to export.";
            return;
        }

        var cellsWide = _exportGrid.CellsWide;
        var cellsTall = _exportGrid.CellsTall;
        ExportPanel.WorldSizeText.Text = $"Worldspace: {cellsWide} × {cellsTall} cells";

        var longEdge = (int)Math.Round(ExportPanel.LongEdgeNumberBox.Value);
        if (longEdge < 32 || double.IsNaN(ExportPanel.LongEdgeNumberBox.Value))
        {
            ExportPanel.OutputSizeText.Text = "";
            return;
        }

        var layer = SelectedExportLayer;
        var native = NativeExportPxPerCell(layer);
        var maxCells = _exportGrid.MaxGridDimension;
        var tiled = ExportPanel.TiledCheckBox.IsChecked == true;

        // All the sizing arithmetic (including the long-typed products that keep a large
        // worldspace from overflowing) lives in Core so it is unit-testable.
        var size = MapExportSizeEstimate.Plan(
            cellsWide, cellsTall, maxCells, longEdge,
            WorldMapExporter.ExportMaxTileDimension, tiled);

        var scaleX = (double)size.EffectivePxPerCell / native;
        string note;
        if (size.Capped)
        {
            note = $"  — capped to {WorldMapExporter.ExportMaxTileDimension} px (enable Tiled for full detail)";
        }
        else if (size.Columns > 1 || size.Rows > 1)
        {
            note = $"  — tiled into {size.Columns}×{size.Rows} PNGs";
        }
        else
        {
            note = "";
        }

        ExportPanel.OutputSizeText.Text =
            $"Output: {size.ImageWidth} × {size.ImageHeight} px "
            + $"({size.EffectivePxPerCell} px/cell, {scaleX:0.##}× of {native} native){note}";
    }

    /// <summary>
    ///     Runs the export: validates the active worldspace's grid, plans the tiles, and drives the same
    ///     tile pipeline (<see cref="RunExportAsync" />) to <c>{folder}\{name}.png</c> (tiled runs add
    ///     <c>_r{row}_c{col}</c> + a manifest). This is the relocated body of the former
    ///     <c>ExportButton_Click</c>, sourced from the panel + folder/name fields instead of a modal
    ///     dialog + save picker.
    /// </summary>
    private async void ExportRun_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null || _exportRunning) return;

        var activeCells = GetActiveCells();
        var cellsWithGrid = activeCells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
        if (cellsWithGrid.Count == 0) return;

        var minGx = cellsWithGrid.Min(c => c.GridX!.Value);
        var maxGx = cellsWithGrid.Max(c => c.GridX!.Value);
        var minGy = cellsWithGrid.Min(c => c.GridY!.Value);
        var maxGy = cellsWithGrid.Max(c => c.GridY!.Value);
        if (!WorldMapExportPlan.TryCreateGridBounds(
                minGx, maxGx, minGy, maxGy, out var gridBounds, out var boundsError))
        {
            Logger.Instance.Warn("Map export rejected invalid grid bounds: {0}", boundsError);
            return;
        }

        var req = BuildExportRequest();

        // Resolve output px/cell. Don't upscale beyond the layer's real source detail (132/528 for
        // texture, 132 for the 33-native heightmap-family layers).
        var maxGridDim = gridBounds.MaxGridDimension;
        var maxSourcePpc = MaxExportPxPerCell(req.Layer);
        var ppc = Math.Clamp(req.LongEdgePx / maxGridDim, 1, maxSourcePpc);

        int cellsPerTile;
        if (req.Tiled)
        {
            // Split into tiles each within the GPU max-texture bound.
            cellsPerTile = Math.Max(1, WorldMapExporter.ExportMaxTileDimension / ppc);
        }
        else
        {
            // Single image: clamp px/cell so the whole worldspace fits one texture.
            ppc = Math.Min(ppc, Math.Max(1, WorldMapExporter.ExportMaxTileDimension / maxGridDim));
            cellsPerTile = maxGridDim;
        }

        if (!WorldMapExportPlan.TryCreate(
                gridBounds,
                ppc,
                cellsPerTile,
                WorldMapExporter.ExportMaxTileDimension,
                _cellSize,
                out var plan,
                out var planError) || plan is null)
        {
            Logger.Instance.Warn("Map export rejected an unrepresentable plan: {0}", planError);
            return;
        }

        // Output path comes from the panel's folder + name fields. The legacy save picker is gone: it
        // returned a newly-created EMPTY StorageFile, truncating a same-name target before the atomic
        // writer could protect it. Composing the path ourselves leaves existing PNG bytes intact until
        // AtomicFileWriter commits.
        var folder = ExportPanel.FolderTextBox.Text?.Trim();
        var name = ExportPanel.FileNameTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name)) return;
        if (!Directory.Exists(folder))
        {
            ShowLayerBuildStatus($"Export folder does not exist: {folder}", busy: false);
            return;
        }

        _exportLastFolder = folder;
        var basePath = Path.Combine(folder, name + ".png");

        EnsureMarkerIconSet(MapCanvas);

        // Apply markers preference: hidden if the user unchecked Map markers in the panel.
        var exportHiddenCategories = new HashSet<PlacedObjectCategory>(_hiddenCategories);
        if (!req.IncludeMarkers)
        {
            exportHiddenCategories.Add(PlacedObjectCategory.MapMarker);
        }
        else
        {
            exportHiddenCategories.Remove(PlacedObjectCategory.MapMarker);
        }

        var progressDialog = new ExportProgressController(XamlRoot);
        _ = progressDialog.ShowAsync();
        _exportRunning = true;
        UpdateExportRunEnabled();
        try
        {
            await RunExportAsync(
                req,
                activeCells,
                basePath,
                plan,
                exportHiddenCategories,
                progressDialog,
                progressDialog.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            // User canceled — leave whatever tiles were already written.
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("Map export failed: {0}", ex.ToString());
        }
        finally
        {
            progressDialog.Complete();
            _exportRunning = false;
            UpdateExportRunEnabled();
        }
    }

    /// <summary>Snapshot of the panel's current options, taken once at the start of a run.</summary>
    private MapExportRequest BuildExportRequest() =>
        new(
            Layer: SelectedExportLayer,
            LongEdgePx: (int)Math.Round(ExportPanel.LongEdgeNumberBox.Value),
            IncludeMarkers: ExportPanel.MarkersCheckBox.IsChecked == true,
            IncludeNavMesh: ExportPanel.NavMeshCheckBox.IsChecked == true,
            IncludeWater: ExportPanel.WaterCheckBox.IsChecked == true,
            IncludeGrid: ExportPanel.GridCheckBox.IsChecked == true,
            Tiled: ExportPanel.TiledCheckBox.IsChecked == true,
            IncludeRenderedMeshes: ExportPanel.RenderedMeshesCheckBox.IsChecked == true);
}
