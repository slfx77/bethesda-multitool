using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class PlacedNifWaterCellResolverTests
{
    [Fact]
    public void Resolve_UsesMorrowindCellSizeInsteadOfFalloutGrid()
    {
        var cells = Cells(((0, -1), Exterior(0x1234)));

        var resolved = PlacedNifWaterCellResolver.Resolve(
            cells,
            new Vector2(5000f, -5000f),
            8192f);

        Assert.Equal(0x1234u, resolved);
    }

    [Theory]
    [InlineData(-0.001f, -8192f, -1, -1)]
    [InlineData(-8192f, -8192.001f, -1, -2)]
    public void Resolve_NegativeCoordinatesUseMathematicalFloor(
        float x,
        float y,
        int expectedGridX,
        int expectedGridY)
    {
        var cells = Cells(((expectedGridX, expectedGridY), Exterior(0x4567)));

        var resolved = PlacedNifWaterCellResolver.Resolve(cells, new Vector2(x, y), 8192f);

        Assert.Equal(0x4567u, resolved);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(-8192f)]
    public void Resolve_InvalidCellSizeFallsBackTo4096(float invalidCellSize)
    {
        var cells = Cells(((1, -2), Exterior(0x89AB)));

        var resolved = PlacedNifWaterCellResolver.Resolve(
            cells,
            new Vector2(5000f, -5000f),
            invalidCellSize);

        Assert.Equal(0x89ABu, resolved);
    }

    [Fact]
    public void Resolve_SoleInteriorUsesItsWaterTypeWithoutSyntheticGridGuess()
    {
        var cells = Cells(((37, -42), new CellRecord
        {
            Flags = 0x01,
            WaterFormId = 0xCDEF
        }));

        var resolved = PlacedNifWaterCellResolver.Resolve(
            cells,
            new Vector2(-900_000f, 700_000f),
            8192f);

        Assert.Equal(0xCDEFu, resolved);
    }

    private static CellRecord Exterior(uint waterFormId) => new() { WaterFormId = waterFormId };

    private static Dictionary<(int gx, int gy), CellRecord> Cells(
        ((int gx, int gy) Key, CellRecord Cell) item) =>
        new() { [item.Key] = item.Cell };
}
