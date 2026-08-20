using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Source-contract pins for the FormID heatmap's whole-cell selection and its settings-panel
///     key. The selection must be a block of FULL cells on the cell grid (Chebyshev around the
///     camera's cell — never a circular world-unit slice through cells), the range must travel in
///     cells end to end (control → renderer), and the key's gradient must be sampled from
///     <c>FormIdHeatmapPalette</c> with its labels fed the live normalization window from the
///     render loop. The WinUI layer is windows-TFM-only, so these hold as source pins.
/// </summary>
public sealed class FormIdHeatmapCellSelectionAndKeyContractTests
{
    private static string RendererSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
    }

    [Fact]
    public void RendererSelectsWholeCellsAroundTheCameraCell()
    {
        var renderer = RendererSource();

        // One shared predicate gates BOTH the min/max scan and the per-instance tint, and it is a
        // Chebyshev test on floor(position / cellSize) — full cells, not a radius.
        Assert.Contains("private bool IsInsideHeatmapCellBlock(float worldX, float worldY)",
            renderer, StringComparison.Ordinal);
        Assert.Contains(
            "Math.Abs((int)MathF.Floor(worldX / _frameHeatmapCellSize) - _frameHeatmapCamCellX) <= _frameHeatmapCellRadius",
            renderer, StringComparison.Ordinal);
        Assert.Contains("if (!IsInsideHeatmapCellBlock(worldX, worldY))", renderer, StringComparison.Ordinal);
        Assert.Contains("if (!IsInsideHeatmapCellBlock(position.X, position.Y))", renderer, StringComparison.Ordinal);

        // The range travels in CELLS and the grid's cell size comes from the active spatial index.
        Assert.Contains("public float FormIdHeatmapRangeCells", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("FormIdHeatmapRangeWorldUnits", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlSendsCellsAndQuantizesToWholeCells()
    {
        var toolbar = SourceContract.ReadAppSource("WorldView3DControl.Toolbar.cs");
        Assert.Contains("_references.FormIdHeatmapRangeCells = _formIdHeatmapRangeCells", toolbar,
            StringComparison.Ordinal);
        Assert.Contains("MathF.Round(Math.Clamp(", toolbar, StringComparison.Ordinal);

        var converter = SourceContract.ReadAppSource("HeatmapRangeTooltipConverter.cs");
        Assert.Contains("\"{0:0} c\"", converter, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyGradientSamplesThePaletteAndLabelsFollowTheLiveWindow()
    {
        var panelXaml = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");
        Assert.Contains("x:Name=\"HeatmapKeyGradientBar\"", panelXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeatmapKeyMinLabel\"", panelXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeatmapKeyMaxLabel\"", panelXaml, StringComparison.Ordinal);

        // The bar is built by sampling the SAME palette the meshes use — the key cannot drift.
        var panelCode = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml.cs");
        Assert.Contains("FormIdHeatmapPalette.ToRgb(t)", panelCode, StringComparison.Ordinal);

        // Live labels: the render loop pushes the renderer's window into the panel on change.
        var renderer = RendererSource();
        Assert.Contains("public (uint Min, uint Max)? FormIdHeatmapWindow", renderer, StringComparison.Ordinal);
        var toolbar = SourceContract.ReadAppSource("WorldView3DControl.Toolbar.cs");
        Assert.Contains("_references?.FormIdHeatmapWindow", toolbar, StringComparison.Ordinal);
        Assert.Contains("SettingsPanel.UpdateHeatmapKeyWindow(window, rankedCount)", toolbar,
            StringComparison.Ordinal);
        // The step count travels with the window so the key can say how many ramp steps are in use.
        Assert.Contains("_references?.FormIdHeatmapRankedCount", toolbar, StringComparison.Ordinal);
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        Assert.Contains("UpdateFormIdHeatmapKey();", frame, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The gradient is ORDINAL and covers only what renders. Value-linear normalization made any
    ///     worldspace with late additions (FO3's DCWorld15) read as one flat colour, and counting
    ///     hidden refs let invisible content consume ramp steps the visible refs needed.
    /// </summary>
    [Fact]
    public void ScanRanksVisibleRefsOrdinallyAndReRanksWhenVisibilityChanges()
    {
        var renderer = RendererSource();

        // Ordinal: the tint reads a rank from the ranking, not a min/max interpolation.
        Assert.Contains("_frameHeatmapRanking.Normalize(formId)", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("FormIdHeatmapPalette.Normalize(", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("_frameHeatmapMin", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("_frameHeatmapMax", renderer, StringComparison.Ordinal);

        // Hidden refs are excluded using the cull's OWN gate, so ranked set == rendered set.
        Assert.Contains(
            "if (!visibilityKey.IsVisible(r, _enabledOverrides, DayNightStates))",
            renderer, StringComparison.Ordinal);

        // And the memo re-ranks when a visibility gate changes, or hiding a ref would do nothing.
        Assert.Contains("_heatmapScanVisibilityKey == visibilityKey", renderer, StringComparison.Ordinal);
        Assert.Contains("_heatmapScanVisibilityKey = visibilityKey;", renderer, StringComparison.Ordinal);
    }

    /// <summary>The ramp is the 2D map's Colorful heightmap ramp, shared from one definition.</summary>
    [Fact]
    public void PaletteDelegatesToTheColorfulHeightmapRamp()
    {
        var palette = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Scene",
            "FormIdHeatmapPalette.cs");

        Assert.Contains("HeightmapColorRenderer.HeightToColor(clamped)", palette, StringComparison.Ordinal);
        // The old fixed-S/L hue sweep is gone.
        Assert.DoesNotContain("ColdHueDegrees", palette, StringComparison.Ordinal);
        Assert.DoesNotContain("HslToRgb", palette, StringComparison.Ordinal);
    }
}