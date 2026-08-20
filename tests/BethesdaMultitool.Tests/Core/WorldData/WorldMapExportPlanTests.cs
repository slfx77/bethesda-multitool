using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class WorldMapExportPlanTests
{
    [Fact]
    public void TryCreateGridBounds_UsesInclusiveLongSpans()
    {
        Assert.True(WorldMapExportPlan.TryCreateGridBounds(
            -2, 5, 10, 14, out var bounds, out var error));

        Assert.Equal(string.Empty, error);
        Assert.Equal(8, bounds.CellsWide);
        Assert.Equal(5, bounds.CellsTall);
        Assert.Equal(8, bounds.MaxGridDimension);
    }

    [Fact]
    public void TryCreateGridBounds_RejectsReversedOrUnrepresentableSpans()
    {
        Assert.False(WorldMapExportPlan.TryCreateGridBounds(
            1, 0, 0, 0, out _, out var reversedError));
        Assert.Contains("maxima", reversedError, StringComparison.Ordinal);

        Assert.False(WorldMapExportPlan.TryCreateGridBounds(
            int.MinValue, int.MaxValue, 0, 0, out _, out var spanError));
        Assert.Contains("span", spanError, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_ComputesTileAndFullImageDimensionsWithoutUncheckedProducts()
    {
        Assert.True(WorldMapExportPlan.TryCreateGridBounds(
            -2, 5, 10, 14, out var bounds, out _));

        Assert.True(WorldMapExportPlan.TryCreate(
            bounds,
            100,
            3,
            16_384,
            4_096f,
            out var plan,
            out var error));

        Assert.Equal(string.Empty, error);
        Assert.NotNull(plan);
        Assert.Equal(3, plan.Columns);
        Assert.Equal(2, plan.Rows);
        Assert.Equal(6, plan.TotalTiles);
        Assert.Equal(800L, plan.ImageWidth);
        Assert.Equal(500L, plan.ImageHeight);
    }

    [Fact]
    public void TryCreate_RejectsTileCountAndGpuDimensionOverflow()
    {
        Assert.True(WorldMapExportPlan.TryCreateGridBounds(
            0, 49_999, 0, 49_999, out var manyCells, out _));
        Assert.False(WorldMapExportPlan.TryCreate(
            manyCells,
            1,
            1,
            16_384,
            4_096f,
            out _,
            out var countError));
        Assert.Contains("tile grid", countError, StringComparison.Ordinal);

        Assert.True(WorldMapExportPlan.TryCreateGridBounds(
            0, 19_999, 0, 0, out var wide, out _));
        Assert.False(WorldMapExportPlan.TryCreate(
            wide,
            1,
            20_000,
            16_384,
            4_096f,
            out _,
            out var dimensionError));
        Assert.Contains("GPU texture", dimensionError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void TryCreate_RejectsExtremeGridEdgesBeforeTheFloatRenderTransform(int gridCoordinate)
    {
        Assert.True(WorldMapExportPlan.TryCreateGridBounds(
            gridCoordinate,
            gridCoordinate,
            0,
            0,
            out var bounds,
            out _));

        Assert.False(WorldMapExportPlan.TryCreate(
            bounds,
            1,
            1,
            16_384,
            4_096f,
            out _,
            out var error));
        Assert.Contains("float render transform", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_RejectsFiniteWorldEdgesWhoseProjectedCellWidthWouldBeWrong()
    {
        const int gridCoordinate = 1_000_000;
        Assert.True(WorldMapExportPlan.TryCreateGridBounds(
            gridCoordinate,
            gridCoordinate,
            0,
            0,
            out var bounds,
            out _));

        Assert.False(WorldMapExportPlan.TryCreate(
            bounds,
            100,
            1,
            16_384,
            4_096f,
            out _,
            out var error));
        Assert.Contains("float render transform", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_RejectsRangesWhoseInteriorCellEdgesCollapseAsFloats()
    {
        Assert.True(WorldMapExportPlan.TryCreateGridBounds(
            16_777_215,
            16_777_217,
            0,
            0,
            out var bounds,
            out _));

        Assert.False(WorldMapExportPlan.TryCreate(
            bounds,
            1,
            3,
            16_384,
            4_096f,
            out _,
            out var error));
        Assert.Contains("float render transform", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_RejectsAFloatScaleThatOverflowsEvenWhenCellSizeIsFinite()
    {
        Assert.True(WorldMapExportPlan.TryCreateGridBounds(
            0, 0, 0, 0, out var bounds, out _));

        Assert.False(WorldMapExportPlan.TryCreate(
            bounds,
            1,
            1,
            16_384,
            float.Epsilon,
            out _,
            out var error));
        Assert.Contains("pixel-to-world scale", error, StringComparison.Ordinal);
    }

    [Fact]
    public void GetTileWorldBounds_EvaluatesUpperEdgesWithoutIntegerAddition()
    {
        const int gridCoordinate = 100;
        Assert.True(WorldMapExportPlan.TryCreateGridBounds(
            gridCoordinate,
            gridCoordinate,
            -gridCoordinate,
            -gridCoordinate,
            out var bounds,
            out _));
        Assert.True(WorldMapExportPlan.TryCreate(
            bounds,
            1,
            1,
            16_384,
            4_096f,
            out var plan,
            out _));

        var world = Assert.IsType<WorldMapExportPlan>(plan).GetTileWorldBounds(
            gridCoordinate,
            gridCoordinate,
            -gridCoordinate,
            -gridCoordinate);

        Assert.Equal((float)(gridCoordinate * 4_096d), world.MinX);
        Assert.Equal((float)((gridCoordinate + 1d) * 4_096d), world.MaxX);
        Assert.Equal((float)(-gridCoordinate * 4_096d), world.MinY);
        Assert.Equal((float)((-gridCoordinate + 1d) * 4_096d), world.MaxY);
        Assert.True(world.MaxX > world.MinX);
        Assert.True(world.MaxY > world.MinY);
    }
}