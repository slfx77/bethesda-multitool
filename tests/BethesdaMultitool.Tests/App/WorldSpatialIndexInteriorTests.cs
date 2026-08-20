using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class WorldSpatialIndexInteriorTests
{
    [Fact]
    public void SyntheticInteriorKey_IsPlacedObjectCentroidTile()
    {
        var cell = new CellRecord
        {
            FormId = 0x100,
            PlacedObjects =
            [
                new PlacedReference { FormId = 0x201, X = 5000f, Y = 9000f },
                new PlacedReference { FormId = 0x202, X = 7000f, Y = 11000f }
            ]
        };

        var key = WorldSpatialIndex.SyntheticInteriorKey(cell);

        // Centroid (6000, 10000) → floor(/CellSize) in the same game-Y grid convention as
        // exterior GridX/GridY, so the cylinder cell enumeration yields the cell.
        var expectedX = (int)MathF.Floor(6000f / WorldGridConstants.CellSize);
        var expectedY = (int)MathF.Floor(10000f / WorldGridConstants.CellSize);
        Assert.Equal((expectedX, expectedY), key);
    }

    [Fact]
    public void SyntheticInteriorKey_NoPlacedObjectsReturnsOrigin()
    {
        var cell = new CellRecord { FormId = 0x100, PlacedObjects = [] };
        Assert.Equal((0, 0), WorldSpatialIndex.SyntheticInteriorKey(cell));
    }

    [Fact]
    public void SyntheticInteriorKey_SkipsNonFinitePlacements()
    {
        var cell = new CellRecord
        {
            FormId = 0x100,
            PlacedObjects =
            [
                new PlacedReference { FormId = 0x201, X = float.NaN, Y = 0f },
                new PlacedReference { FormId = 0x202, X = 8192f, Y = 8192f }
            ]
        };

        var key = WorldSpatialIndex.SyntheticInteriorKey(cell);

        // Only the finite placement counts → centroid == that placement.
        var expected = (
            (int)MathF.Floor(8192f / WorldGridConstants.CellSize),
            (int)MathF.Floor(8192f / WorldGridConstants.CellSize));
        Assert.Equal(expected, key);
    }
}