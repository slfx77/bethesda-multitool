using System.Numerics;
using Xunit;

namespace BethesdaMultitool.Tests.Core;

public sealed class WorldMapGridViewportTests
{
    private const float FalloutCellSize = 4096f;
    private const float MorrowindCellSize = 8192f;

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]
    [InlineData(-1, 0, true)] // Inclusive edge: cell -1 touches the viewport at x=0.
    [InlineData(0, 1, false)]
    [InlineData(0, -1, true)] // Inclusive edge: cell -1 touches the viewport at y=0.
    public void IntersectsCell_UsesNorthNegativeGridCoordinates(
        int gridX, int gridY, bool expected)
    {
        var viewport = WorldMapGridViewport.FromCorners(
            new Vector2(0f, -FalloutCellSize + 1f),
            new Vector2(FalloutCellSize - 1f, 0f));

        Assert.Equal(expected, viewport.IntersectsCell(gridX, gridY, FalloutCellSize));
    }

    [Fact]
    public void FromCorners_AcceptsInvertedViewportCorners()
    {
        var viewport = WorldMapGridViewport.FromCorners(
            new Vector2(-2 * FalloutCellSize, -2 * FalloutCellSize),
            new Vector2(-3 * FalloutCellSize, -3 * FalloutCellSize));

        Assert.True(viewport.IntersectsCell(-3, 2, FalloutCellSize));
    }

    [Fact]
    public void IntersectsCell_UsesProvidedGameCellSize()
    {
        // Morrowind cell (2, -3) occupies x=16384..24576, y=16384..24576.
        var viewport = WorldMapGridViewport.FromCorners(
            new Vector2(17000f, 18000f), new Vector2(17500f, 18500f));

        Assert.True(viewport.IntersectsCell(2, -3, MorrowindCellSize));
        Assert.False(viewport.IntersectsCell(2, -3, FalloutCellSize));
    }

    [Fact]
    public void FromCorners_MarginRetainsTerrainOutsetAtViewportEdge()
    {
        // Cell (1,0) begins at x=4096 and normally misses a viewport ending at x=4095.5.
        var topLeft = new Vector2(0f, -FalloutCellSize);
        var bottomRight = new Vector2(FalloutCellSize - 0.5f, 0f);

        Assert.False(WorldMapGridViewport.FromCorners(topLeft, bottomRight)
            .IntersectsCell(1, 0, FalloutCellSize));
        Assert.True(WorldMapGridViewport.FromCorners(topLeft, bottomRight, margin: 1f)
            .IntersectsCell(1, 0, FalloutCellSize));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void IntersectsCell_InvalidCellSize_IsNotVisible(float cellWorldSize)
    {
        var viewport = WorldMapGridViewport.FromCorners(
            new Vector2(-100f, -100f), new Vector2(100f, 100f));

        Assert.False(viewport.IntersectsCell(0, 0, cellWorldSize));
    }
}
