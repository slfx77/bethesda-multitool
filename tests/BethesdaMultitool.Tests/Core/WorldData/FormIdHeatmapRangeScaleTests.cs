using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     Pins the FormID-heatmap range slider mapping (log₂-cells, mirroring
///     <see cref="RenderDistanceScaleTests" />): endpoints, finite round trips, monotonicity,
///     clamping, and the dedicated UNLIMITED top stop that maps to
///     <see cref="float.PositiveInfinity" /> — the WinUI panel is windows-TFM-only, so the math
///     lives in Core where the fast suite can hold it.
/// </summary>
public sealed class FormIdHeatmapRangeScaleTests
{
    [Fact]
    public void Endpoints_MapToSliderRange()
    {
        Assert.Equal(1d, FormIdHeatmapRangeScale.SliderFromCells(FormIdHeatmapRangeScale.MinCells), 6);
        Assert.Equal(Math.Log2(FormIdHeatmapRangeScale.MaxFiniteCells),
            FormIdHeatmapRangeScale.SliderFromCells(FormIdHeatmapRangeScale.MaxFiniteCells), 6);
        Assert.Equal(FormIdHeatmapRangeScale.MinCells,
            FormIdHeatmapRangeScale.CellsFromSlider(FormIdHeatmapRangeScale.SliderMinimum), 3);
        Assert.Equal(FormIdHeatmapRangeScale.MaxFiniteCells,
            FormIdHeatmapRangeScale.CellsFromSlider(Math.Log2(FormIdHeatmapRangeScale.MaxFiniteCells)), 2);
    }

    [Fact]
    public void UnlimitedTopStop_RoundTrips()
    {
        Assert.Equal(FormIdHeatmapRangeScale.SliderMaximum,
            FormIdHeatmapRangeScale.SliderFromCells(float.PositiveInfinity), 6);
        Assert.True(float.IsPositiveInfinity(
            FormIdHeatmapRangeScale.CellsFromSlider(FormIdHeatmapRangeScale.SliderMaximum)));
        // Anything past the finite ceiling is inside the unlimited band.
        Assert.True(float.IsPositiveInfinity(FormIdHeatmapRangeScale.CellsFromSlider(
            Math.Log2(FormIdHeatmapRangeScale.MaxFiniteCells) + 0.01)));
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(5f)]
    [InlineData(16f)]
    [InlineData(64f)]
    [InlineData(128f)]
    public void FiniteRoundTrip_IsStable(float cells)
    {
        var roundTripped = FormIdHeatmapRangeScale.CellsFromSlider(
            FormIdHeatmapRangeScale.SliderFromCells(cells));
        Assert.Equal(cells, roundTripped, 2);
    }

    [Fact]
    public void FinitePositions_QuantizeToWholeCells()
    {
        // The heatmap selects FULL cells on the cell grid, so the value the slider reports must be
        // the value applied — fractional log positions land on the nearest whole cell.
        Assert.Equal(3f, FormIdHeatmapRangeScale.CellsFromSlider(Math.Log2(3.4)), 3);
        Assert.Equal(4f, FormIdHeatmapRangeScale.CellsFromSlider(Math.Log2(3.6)), 3);
        Assert.Equal(2f, FormIdHeatmapRangeScale.CellsFromSlider(Math.Log2(2.4)), 3);
        Assert.Equal(127f, FormIdHeatmapRangeScale.CellsFromSlider(Math.Log2(126.7)), 3);
    }

    [Fact]
    public void Mapping_IsMonotonic()
    {
        var previous = double.NegativeInfinity;
        for (var cells = FormIdHeatmapRangeScale.MinCells;
             cells <= FormIdHeatmapRangeScale.MaxFiniteCells;
             cells += 3f)
        {
            var value = FormIdHeatmapRangeScale.SliderFromCells(cells);
            Assert.True(value > previous);
            previous = value;
        }

        // The unlimited stop sits strictly above every finite position.
        Assert.True(FormIdHeatmapRangeScale.SliderFromCells(float.PositiveInfinity) > previous);
    }

    [Fact]
    public void OutOfRangeInputs_Clamp()
    {
        Assert.Equal(1d, FormIdHeatmapRangeScale.SliderFromCells(0.25f), 6);
        // A huge FINITE input clamps to the finite ceiling — only PositiveInfinity selects the stop.
        Assert.Equal(Math.Log2(FormIdHeatmapRangeScale.MaxFiniteCells),
            FormIdHeatmapRangeScale.SliderFromCells(100_000f), 6);
        Assert.Equal(FormIdHeatmapRangeScale.MinCells, FormIdHeatmapRangeScale.CellsFromSlider(-3d), 3);
        Assert.Equal(FormIdHeatmapRangeScale.MinCells, FormIdHeatmapRangeScale.CellsFromSlider(0d), 3);
    }
}