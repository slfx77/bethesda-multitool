using System.Linq;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>
///     The right-panel "Export" tab (Projection / Include / Output), replacing the old modal
///     <c>MapExport3DDialog</c>. The viewer owns <see cref="ExportPanel" /> and wires it here; the
///     include toggles are an INDEPENDENT copy (seeded once from the live view, then separate), the
///     "Export…" button drives the same offscreen pipeline (<see cref="RunProjectionExportAsync" />)
///     writing to the folder/name fields (no save picker), and the projection/scale choices feed both
///     the live output-size readout and the framing overlay (<c>WorldView3DControl.Frame.cs</c>).
/// </summary>
public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     The viewer's export panel, constructed in the ctor and displayed by the host's right-panel
    ///     Export tab. Owned here so the run action reaches the live renderer.
    /// </summary>
    internal WorldView3DExportPanel ExportPanel { get; }

    // Projection mode + per-mode direction labels (relocated from MapExport3DDialog).
    private static readonly ProjectionMode[] s_exportModes =
        [ProjectionMode.Orthographic, ProjectionMode.Isometric, ProjectionMode.Trimetric];
    private static readonly string[] s_exportOrthoDirections =
        ["North at top", "East at top", "South at top", "West at top"];
    private static readonly string[] s_exportIsoDirections =
        ["From the NE", "From the SE", "From the SW", "From the NW"];

    private float _exportScale = 1f;
    private bool _exportBoundsValid;
    private bool _exportSeeded;
    private float _exportMinX, _exportMaxX, _exportMinY, _exportMaxY, _exportMinZ, _exportMaxZ;
    private string? _exportLastFolder;
    // True while the Export tab is the selected right-panel tab (set by the host). Combined with the
    // panel's "Show framing" toggle to decide whether Frame.cs draws the framing overlay.
    private bool _exportTabSelected;
    private bool _exportFramingDirty = true;
    private Vector3[]? _exportFramingLines;

    // Amber — distinct from the collision cage (green) and the navmesh overlay.
    private static readonly Vector4 ExportFramingColor = new(1.0f, 0.75f, 0.1f, 0.9f);

    private ProjectionMode SelectedExportMode =>
        ExportPanel.ProjectionComboBox.SelectedIndex >= 0
        && ExportPanel.ProjectionComboBox.SelectedIndex < s_exportModes.Length
            ? s_exportModes[ExportPanel.ProjectionComboBox.SelectedIndex]
            : ProjectionMode.Orthographic;

    private int SelectedExportDirectionQuadrant => Math.Max(0, ExportPanel.DirectionComboBox.SelectedIndex);

    /// <summary>Whether the framing overlay should draw this frame (tab selected AND toggle on).</summary>
    private bool ExportFramingVisible =>
        _exportTabSelected && _exportBoundsValid && ExportPanel.ShowFramingToggle.IsOn;

    /// <summary>Subscribes the export panel's controls, mirroring <c>WireSettingsPanel</c>. The 13
    /// include checkboxes are intentionally NOT wired — they are an independent export-only copy read
    /// at export time, not live-view drivers.</summary>
    private void WireExportPanel()
    {
        var p = ExportPanel;

        p.ProjectionComboBox.Items.Add("Orthographic (top-down)");
        p.ProjectionComboBox.Items.Add("Isometric");
        p.ProjectionComboBox.Items.Add("Trimetric");
        p.ProjectionComboBox.SelectionChanged += ExportProjection_SelectionChanged;
        p.DirectionComboBox.SelectionChanged += ExportDirection_SelectionChanged;

        p.TiledCheckBox.Checked += ExportTiled_Changed;
        p.TiledCheckBox.Unchecked += ExportTiled_Changed;

        p.Scale025Button.Click += ExportScalePreset_Click;
        p.Scale05Button.Click += ExportScalePreset_Click;
        p.Scale1Button.Click += ExportScalePreset_Click;
        p.Scale2Button.Click += ExportScalePreset_Click;
        p.Scale4Button.Click += ExportScalePreset_Click;

        p.BrowseFolderButton.Click += ExportBrowseFolder_Click;
        p.ExportRunButton.Click += ExportRun_Click;
        p.ShowFramingToggle.Toggled += ExportShowFraming_Toggled;
        p.FolderTextBox.TextChanged += ExportPath_TextChanged;
        p.FileNameTextBox.TextChanged += ExportPath_TextChanged;

        // Fires SelectionChanged → PopulateExportDirections + UpdateExportOutputSize (bounds guard).
        p.ProjectionComboBox.SelectedIndex = 0;
    }

    private void ExportProjection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateExportDirections();
        UpdateExportOutputSize();
        _exportFramingDirty = true;
    }

    private void ExportDirection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateExportOutputSize();
        _exportFramingDirty = true;
    }

    private void ExportTiled_Changed(object sender, RoutedEventArgs e) => UpdateExportOutputSize();

    private void ExportScalePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }
            && double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var scale))
        {
            _exportScale = (float)scale;
            UpdateExportOutputSize();
        }
    }

    private void ExportShowFraming_Toggled(object sender, RoutedEventArgs e) => _exportFramingDirty = true;

    private void ExportPath_TextChanged(object sender, TextChangedEventArgs e) => UpdateExportRunEnabled();

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

    private void PopulateExportDirections()
    {
        // SelectionChanged can fire before DirectionComboBox is realized during XAML load.
        if (ExportPanel.DirectionComboBox is null) return;
        var labels = SelectedExportMode == ProjectionMode.Orthographic
            ? s_exportOrthoDirections
            : s_exportIsoDirections;
        ExportPanel.DirectionComboBox.Items.Clear();
        foreach (var label in labels) ExportPanel.DirectionComboBox.Items.Add(label);
        ExportPanel.DirectionComboBox.SelectedIndex = 0;
    }

    private void UpdateExportOutputSize()
    {
        if (ExportPanel.OutputSizeText is null || ExportPanel.DirectionComboBox is null) return;
        if (!_exportBoundsValid)
        {
            ExportPanel.OutputSizeText.Text = "Open an exterior worldspace to export.";
            return;
        }

        var tiled = ExportPanel.TiledCheckBox.IsChecked == true;
        var plan = MapExport3DPlanner.Plan(
            SelectedExportMode, SelectedExportDirectionQuadrant,
            _exportMinX, _exportMaxX, _exportMinY, _exportMaxY, _exportMinZ, _exportMaxZ,
            _exportScale, tiled, Export3DMaxTileDimension, Export3DMaxImageDimension);

        var pxPerCell = (int)Math.Round(MapExport3DPlanner.BaseScalePxPerUnit * _exportScale * 4096f);
        var totalTiles = (long)plan.Cols * plan.Rows;
        string tileNote;
        if (tiled && totalTiles > 1)
        {
            tileNote = $" — {plan.Cols}×{plan.Rows} tiles ({totalTiles} PNGs + manifest)";
        }
        else if (totalTiles > 1)
        {
            tileNote = $" — single image (stitched from {plan.Cols}×{plan.Rows} tiles)";
        }
        else
        {
            tileNote = " — single image";
        }

        ExportPanel.OutputSizeText.Text =
            $"Output: {plan.ImageWidth} × {plan.ImageHeight} px (~{pxPerCell} px/cell at {_exportScale:0.##}×){tileNote}";
    }

    private void UpdateExportRunEnabled()
    {
        if (ExportPanel.ExportRunButton is null) return;
        ExportPanel.ExportRunButton.IsEnabled =
            _exportBoundsValid
            && _selectedInterior is null
            && !string.IsNullOrWhiteSpace(ExportPanel.FolderTextBox.Text)
            && !string.IsNullOrWhiteSpace(ExportPanel.FileNameTextBox.Text);
    }

    /// <summary>Host hook: the Export tab became (in)active. Gates the framing overlay.</summary>
    internal void SetExportFramingActive(bool active)
    {
        _exportTabSelected = active;
        _exportFramingDirty = true;
    }

    /// <summary>Rebuilds the framing edge list when the projection/bounds changed. Called from the
    /// live frame only while the overlay is visible, so an off-screen tab costs nothing.</summary>
    private void EnsureExportFramingLines()
    {
        if (!_exportFramingDirty) return;
        _exportFramingDirty = false;
        _exportFramingLines = _exportBoundsValid ? BuildExportFramingLines() : null;
    }

    /// <summary>The export's captured world AABB (12 edges) + a view-direction gizmo from the box
    /// center toward the ortho eye — a flat list of edge endpoint PAIRS in absolute world coords.</summary>
    private Vector3[] BuildExportFramingLines()
    {
        float x0 = _exportMinX, x1 = _exportMaxX;
        float y0 = _exportMinY, y1 = _exportMaxY;
        float z0 = _exportMinZ, z1 = _exportMaxZ;
        var corners = new[]
        {
            new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
            new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1),
        };
        var lines = new List<Vector3>(26);
        void Edge(int a, int b) { lines.Add(corners[a]); lines.Add(corners[b]); }
        Edge(0, 1); Edge(1, 2); Edge(2, 3); Edge(3, 0); // floor
        Edge(4, 5); Edge(5, 6); Edge(6, 7); Edge(7, 4); // ceiling
        Edge(0, 4); Edge(1, 5); Edge(2, 6); Edge(3, 7); // verticals

        // View-direction gizmo: box center → toward the ortho eye (shows the projection angle live).
        var plan = MapExport3DPlanner.Plan(
            SelectedExportMode, SelectedExportDirectionQuadrant,
            x0, x1, y0, y1, z0, z1, _exportScale,
            ExportPanel.TiledCheckBox.IsChecked == true, Export3DMaxTileDimension, Export3DMaxImageDimension);
        var center = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
        var eye = OrthoViewProjBuilder.EyePosition(plan.Focus, plan.Azimuth, plan.Elevation);
        var toEye = eye - center;
        if (toEye.LengthSquared() > 1e-3f)
        {
            var extent = new Vector3(x1 - x0, y1 - y0, z1 - z0).Length();
            lines.Add(center);
            lines.Add(center + Vector3.Normalize(toEye) * (0.25f * extent));
        }
        return lines.ToArray();
    }

    /// <summary>
    ///     Recomputes the export's captured world bounds from the active exterior worldspace and
    ///     refreshes the output-size readout, the Export-enabled state, and the folder/name defaults.
    ///     Must be called on data load and on every worldspace/interior change — a persistent tab can't
    ///     capture bounds once the way the old dialog did.
    /// </summary>
    internal void RefreshExportBounds()
    {
        if (ExportPanel is null) return;

        _exportBoundsValid = false;
        if (_data is not null && _selectedInterior is null)
        {
            var cells = GetSelectedWorldspaceCells(_data).Cells
                .Where(c => c.GridX is int && c.GridY is int).ToList();
            if (cells.Count > 0)
            {
                var minGx = cells.Min(c => c.GridX!.Value);
                var maxGx = cells.Max(c => c.GridX!.Value);
                var minGy = cells.Min(c => c.GridY!.Value);
                var maxGy = cells.Max(c => c.GridY!.Value);
                _exportMinX = minGx * _cellSize;
                _exportMaxX = (maxGx + 1) * _cellSize;
                _exportMinY = minGy * _cellSize;
                _exportMaxY = (maxGy + 1) * _cellSize;
                (_exportMinZ, _exportMaxZ) =
                    ComputeGridZExtent(cells) ?? (Export3DFallbackMinZ, Export3DFallbackMaxZ);
                _exportBoundsValid = true;
            }
        }

        if (_exportBoundsValid && !_exportSeeded)
        {
            _exportSeeded = true;
            ExportPanel.TerrainCheckBox.IsChecked = _showTerrain;
            ExportPanel.MeshesCheckBox.IsChecked = _showReferences;
            ExportPanel.WaterCheckBox.IsChecked = _showWater;
            ExportPanel.TreesCheckBox.IsChecked = !_hiddenCategories.Contains(PlacedObjectCategory.Tree);
            ExportPanel.GrassCheckBox.IsChecked = _showGrass;
            ExportPanel.ActivatorsCheckBox.IsChecked =
                !_hiddenCategories.Contains(PlacedObjectCategory.Activator);
            ExportPanel.MarkersCheckBox.IsChecked = _showMarkers;
            ExportPanel.DisabledCheckBox.IsChecked = _showDisabled;
            ExportPanel.NavMeshCheckBox.IsChecked = _showNavMesh;
            ExportPanel.CollisionCheckBox.IsChecked = _showCollision;
            ExportPanel.GridCheckBox.IsChecked = _showWireframe;
            ExportPanel.LightingCheckBox.IsChecked = _showLighting;
            ExportPanel.FogCheckBox.IsChecked = _showFog;
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
                ExportPanel.FileNameTextBox.Text = $"{ExportWorldspaceName()}_3d";
            }
        }

        UpdateExportOutputSize();
        UpdateExportRunEnabled();
        _exportFramingDirty = true;
    }

    private string ExportWorldspaceName()
    {
        var wsIdx = WorldspaceComboBox.SelectedIndex;
        return _data?.Worldspaces is { } wss && wsIdx >= 0 && wsIdx < wss.Count
            ? wss[wsIdx].EditorId ?? wss[wsIdx].FullName ?? "worldspace"
            : "worldspace";
    }

    /// <summary>Runs the export: reads the panel's current options, plans, and drives the offscreen
    /// pipeline to <c>{folder}\{name}.png</c> (tiled runs add <c>_r{j}_c{i}</c> + a manifest). This is
    /// the relocated body of the former <c>ExportButton_Click</c>, sourced from the panel + folder/name
    /// fields instead of a modal dialog + save picker.</summary>
    private async void ExportRun_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null) return;
        if (!CanRenderProjectionExport)
        {
            ShowStatus("3D export isn't ready yet — the renderer is still initializing.", autoDismiss: true);
            return;
        }
        if (_selectedInterior is not null)
        {
            ShowStatus("3D export covers exterior worldspaces. Switch to an exterior view first.", autoDismiss: true);
            return;
        }

        var cells = GetSelectedWorldspaceCells(_data).Cells
            .Where(c => c.GridX is int && c.GridY is int).ToList();
        if (cells.Count == 0)
        {
            ShowStatus("No exterior cells in the active worldspace to export.", autoDismiss: true);
            return;
        }

        var folder = ExportPanel.FolderTextBox.Text?.Trim();
        var name = ExportPanel.FileNameTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name)) return;
        if (!Directory.Exists(folder))
        {
            ShowStatus($"Export folder does not exist:\n{folder}", autoDismiss: true);
            return;
        }
        _exportLastFolder = folder;
        var basePath = Path.Combine(folder, name + ".png");

        var minGx = cells.Min(c => c.GridX!.Value);
        var maxGx = cells.Max(c => c.GridX!.Value);
        var minGy = cells.Min(c => c.GridY!.Value);
        var maxGy = cells.Max(c => c.GridY!.Value);
        var worldMinX = minGx * _cellSize;
        var worldMaxX = (maxGx + 1) * _cellSize;
        var worldMinY = minGy * _cellSize;
        var worldMaxY = (maxGy + 1) * _cellSize;
        var (worldMinZ, worldMaxZ) = ComputeGridZExtent(cells) ?? (Export3DFallbackMinZ, Export3DFallbackMaxZ);

        var tiledRequested = ExportPanel.TiledCheckBox.IsChecked == true;

        var hidden = new HashSet<PlacedObjectCategory>(_hiddenCategories);
        if (ExportPanel.ActivatorsCheckBox.IsChecked == true) hidden.Remove(PlacedObjectCategory.Activator);
        else hidden.Add(PlacedObjectCategory.Activator);
        if (ExportPanel.TreesCheckBox.IsChecked == true) hidden.Remove(PlacedObjectCategory.Tree);
        else hidden.Add(PlacedObjectCategory.Tree);

        var opts = new Export3DOptions(
            ShowTerrain: ExportPanel.TerrainCheckBox.IsChecked == true,
            ShowReferences: ExportPanel.MeshesCheckBox.IsChecked == true,
            ShowGrass: ExportPanel.GrassCheckBox.IsChecked == true,
            ShowWater: ExportPanel.WaterCheckBox.IsChecked == true,
            ShowNavMesh: ExportPanel.NavMeshCheckBox.IsChecked == true,
            ShowCollision: ExportPanel.CollisionCheckBox.IsChecked == true,
            ShowGrid: ExportPanel.GridCheckBox.IsChecked == true,
            ShowDisabled: ExportPanel.DisabledCheckBox.IsChecked == true,
            ShowMarkers: ExportPanel.MarkersCheckBox.IsChecked == true,
            EnableLighting: ExportPanel.LightingCheckBox.IsChecked == true,
            EnableFog: ExportPanel.FogCheckBox.IsChecked == true,
            HiddenCategories: hidden);

        var plan = MapExport3DPlanner.Plan(
            SelectedExportMode, SelectedExportDirectionQuadrant,
            worldMinX, worldMaxX, worldMinY, worldMaxY, worldMinZ, worldMaxZ,
            _exportScale, tiledRequested, Export3DMaxTileDimension, Export3DMaxImageDimension);

        // Per-tile PNGs + manifest only when the user asked for tiles AND there is more than one; a
        // one-tile "tiled" run stays a single PNG. A non-tiled grid > 1 tile is stitched into one PNG.
        var tiledOutput = tiledRequested && ((long)plan.Cols * plan.Rows) > 1;

        // World-space centers of the worldspace's ACTUAL cells — lets the tile loop skip a provably
        // empty tile before spending a full GPU render + readback (see RunProjectionExportAsync).
        var contentCellCenters = cells
            .Select(c => ((c.GridX!.Value + 0.5f) * _cellSize, (c.GridY!.Value + 0.5f) * _cellSize))
            .ToList();

        var progress = new ExportProgressController(XamlRoot);
        _ = progress.ShowAsync();
        // Pause the live render loop: the export records on the shared command recorder, which would
        // race the per-frame loop. Drain the GPU so no in-flight frame collides with the first tile.
        DetachRenderLoop();
        try
        {
            _commandRecorder12?.WaitForGpuIdle();
            await RunProjectionExportAsync(
                plan, opts, basePath, worldMaxZ - worldMinZ, tiledOutput, contentCellCenters,
                progress, progress.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            // User canceled — keep whatever tiles were already written.
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Logger.Instance.Warn("3D export failed: {0}", ex.ToString());
        }
        finally
        {
            progress.Complete();
            AttachRenderLoop();
        }
    }
}
