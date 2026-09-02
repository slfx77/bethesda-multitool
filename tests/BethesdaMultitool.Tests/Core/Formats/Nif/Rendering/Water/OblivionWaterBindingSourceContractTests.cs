using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Pins the behavior-bearing TES4 WATR bindings that sit outside the shader itself: FNAM bit 1
///     chooses WATER007, while CELL XCWT chooses both the WATR constants and its TNAM detail map.
/// </summary>
public sealed class OblivionWaterBindingSourceContractTests
{
    [Fact]
    public void ReflectiveFnamGatesLiveCaptureAndDrawTimeProjectiveSceneRoutes()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
        Assert.Contains(
            "_game == BethesdaGame.Oblivion && _appearance?.IsReflective != false;",
            renderer, StringComparison.Ordinal);
        Assert.Equal(2, SourceContract.CountOccurrences(
            renderer,
            "ReflectionSceneFlag = _reflectionIsScene && AllowsProjectiveSceneReflection ? 1u : 0u,"));

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var livePlanner = SourceContract.Extract(
            frame,
            "// The mirror planner reads the current material's FNAM Reflective bit",
            "// PLANAR SKY REFLECTION");
        SourceContract.AssertOrder(
            livePlanner,
            "RefreshWaterAppearanceForCurrentCell();",
            "_water.AllowsProjectiveSceneReflection",
            "_water.TryGetDominantVisibleWaterPlaneHeight(");

        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var capturePlanner = SourceContract.Extract(
            capture,
            "// Scene-mirror planning",
            "if (_showSky)");
        Assert.Contains("_water.AllowsProjectiveSceneReflection", capturePlanner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CellXcwtRefreshRebindsOblivionWatrAndTnamIncludingInteriors()
    {
        var cells = SourceContract.ReadAppSource("WorldView3DControl.Cells.cs");
        var refresh = SourceContract.Extract(
            cells,
            "private void RefreshWaterAppearanceForCurrentCell",
            "// ── Interior cell browser");
        Assert.Contains(
            "or BethesdaMultitool.Core.Games.BethesdaGame.Oblivion",
            refresh, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            refresh,
            "var appearance = WaterAppearance.FromWaterRecord(selection.Water);",
            "_water.SetAppearance(",
            "_water.SetOblivionDetailTexture(ResolveWatrDetailTextureIndex(selection.Water));");

        var interior = SourceContract.Extract(
            cells,
            "private void BuildInteriorCellGrid",
            "/// <summary>\n    ///     Computes the vertical");
        Assert.DoesNotContain("SetOblivionDetailTexture(null)", interior,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            interior,
            "var appearance = WaterAppearance.FromWaterRecord(waterSelection.Water);",
            "_water?.SetOblivionDetailTexture(ResolveWatrDetailTextureIndex(waterSelection.Water));",
            "_water?.LoadData(");
    }
}
