using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class ExportTileTraversalTests
{
    [Fact]
    public void GetSerpentineCoordinate_KnownThreeByFourGrid_AlternatesOnlyColumnDirection()
    {
        var actual = Enumerable.Range(0, 12)
            .Select(ordinal => ExportTileTraversal.GetSerpentineCoordinate(ordinal, 4, 3))
            .ToArray();

        Assert.Equal(
            new[]
            {
                new ExportTileCoordinate(0, 0),
                new ExportTileCoordinate(0, 1),
                new ExportTileCoordinate(0, 2),
                new ExportTileCoordinate(0, 3),
                new ExportTileCoordinate(1, 3),
                new ExportTileCoordinate(1, 2),
                new ExportTileCoordinate(1, 1),
                new ExportTileCoordinate(1, 0),
                new ExportTileCoordinate(2, 0),
                new ExportTileCoordinate(2, 1),
                new ExportTileCoordinate(2, 2),
                new ExportTileCoordinate(2, 3)
            },
            actual);
    }

    [Fact]
    public void GetSerpentineCoordinate_GridsThroughSixteenBySixteen_VisitEveryPhysicalTileOnce()
    {
        for (var rows = 1; rows <= 16; rows++)
        {
            for (var columns = 1; columns <= 16; columns++)
            {
                var seen = new bool[rows, columns];
                ExportTileCoordinate? previous = null;

                for (var ordinal = 0; ordinal < rows * columns; ordinal++)
                {
                    var coordinate = ExportTileTraversal.GetSerpentineCoordinate(
                        ordinal, columns, rows);
                    var expectedRow = ordinal / columns;
                    var offsetInRow = ordinal % columns;
                    var expectedColumn = (expectedRow & 1) == 0
                        ? offsetInRow
                        : columns - 1 - offsetInRow;

                    Assert.Equal(new ExportTileCoordinate(expectedRow, expectedColumn), coordinate);
                    Assert.False(seen[coordinate.Row, coordinate.Column]);
                    seen[coordinate.Row, coordinate.Column] = true;

                    if (previous is { } prior)
                    {
                        var manhattanDistance =
                            Math.Abs(coordinate.Row - prior.Row) +
                            Math.Abs(coordinate.Column - prior.Column);
                        Assert.Equal(1, manhattanDistance);
                    }

                    previous = coordinate;
                }

                for (var row = 0; row < rows; row++)
                {
                    for (var column = 0; column < columns; column++)
                    {
                        Assert.True(seen[row, column]);
                    }
                }
            }
        }
    }

    [Fact]
    public void GetSerpentineCoordinate_MaximumDimensions_UsesLongTileCountWithoutOverflow()
    {
        const int dimension = int.MaxValue;
        var finalOrdinal = (long)dimension * dimension - 1;

        Assert.Equal(
            new ExportTileCoordinate(dimension - 1, dimension - 1),
            ExportTileTraversal.GetSerpentineCoordinate(finalOrdinal, dimension, dimension));
        Assert.Equal(
            new ExportTileCoordinate(1, dimension - 1),
            ExportTileTraversal.GetSerpentineCoordinate(dimension, dimension, 2));
    }

    [Fact]
    public void GetSerpentineCoordinate_RejectsInvalidDimensionsAndOrdinals()
    {
        Assert.Equal(
            "columns",
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportTileTraversal.GetSerpentineCoordinate(0, 0, 1))
                .ParamName);
        Assert.Equal(
            "rows",
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportTileTraversal.GetSerpentineCoordinate(0, 1, 0))
                .ParamName);
        Assert.Equal(
            "visitOrdinal",
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportTileTraversal.GetSerpentineCoordinate(-1, 1, 1))
                .ParamName);
        Assert.Equal(
            "visitOrdinal",
            Assert.Throws<ArgumentOutOfRangeException>(() => ExportTileTraversal.GetSerpentineCoordinate(6, 3, 2))
                .ParamName);
    }
}