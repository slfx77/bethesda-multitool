using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Caller-facing values returned from <see cref="MapExportDialog" />. Null is returned
///     when the user cancels.
/// </summary>
internal sealed record MapExportRequest(
    WorldMapLayer Layer,
    int LongEdgePx,
    bool IncludeMarkers,
    bool IncludeNavMesh,
    bool IncludeWater,
    bool IncludeGrid,
    bool Tiled);

/// <summary>
///     Dialog that lets the user choose the export layer, resolution, and which overlays to include.
///     Resolution is shown both as a px long-edge value and as a "px per cell" scale relative to the
///     selected layer's NATIVE source resolution — 33 px/cell for heightmap-family layers, 132 (up to
///     528) for the texture layer — so switching layers updates the meaningful scale.
/// </summary>
public sealed partial class MapExportDialog : ContentDialog
{
    /// <summary>Layers offered for export, in display order (matches the viewer's layer dropdown).</summary>
    private static readonly WorldMapLayer[] s_layers =
    [
        WorldMapLayer.Heightmap,
        WorldMapLayer.VertexColors,
        WorldMapLayer.TerrainRegions,
        WorldMapLayer.TerrainTextures,
        WorldMapLayer.Slope
    ];

    private readonly int _cellsWide;
    private readonly int _cellsTall;
    private bool _suspendNumberBox;

    internal MapExportDialog(
        int cellsWide, int cellsTall,
        WorldMapLayer initialLayer,
        bool initialIncludeMarkers, bool initialIncludeNavMesh, bool initialIncludeWater,
        bool initialIncludeGrid,
        int initialLongEdgePx = 4096)
    {
        InitializeComponent();
        _cellsWide = Math.Max(1, cellsWide);
        _cellsTall = Math.Max(1, cellsTall);

        foreach (var layer in s_layers)
        {
            LayerComboBox.Items.Add(layer.DisplayName());
        }

        LayerComboBox.SelectedIndex = Math.Max(0, Array.IndexOf(s_layers, initialLayer));

        MarkersCheckBox.IsChecked = initialIncludeMarkers;
        NavMeshCheckBox.IsChecked = initialIncludeNavMesh;
        WaterCheckBox.IsChecked = initialIncludeWater;
        GridCheckBox.IsChecked = initialIncludeGrid;

        WorldSizeText.Text = $"Worldspace: {_cellsWide} × {_cellsTall} cells";

        _suspendNumberBox = true;
        LongEdgeNumberBox.Value = initialLongEdgePx;
        _suspendNumberBox = false;
        UpdateOutputSize();
    }

    private WorldMapLayer SelectedLayer =>
        LayerComboBox.SelectedIndex >= 0 && LayerComboBox.SelectedIndex < s_layers.Length
            ? s_layers[LayerComboBox.SelectedIndex]
            : WorldMapLayer.Heightmap;

    /// <summary>Native source px/cell for a layer: 132 for the texture layer, 33 otherwise.</summary>
    private static int NativePxPerCell(WorldMapLayer layer) =>
        layer == WorldMapLayer.TerrainTextures
            ? WorldMapLayerRenderer.TexturePixelsPerCell
            : WorldMapLayerRenderer.HeightmapPixelsPerCell;

    /// <summary>Ceiling on real source detail: 528 for texture, 4× native (132) for others.</summary>
    private static int MaxPxPerCell(WorldMapLayer layer) =>
        layer == WorldMapLayer.TerrainTextures
            ? WorldMapLayerRenderer.MaxTexturePixelsPerCell
            : WorldMapLayerRenderer.HeightmapPixelsPerCell * 4;

    internal MapExportRequest GetRequest()
    {
        return new MapExportRequest(
            Layer: SelectedLayer,
            LongEdgePx: (int)Math.Round(LongEdgeNumberBox.Value),
            IncludeMarkers: MarkersCheckBox.IsChecked == true,
            IncludeNavMesh: NavMeshCheckBox.IsChecked == true,
            IncludeWater: WaterCheckBox.IsChecked == true,
            IncludeGrid: GridCheckBox.IsChecked == true,
            Tiled: TiledCheckBox.IsChecked == true);
    }

    private void LayerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOutputSize();

    private void TiledCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateOutputSize();

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var scale)) return;

        var layer = SelectedLayer;
        var maxCells = Math.Max(_cellsWide, _cellsTall);
        var pxPerCell = Math.Clamp((int)Math.Round(NativePxPerCell(layer) * scale), 1, MaxPxPerCell(layer));
        var longEdge = Math.Clamp(pxPerCell * maxCells, 32, (int)LongEdgeNumberBox.Maximum);

        _suspendNumberBox = true;
        LongEdgeNumberBox.Value = longEdge;
        _suspendNumberBox = false;
        UpdateOutputSize();
    }

    private void LongEdgeNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suspendNumberBox) return;
        UpdateOutputSize();
    }

    private void UpdateOutputSize()
    {
        // SelectionChanged can fire before the rest of the named controls exist; bail until ready.
        if (OutputSizeText is null || LongEdgeNumberBox is null) return;

        var longEdge = (int)Math.Round(LongEdgeNumberBox.Value);
        if (longEdge < 32 || double.IsNaN(LongEdgeNumberBox.Value))
        {
            OutputSizeText.Text = "";
            return;
        }

        var layer = SelectedLayer;
        var native = NativePxPerCell(layer);
        var maxCells = Math.Max(_cellsWide, _cellsTall);
        var requestedPpc = Math.Max(1, longEdge / maxCells);

        // Without tiling a single PNG can't exceed the GPU max dimension — show the effective
        // px/cell after that clamp. Tiled splits into multiple files instead, so no clamp.
        var tiled = TiledCheckBox?.IsChecked == true;
        var effectivePpc = requestedPpc;
        var capped = false;
        if (!tiled && (long)requestedPpc * maxCells > WorldMapExporter.ExportMaxTileDimension)
        {
            effectivePpc = Math.Max(1, WorldMapExporter.ExportMaxTileDimension / maxCells);
            capped = true;
        }

        var imageW = _cellsWide * effectivePpc;
        var imageH = _cellsTall * effectivePpc;
        var scaleX = (double)effectivePpc / native;
        string note;
        if (capped)
        {
            note = $"  — capped to {WorldMapExporter.ExportMaxTileDimension} px (enable Tiled for full detail)";
        }
        else if (tiled && (long)effectivePpc * maxCells > WorldMapExporter.ExportMaxTileDimension)
        {
            var perTile = Math.Max(1, WorldMapExporter.ExportMaxTileDimension / effectivePpc);
            var cols = (_cellsWide + perTile - 1) / perTile;
            var rows = (_cellsTall + perTile - 1) / perTile;
            note = $"  — tiled into {cols}×{rows} PNGs";
        }
        else
        {
            note = "";
        }

        OutputSizeText.Text =
            $"Output: {imageW} × {imageH} px ({effectivePpc} px/cell, {scaleX:0.##}× of {native} native){note}";
    }
}
