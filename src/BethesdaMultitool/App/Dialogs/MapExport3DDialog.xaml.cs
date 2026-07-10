using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>Caller-facing values from <see cref="MapExport3DDialog" />; null when the user cancels.</summary>
internal sealed record MapExport3DRequest(
    ProjectionMode Mode,
    int DirectionQuadrant,
    float Scale,
    bool Tiled,
    bool ShowTerrain,
    bool ShowMeshes,
    bool ShowWater,
    bool ShowActivators,
    bool ShowMarkers,
    bool ShowDisabled,
    bool ShowNavMesh,
    bool ShowCollision,
    bool ShowGrid,
    bool EnableLighting,
    bool EnableFog);

/// <summary>
///     Dialog for the dedicated 3D export: projection (Orthographic / Isometric / Trimetric) + a
///     mode-dependent direction, a player-anchored scale (1× ⇒ player 64 px tall), tiling, and the
///     viewer's visibility/atmosphere toggles. The live output-size text is driven by
///     <see cref="MapExport3DPlanner" /> so the user sees the image size + tile count before exporting.
/// </summary>
public sealed partial class MapExport3DDialog : ContentDialog
{
    private static readonly ProjectionMode[] s_modes =
        [ProjectionMode.Orthographic, ProjectionMode.Isometric, ProjectionMode.Trimetric];

    // "What's at the top" for orthographic (the up roll); "viewed from" for the tilted modes.
    private static readonly string[] s_orthoDirections = ["North at top", "East at top", "South at top", "West at top"];
    private static readonly string[] s_isoDirections = ["From the NE", "From the SE", "From the SW", "From the NW"];

    private readonly float _worldMinX;
    private readonly float _worldMaxX;
    private readonly float _worldMinY;
    private readonly float _worldMaxY;
    private readonly float _worldMinZ;
    private readonly float _worldMaxZ;
    private readonly int _maxTileDim;
    private readonly int _maxImageDim;
    private float _scale = 1f;

    internal MapExport3DDialog(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        float worldMinZ, float worldMaxZ, int maxTileDim, int maxImageDim,
        bool showTerrain, bool showMeshes, bool showWater, bool showActivators, bool showMarkers,
        bool showDisabled, bool showNavMesh, bool showCollision, bool showGrid,
        bool enableLighting, bool enableFog)
    {
        InitializeComponent();
        _worldMinX = worldMinX;
        _worldMaxX = worldMaxX;
        _worldMinY = worldMinY;
        _worldMaxY = worldMaxY;
        _worldMinZ = worldMinZ;
        _worldMaxZ = worldMaxZ;
        _maxTileDim = Math.Max(1, maxTileDim);
        _maxImageDim = Math.Max(1, maxImageDim);

        ProjectionComboBox.Items.Add("Orthographic (top-down)");
        ProjectionComboBox.Items.Add("Isometric");
        ProjectionComboBox.Items.Add("Trimetric");
        ProjectionComboBox.SelectedIndex = 0; // fires SelectionChanged → populates directions + sizes

        TerrainCheckBox.IsChecked = showTerrain;
        MeshesCheckBox.IsChecked = showMeshes;
        WaterCheckBox.IsChecked = showWater;
        ActivatorsCheckBox.IsChecked = showActivators;
        MarkersCheckBox.IsChecked = showMarkers;
        DisabledCheckBox.IsChecked = showDisabled;
        NavMeshCheckBox.IsChecked = showNavMesh;
        CollisionCheckBox.IsChecked = showCollision;
        GridCheckBox.IsChecked = showGrid;
        LightingCheckBox.IsChecked = enableLighting;
        FogCheckBox.IsChecked = enableFog;

        UpdateOutputSize();
    }

    private ProjectionMode SelectedMode =>
        ProjectionComboBox.SelectedIndex >= 0 && ProjectionComboBox.SelectedIndex < s_modes.Length
            ? s_modes[ProjectionComboBox.SelectedIndex]
            : ProjectionMode.Orthographic;

    private int SelectedDirectionQuadrant => Math.Max(0, DirectionComboBox.SelectedIndex);

    internal MapExport3DRequest GetRequest() => new(
        Mode: SelectedMode,
        DirectionQuadrant: SelectedDirectionQuadrant,
        Scale: _scale,
        Tiled: TiledCheckBox.IsChecked == true,
        ShowTerrain: TerrainCheckBox.IsChecked == true,
        ShowMeshes: MeshesCheckBox.IsChecked == true,
        ShowWater: WaterCheckBox.IsChecked == true,
        ShowActivators: ActivatorsCheckBox.IsChecked == true,
        ShowMarkers: MarkersCheckBox.IsChecked == true,
        ShowDisabled: DisabledCheckBox.IsChecked == true,
        ShowNavMesh: NavMeshCheckBox.IsChecked == true,
        ShowCollision: CollisionCheckBox.IsChecked == true,
        ShowGrid: GridCheckBox.IsChecked == true,
        EnableLighting: LightingCheckBox.IsChecked == true,
        EnableFog: FogCheckBox.IsChecked == true);

    private void ProjectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateDirections();
        UpdateOutputSize();
    }

    private void DirectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOutputSize();

    private void TiledCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateOutputSize();

    private void ScalePreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var scale))
        {
            _scale = (float)scale;
            UpdateOutputSize();
        }
    }

    private void PopulateDirections()
    {
        // SelectionChanged can fire before DirectionComboBox is realized during XAML load.
        if (DirectionComboBox is null) return;
        var labels = SelectedMode == ProjectionMode.Orthographic ? s_orthoDirections : s_isoDirections;
        DirectionComboBox.Items.Clear();
        foreach (var label in labels) DirectionComboBox.Items.Add(label);
        DirectionComboBox.SelectedIndex = 0;
    }

    private void UpdateOutputSize()
    {
        if (OutputSizeText is null || DirectionComboBox is null) return;

        var tiled = TiledCheckBox.IsChecked == true;
        var plan = MapExport3DPlanner.Plan(
            SelectedMode, SelectedDirectionQuadrant,
            _worldMinX, _worldMaxX, _worldMinY, _worldMaxY, _worldMinZ, _worldMaxZ,
            _scale, tiled, _maxTileDim, _maxImageDim);

        var pxPerCell = (int)Math.Round(MapExport3DPlanner.BaseScalePxPerUnit * _scale * 4096f);
        var totalTiles = plan.Cols * plan.Rows;
        string tileNote;
        if (tiled && totalTiles > 1)
        {
            tileNote = $" — {plan.Cols}×{plan.Rows} tiles ({totalTiles} PNGs + manifest)";
        }
        else if (totalTiles > 1)
        {
            // Non-tiled beyond the per-tile GPU cap: rendered internally tiled, stitched to one PNG.
            tileNote = $" — single image (stitched from {plan.Cols}×{plan.Rows} tiles)";
        }
        else
        {
            tileNote = " — single image";
        }
        OutputSizeText.Text =
            $"Output: {plan.ImageWidth} × {plan.ImageHeight} px (~{pxPerCell} px/cell at {_scale:0.##}×){tileNote}";
    }
}
