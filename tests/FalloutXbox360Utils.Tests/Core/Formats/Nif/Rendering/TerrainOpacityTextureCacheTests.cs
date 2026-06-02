using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     v3 Phase 2b — verifies <see cref="TerrainOpacityTextureCache.BuildOpacityGrid" />
///     rasterizes VTXT BlendEntries into the 17×17 R8 byte layout the GPU consumes. No GPU
///     dependency — the GPU upload step is intentionally separate from the grid-fill so this
///     test can run on the cross-platform TFM.
/// </summary>
public sealed class TerrainOpacityTextureCacheTests
{
    [Fact]
    public void BuildOpacityGrid_EmptyEntries_AllZeros()
    {
        var layer = MakeLayer();
        Span<byte> grid = stackalloc byte[TerrainOpacityTextureCache.GridSize];
        for (var i = 0; i < grid.Length; i++) grid[i] = 0xFF; // dirty fill — must be cleared

        TerrainOpacityTextureCache.BuildOpacityGrid(layer, grid);

        for (var i = 0; i < grid.Length; i++)
            Assert.Equal(0, grid[i]);
    }

    [Fact]
    public void BuildOpacityGrid_SingleFullOpacityAtOrigin_WritesTopLeftPixel()
    {
        var layer = MakeLayer(new LandTextureBlendEntry(0, 0, 0, 1.0f));
        Span<byte> grid = stackalloc byte[TerrainOpacityTextureCache.GridSize];

        TerrainOpacityTextureCache.BuildOpacityGrid(layer, grid);

        Assert.Equal(0xFF, grid[0]);
        // All other pixels remain 0.
        for (var i = 1; i < grid.Length; i++) Assert.Equal(0, grid[i]);
    }

    [Fact]
    public void BuildOpacityGrid_HalfOpacityAtFarCorner_WritesBottomRightPixel()
    {
        // Position 288 = 17 × 17 - 1 → grid (i=16, j=16) → byte at index 288.
        const ushort farCorner = 17 * 17 - 1;
        var layer = MakeLayer(new LandTextureBlendEntry(farCorner, 0, 0, 0.5f));
        Span<byte> grid = stackalloc byte[TerrainOpacityTextureCache.GridSize];

        TerrainOpacityTextureCache.BuildOpacityGrid(layer, grid);

        Assert.Equal((byte)(0.5f * 255f), grid[288]);
    }

    [Fact]
    public void BuildOpacityGrid_PositionAtOrPast289_SilentlyDropped()
    {
        // The parser already filters > 288, but BuildOpacityGrid hardens against a list that
        // somehow holds an out-of-range entry.
        var layer = MakeLayer(
            new LandTextureBlendEntry(289, 0, 0, 1.0f),
            new LandTextureBlendEntry(ushort.MaxValue, 0, 0, 1.0f));
        Span<byte> grid = stackalloc byte[TerrainOpacityTextureCache.GridSize];

        TerrainOpacityTextureCache.BuildOpacityGrid(layer, grid);

        for (var i = 0; i < grid.Length; i++) Assert.Equal(0, grid[i]);
    }

    [Fact]
    public void BuildOpacityGrid_OutOfRangeOpacity_ClampsToByteBounds()
    {
        var layer = MakeLayer(
            new LandTextureBlendEntry(0, 0, 0, 1.5f),     // clamps to 1.0 → 255
            new LandTextureBlendEntry(1, 0, 0, -0.5f));   // clamps to 0.0 → 0
        Span<byte> grid = stackalloc byte[TerrainOpacityTextureCache.GridSize];

        TerrainOpacityTextureCache.BuildOpacityGrid(layer, grid);

        Assert.Equal(0xFF, grid[0]);
        Assert.Equal(0x00, grid[1]);
    }

    [Fact]
    public void BuildOpacityGrid_PositionDecodes_jTimes17PlusI()
    {
        // Sanity check the position decode matches the grid layout. Position 17 = (i=0, j=1).
        var layer = MakeLayer(new LandTextureBlendEntry(17, 0, 0, 1.0f));
        Span<byte> grid = stackalloc byte[TerrainOpacityTextureCache.GridSize];

        TerrainOpacityTextureCache.BuildOpacityGrid(layer, grid);

        Assert.Equal(0xFF, grid[17]);  // row 1, column 0
        Assert.Equal(0, grid[16]);     // row 0, column 16 (unaffected)
    }

    [Fact]
    public void BuildOpacityGrid_ThrowsWhenDestinationTooSmall()
    {
        var layer = MakeLayer();
        var tooSmall = new byte[TerrainOpacityTextureCache.GridSize - 1];

        Assert.Throws<ArgumentException>(() =>
            TerrainOpacityTextureCache.BuildOpacityGrid(layer, tooSmall));
    }

    private static LandTextureLayer MakeLayer(params LandTextureBlendEntry[] entries)
    {
        return new LandTextureLayer
        {
            Kind = LandTextureLayerKind.Alpha,
            TextureFormId = 0x123,
            Quadrant = 0,
            PlatformFlag = 0,
            Layer = 0,
            BlendEntries = [.. entries]
        };
    }
}
