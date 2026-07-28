using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Source contracts for the 3D export path. Two regressions these lock:
///     (1) the export must resolve HDR→SDR with the SAME engine imagespace tonemap the live frame and
///     scene-capture use — without it the offscreen target keeps its GammaAcesDefaults operator while the
///     scene is lit at engine-HDR scale, so exported PNGs come out washed out; and (2) grass must be an
///     explicit, saved/restored export choice rather than passively inheriting the live viewer's state.
/// </summary>
public sealed class Export3DTonemapAndGrassSourceContractTests
{
    [Fact]
    public void ExportResolvesReadbackWithEngineTonemapBeforeBindingAndReadback()
    {
        var render = ExportRenderMethod();

        // Deterministic offscreen exposure (AdaptFactor=1), same contract as the scene-capture readback.
        Assert.Contains(
            "var tonemap = ResolveTonemapSettings() with { AdaptFactor = 1f };", render,
            StringComparison.Ordinal);

        // The target's readback operator AND the scene lighting both use that one resolved tonemap, and
        // the assignment happens before the atmosphere bind and before the readback is recorded.
        SourceContract.AssertOrder(
            render,
            "var tonemap = ResolveTonemapSettings() with { AdaptFactor = 1f };",
            "target.TonemapSettings = tonemap;",
            "lightVisibility: cylinder, tonemapOverride: tonemap);",
            "target.RecordReadback(cmd);");
    }

    [Fact]
    public void GrassIsAnExplicitExportOptionSavedSetAndRestored()
    {
        var export3D = ReadAppSource("WorldView3DControl.Export3D.cs");

        // The option exists on the export record.
        Assert.Contains("bool ShowGrass,", export3D, StringComparison.Ordinal);

        var render = ExportRenderMethod();
        // Save → apply from opts → restore, so the export never leaks its choice into the live viewer.
        SourceContract.AssertOrder(
            render,
            "var prevShowGrass = _references.ShowGrass;",
            "_references.ShowGrass = opts.ShowGrass;",
            "_references.ShowGrass = prevShowGrass;");
    }

    [Fact]
    public void GrassIsAnIndependentExportTabToggleSeededFromTheViewAndReadIntoOptions()
    {
        // The Export tab hosts its own Grass checkbox (independent of the live view).
        var panelXaml = SourceContract.ReadAppSource("WorldView3DExportPanel.xaml");
        Assert.Contains("x:Name=\"GrassCheckBox\"", panelXaml, StringComparison.Ordinal);

        // The viewer seeds it once from the live grass state, then reads it into Export3DOptions on run.
        var panel = ReadAppSource("WorldView3DControl.ExportPanel.cs");
        Assert.Contains("ExportPanel.GrassCheckBox.IsChecked = _showGrass;", panel, StringComparison.Ordinal);
        Assert.Contains("ShowGrass: ExportPanel.GrassCheckBox.IsChecked == true,", panel, StringComparison.Ordinal);
    }

    private static string ExportRenderMethod()
    {
        var source = ReadAppSource("WorldView3DControl.Export3D.cs");
        var start = source.IndexOf(
            "internal async Task<Export3DTile?> RenderProjectionTileAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing RenderProjectionTileAsync.");
        var end = source.IndexOf("internal sealed record Export3DOptions(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Missing Export3DOptions record after the render method.");
        return source[start..end];
    }

    private static string ReadAppSource(string fileName)
    {
        return SourceContract.ReadAppSource(fileName);
    }
}