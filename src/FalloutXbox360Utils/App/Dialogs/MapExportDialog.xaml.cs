using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FalloutXbox360Utils;

/// <summary>
///     Caller-facing values returned from <see cref="MapExportDialog" />. Null is returned
///     when the user cancels.
/// </summary>
internal sealed record MapExportRequest(
    int LongEdgePx,
    bool IncludeMarkers,
    bool IncludeNavMesh,
    bool IncludeWater);

/// <summary>
///     Dialog that lets the user choose export resolution and which overlays to include.
///     Resolution is expressed both as a px long-edge value and as a "px per cell" scale
///     (1× = 33 px/cell, matching the 33-vertex LAND grid one-to-one with the source bitmap).
/// </summary>
public sealed partial class MapExportDialog : ContentDialog
{
    /// <summary>
    ///     Native source resolution per cell side. The layer bitmaps blit 33 columns per cell
    ///     without sharing boundary verts between neighbours (see HeightmapRenderer.imgW), so
    ///     1:1 output is 33 px/cell — anything less downscales and bilinear-smudges the source.
    /// </summary>
    private const int NativePxPerCell = 33;

    private readonly int _cellsWide;
    private readonly int _cellsTall;
    private bool _suspendNumberBox;

    public MapExportDialog(
        int cellsWide, int cellsTall,
        bool initialIncludeMarkers, bool initialIncludeNavMesh, bool initialIncludeWater,
        int initialLongEdgePx = 4096)
    {
        InitializeComponent();
        _cellsWide = Math.Max(1, cellsWide);
        _cellsTall = Math.Max(1, cellsTall);

        MarkersCheckBox.IsChecked = initialIncludeMarkers;
        NavMeshCheckBox.IsChecked = initialIncludeNavMesh;
        WaterCheckBox.IsChecked = initialIncludeWater;

        WorldSizeText.Text = $"Worldspace: {_cellsWide} × {_cellsTall} cells";

        _suspendNumberBox = true;
        LongEdgeNumberBox.Value = initialLongEdgePx;
        _suspendNumberBox = false;
        UpdateOutputSize();
    }

    internal MapExportRequest GetRequest()
    {
        return new MapExportRequest(
            LongEdgePx: (int)Math.Round(LongEdgeNumberBox.Value),
            IncludeMarkers: MarkersCheckBox.IsChecked == true,
            IncludeNavMesh: NavMeshCheckBox.IsChecked == true,
            IncludeWater: WaterCheckBox.IsChecked == true);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tag) return;
        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var scale)) return;

        var maxCells = Math.Max(_cellsWide, _cellsTall);
        var pxPerCell = NativePxPerCell * scale;
        var longEdge = (int)Math.Round(pxPerCell * maxCells);
        longEdge = Math.Clamp(longEdge, 32, 32768);

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
        var longEdge = (int)Math.Round(LongEdgeNumberBox.Value);
        if (longEdge < 32 || double.IsNaN(LongEdgeNumberBox.Value))
        {
            OutputSizeText.Text = "";
            return;
        }

        var maxCells = Math.Max(_cellsWide, _cellsTall);
        var pxPerCell = Math.Max(1, longEdge / maxCells);
        var imageW = _cellsWide * pxPerCell;
        var imageH = _cellsTall * pxPerCell;
        var scaleX = (double)pxPerCell / NativePxPerCell;
        OutputSizeText.Text = $"Output: {imageW} × {imageH} px ({pxPerCell} px/cell, {scaleX:0.##}×)";
    }
}
