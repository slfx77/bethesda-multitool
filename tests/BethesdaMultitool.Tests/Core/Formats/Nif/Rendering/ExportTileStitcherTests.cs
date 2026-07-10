using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Locks the pixel placement of <see cref="ExportTileStitcher" /> — the non-tiled 3D export's
///     internal tile-to-single-PNG composition. Synthetic distinct-color tiles, no GPU.
/// </summary>
public sealed class ExportTileStitcherTests
{
    private static byte[] SolidTile(int width, int height, byte r, byte g, byte b, byte a)
    {
        var tile = new byte[width * height * 4];
        for (var i = 0; i < tile.Length; i += 4)
        {
            tile[i] = r;
            tile[i + 1] = g;
            tile[i + 2] = b;
            tile[i + 3] = a;
        }
        return tile;
    }

    private static (byte R, byte G, byte B, byte A) PixelAt(byte[] image, int imageWidth, int x, int y)
    {
        var i = ((y * imageWidth) + x) * 4;
        return (image[i], image[i + 1], image[i + 2], image[i + 3]);
    }

    [Fact]
    public void CopyTile_2x2Grid_PlacesEachTileInItsQuadrant()
    {
        const int tileW = 3, tileH = 2, cols = 2, rows = 2;
        const int imageW = cols * tileW, imageH = rows * tileH;
        var image = new byte[imageW * imageH * 4];

        // Four distinct colors; row counts top→bottom like image rows + the render loop.
        ExportTileStitcher.CopyTile(SolidTile(tileW, tileH, 10, 0, 0, 255), tileW, tileH, image, imageW, imageH, 0, 0);
        ExportTileStitcher.CopyTile(SolidTile(tileW, tileH, 0, 20, 0, 255), tileW, tileH, image, imageW, imageH, 1, 0);
        ExportTileStitcher.CopyTile(SolidTile(tileW, tileH, 0, 0, 30, 255), tileW, tileH, image, imageW, imageH, 0, 1);
        ExportTileStitcher.CopyTile(SolidTile(tileW, tileH, 40, 40, 0, 255), tileW, tileH, image, imageW, imageH, 1, 1);

        // One probe inside each quadrant + the exact corner pixels of each boundary.
        Assert.Equal(((byte)10, (byte)0, (byte)0, (byte)255), PixelAt(image, imageW, 0, 0));
        Assert.Equal(((byte)10, (byte)0, (byte)0, (byte)255), PixelAt(image, imageW, tileW - 1, tileH - 1));
        Assert.Equal(((byte)0, (byte)20, (byte)0, (byte)255), PixelAt(image, imageW, tileW, 0));
        Assert.Equal(((byte)0, (byte)20, (byte)0, (byte)255), PixelAt(image, imageW, imageW - 1, tileH - 1));
        Assert.Equal(((byte)0, (byte)0, (byte)30, (byte)255), PixelAt(image, imageW, 0, tileH));
        Assert.Equal(((byte)0, (byte)0, (byte)30, (byte)255), PixelAt(image, imageW, tileW - 1, imageH - 1));
        Assert.Equal(((byte)40, (byte)40, (byte)0, (byte)255), PixelAt(image, imageW, tileW, tileH));
        Assert.Equal(((byte)40, (byte)40, (byte)0, (byte)255), PixelAt(image, imageW, imageW - 1, imageH - 1));
    }

    [Fact]
    public void CopyTile_PreservesPerPixelContentRowOrder()
    {
        // A tile whose pixels encode (x, y) must land with rows intact (a stride bug would shear it).
        const int tileW = 4, tileH = 3;
        var tile = new byte[tileW * tileH * 4];
        for (var y = 0; y < tileH; y++)
        {
            for (var x = 0; x < tileW; x++)
            {
                var i = ((y * tileW) + x) * 4;
                tile[i] = (byte)x;
                tile[i + 1] = (byte)y;
                tile[i + 2] = 200;
                tile[i + 3] = 255;
            }
        }

        const int imageW = 8, imageH = 6; // 2×2 grid; place at col 1, row 1 (bottom-right slot)
        var image = new byte[imageW * imageH * 4];
        ExportTileStitcher.CopyTile(tile, tileW, tileH, image, imageW, imageH, 1, 1);

        for (var y = 0; y < tileH; y++)
        {
            for (var x = 0; x < tileW; x++)
            {
                Assert.Equal(
                    ((byte)x, (byte)y, (byte)200, (byte)255),
                    PixelAt(image, imageW, tileW + x, tileH + y));
            }
        }
        // Slots outside the copied tile stay untouched (pre-cleared transparent).
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), PixelAt(image, imageW, 0, 0));
    }

    [Fact]
    public void CopyTile_OutOfBoundsSlot_Throws()
    {
        var image = new byte[4 * 4 * 4];
        var tile = SolidTile(2, 2, 1, 2, 3, 4);
        Assert.Throws<ArgumentException>(
            () => ExportTileStitcher.CopyTile(tile, 2, 2, image, 4, 4, tileCol: 2, tileRow: 0));
    }
}
