using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     Pins the 3D viewer draw-distance slider mapping (log₂-cells): endpoints, the 16-cell
///     default round trip, monotonicity, and clamping — the WinUI panel itself is windows-TFM-only,
///     so the math lives in Core where the fast suite can hold it.
/// </summary>
public sealed class RenderDistanceScaleTests
{
    [Fact]
    public void Endpoints_MapToSliderRange()
    {
        Assert.Equal(2d, RenderDistanceScale.SliderFromCells(RenderDistanceScale.MinCells), 6);
        Assert.Equal(RenderDistanceScale.SliderMaximum,
            RenderDistanceScale.SliderFromCells(RenderDistanceScale.MaxCells), 6);
        Assert.Equal(RenderDistanceScale.MinCells, RenderDistanceScale.CellsFromSlider(2d), 3);
        Assert.Equal(RenderDistanceScale.MaxCells,
            RenderDistanceScale.CellsFromSlider(RenderDistanceScale.SliderMaximum), 2);
    }

    [Fact]
    public void DefaultSixteenCells_RoundTripsExactly()
    {
        var slider = RenderDistanceScale.SliderFromCells(16f);
        Assert.Equal(4d, slider, 6); // log2(16)
        Assert.Equal(16f, RenderDistanceScale.CellsFromSlider(slider), 4);
    }

    [Theory]
    [InlineData(4f)]
    [InlineData(20f)]
    [InlineData(64f)]
    [InlineData(200f)]
    public void RoundTrip_IsStable(float cells)
    {
        var roundTripped = RenderDistanceScale.CellsFromSlider(RenderDistanceScale.SliderFromCells(cells));
        Assert.Equal(cells, roundTripped, 2);
    }

    [Fact]
    public void Mapping_IsMonotonic()
    {
        var previous = double.NegativeInfinity;
        for (var cells = RenderDistanceScale.MinCells; cells <= RenderDistanceScale.MaxCells; cells += 7f)
        {
            var value = RenderDistanceScale.SliderFromCells(cells);
            Assert.True(value > previous);
            previous = value;
        }
    }

    [Fact]
    public void OutOfRangeInputs_Clamp()
    {
        Assert.Equal(2d, RenderDistanceScale.SliderFromCells(1f), 6);
        Assert.Equal(RenderDistanceScale.SliderMaximum, RenderDistanceScale.SliderFromCells(100_000f), 6);
        Assert.Equal(RenderDistanceScale.MinCells, RenderDistanceScale.CellsFromSlider(-3d), 3);
        Assert.Equal(RenderDistanceScale.MaxCells, RenderDistanceScale.CellsFromSlider(40d), 2);
    }
}