using System.Globalization;
using System.Text.RegularExpressions;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Source contracts for the interior/exterior placed-light caps
///     (<c>WorldView3DControl.PointLights.cs</c> lives in the App TFM, so the routing is pinned by
///     source text). Interiors are hard-limited by the per-cell cap alone — the frame-level cap only
///     runs on the exterior branch — so the interior ceiling was raised to 64 as an interim measure
///     (dense interiors like the Strip casinos popped lights at 16) until the engine-parity
///     light-volume selection work (FnvRetailLightAssociationOracle) lands. The exterior cap must
///     stay 16: exteriors accumulate across every visible cell, and FnvActiveAdtBasePolicy keys on
///     PlacedLightCount == 0.
/// </summary>
public sealed class PlacedLightCapTests
{
    private static string ReadPointLightsSource()
    {
        return SourceContract.ReadAppSource("WorldView3DControl.PointLights.cs");
    }

    [Fact]
    public void CapConstantsKeepTheirValues()
    {
        var source = ReadPointLightsSource();
        Assert.Contains(
            "private const int MaxPlacedLightsPerInteriorCell = 64;", source, StringComparison.Ordinal);
        Assert.Contains(
            "private const int MaxPlacedLightsPerExteriorCell = 16;", source, StringComparison.Ordinal);
        Assert.Contains(
            "private const int MaxPlacedLightsPerFrame = 64;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameBudgetCoversTheInteriorCellCap()
    {
        // Interiors bypass ApplyFramePlacedLightCap, so a single interior cell must never be able
        // to exceed the whole-frame upload budget.
        var source = ReadPointLightsSource();
        var interior = ExtractConstant(source, "MaxPlacedLightsPerInteriorCell");
        var exterior = ExtractConstant(source, "MaxPlacedLightsPerExteriorCell");
        var frame = ExtractConstant(source, "MaxPlacedLightsPerFrame");
        Assert.True(frame >= interior, $"MaxPlacedLightsPerFrame ({frame}) < interior cap ({interior}).");
        Assert.True(interior >= exterior, $"Interior cap ({interior}) < exterior cap ({exterior}).");
    }

    [Fact]
    public void InteriorBranchUsesTheInteriorCapAndExteriorBranchUsesTheExteriorCap()
    {
        var source = ReadPointLightsSource();

        // Interior branch: _selectedInterior gather runs with the interior constant, and the
        // exterior visible-cell loop runs with the exterior constant before the frame cap.
        SourceContract.AssertOrder(
            source,
            "if (_selectedInterior is { } interior)",
            "AppendCellLights(interior, _camera.Position, MaxPlacedLightsPerInteriorCell);",
            "AppendCellLights(visibleCell.Cell, cylinder.Position, MaxPlacedLightsPerExteriorCell);",
            "ApplyFramePlacedLightCap(cylinder.Position);");

        // No call site may fall back to an unsplit per-cell constant.
        Assert.DoesNotContain("MaxPlacedLightsPerCell,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("const int MaxPlacedLightsPerCell ", source, StringComparison.Ordinal);
    }

    private static int ExtractConstant(string source, string name)
    {
        var match = Regex.Match(source, $@"private const int {Regex.Escape(name)} = (\d+);");
        Assert.True(match.Success, $"Constant {name} not found.");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}