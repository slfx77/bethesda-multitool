using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Travels.OblivionMobile;
using BethesdaMultitool.Core.Rendering.Level2D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Rendering.Level2D;

/// <summary>
///     The Oblivion Mobile tile compositor — the caller
///     <see cref="OblivionMobileIsometric" /> was written for and never had.
///     <para>
///         The test that matters is <see cref="TheLaterCellInTheEngineWalkWins" />. The engine paints
///         i-outer/j-inner and lets tall tiles overlap as a consequence; it does NOT depth-sort. Cells
///         (0,1) and (1,0) sit at the same screen depth, so they are the pair that separates the two
///         readings — under the engine's walk (1,0) is painted second and wins, and under any
///         j-major or depth-ordered walk (0,1) does. Every other property here would hold under
///         either.
///     </para>
/// </summary>
public sealed class OblivionMobileLevel2DSourceTests
{
    // ---------------------------------------------------------------- fixture builders

    private static byte[] Run(int count, int value)
    {
        return [OblivionMobileTileMap.RunMarker, (byte)count, (byte)value];
    }

    private static byte[] Literals(params int[] values)
    {
        return [.. values.Select(v => (byte)v)];
    }

    private static byte[] Jtm(int width, int height, params byte[][] parts)
    {
        return [(byte)width, (byte)height, .. parts.SelectMany(p => p)];
    }

    private static byte[] Attributes(params (int Bit, int[] Bytes)[] fields)
    {
        var mask = 0;
        var payload = new List<byte>();
        foreach (var (bit, value) in fields.OrderByDescending(f => f.Bit))
        {
            mask |= 1 << bit;
            payload.AddRange(value.Select(b => (byte)b));
        }

        return [(byte)(mask >> 8), (byte)(mask & 0xFF), .. payload];
    }

    /// <summary>An image-attribute block describing one whole-sheet tile.</summary>
    private static byte[] Tile(int id, int sx, int sy, int w, int h, int dx = 0, int dy = 0, int mirror = 0)
    {
        return Attributes(
            (OblivionMobileAttributes.IdBit, [id]),
            (OblivionMobileAttributes.SourceXBit, [sx >> 8, sx & 0xFF]),
            (OblivionMobileAttributes.SourceYBit, [sy >> 8, sy & 0xFF]),
            (OblivionMobileAttributes.WidthBit, [w]),
            (OblivionMobileAttributes.HeightBit, [h]),
            (OblivionMobileAttributes.OffsetXBit, [dx & 0xFF]),
            (OblivionMobileAttributes.OffsetYBit, [dy & 0xFF]),
            (OblivionMobileAttributes.MirrorBit, [mirror]));
    }

    private static byte[] Entry(int defaultId, string path, byte[] imageAttributes)
    {
        var name = Encoding.ASCII.GetBytes(path);
        return [(byte)defaultId, (byte)name.Length, .. name, .. imageAttributes, 0, 0];
    }

    private static byte[] Cml(string prefix, params byte[][] entries)
    {
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        return [(byte)prefixBytes.Length, .. prefixBytes, .. entries.SelectMany(e => e)];
    }

    /// <summary>A solid RGBA PNG, so a blit's colour identifies which sheet it came from.</summary>
    private static byte[] SolidPng(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = a;
        }

        return PngWriter.EncodeRgba(rgba, width, height);
    }

    private static (byte R, byte G, byte B, byte A) Pixel(Level2DRender render, int x, int y)
    {
        var offset = (y * render.Width + x) * 4;
        return (render.Rgba[offset], render.Rgba[offset + 1], render.Rgba[offset + 2], render.Rgba[offset + 3]);
    }

    // ---------------------------------------------------------------- the draw-order claim

    /// <summary>
    ///     ⚑ Cells (0,1) and (1,0) are the discriminating pair: same screen depth, opposite order
    ///     under the two candidate walks. The engine paints i-outer/j-inner ascending, so (1,0)
    ///     lands second and owns the overlap. A depth sort or a j-major walk gives (0,1) instead,
    ///     and every wall tile in the game then occludes the wrong neighbour.
    /// </summary>
    [Fact]
    public void TheLaterCellInTheEngineWalkWins()
    {
        // 2x2 map: passability all open, then one tile layer placing tile 1 at (0,1) and tile 2 at
        // (1,0). Cells are stored j-major, so the layer reads (i,j) = (0,0) (1,0) (0,1) (1,1).
        var jtm = Jtm(2, 2, Run(4, 1), Literals(0, 2, 1, 0));

        // Tile 1 is 64 wide so it reaches across cell (1,0)'s diamond; tile 2 is an ordinary 32.
        var cml = Cml(
            "gfx/",
            Entry(1, "wide.png", Tile(1, 0, 0, 64, 16)),
            Entry(2, "narrow.png", Tile(2, 0, 0, 32, 16)));

        var source = OblivionMobileLevel2DSource.ForMap(
            OblivionMobileTileMap.Parse(jtm, "l01.jtm"),
            OblivionMobileAtlas.Parse(cml, "l01.cml"),
            path => path.Contains("wide", StringComparison.Ordinal)
                ? SolidPng(64, 16, 200, 20, 20)
                : SolidPng(32, 16, 20, 20, 200));

        var render = source.Render(Level2DLayer.Overlay);
        Assert.NotNull(render);

        // Cell (0,1) spans x -32..32 at y 8; cell (1,0) spans x 0..32 at the same y. Sample inside
        // the overlap, in canvas coordinates (the canvas starts at the leftmost placed pixel).
        var sample = Pixel(render.Value, 48, 8);
        Assert.Equal((byte)20, sample.R);
        Assert.Equal((byte)200, sample.B);
    }

    // ---------------------------------------------------------------- compositing

    /// <summary>
    ///     The canvas is measured from the placed tiles, not from the grid: a wall tile's negative
    ///     dy lifts it above row 0, and a grid-sized canvas would clip it away silently.
    /// </summary>
    [Fact]
    public void TheCanvasGrowsToCoverATileThatRisesAboveTheGrid()
    {
        var jtm = Jtm(1, 1, Run(1, 1), Literals(1));
        var cml = Cml("gfx/", Entry(1, "wall.png", Tile(1, 0, 0, 32, 48, dy: -32)));

        var source = OblivionMobileLevel2DSource.ForMap(
            OblivionMobileTileMap.Parse(jtm, "l.jtm"),
            OblivionMobileAtlas.Parse(cml, "l.cml"),
            _ => SolidPng(32, 48, 90, 90, 90));

        var render = source.Render(Level2DLayer.Overlay)!.Value;

        Assert.Equal(32, render.Width);
        Assert.Equal(48, render.Height);
    }

    /// <summary>
    ///     A transparent texel must leave what is underneath alone. Tiles overlap by design, so a
    ///     straight copy punches holes in the tile below wherever the one above is see-through.
    /// </summary>
    [Fact]
    public void ATransparentTexelDoesNotPunchThroughTheTileBelow()
    {
        var jtm = Jtm(1, 1, Run(1, 1), Literals(1), Literals(2));
        var cml = Cml(
            "gfx/",
            Entry(1, "under.png", Tile(1, 0, 0, 32, 16)),
            Entry(2, "clear.png", Tile(2, 0, 0, 32, 16)));

        var source = OblivionMobileLevel2DSource.ForMap(
            OblivionMobileTileMap.Parse(jtm, "l.jtm"),
            OblivionMobileAtlas.Parse(cml, "l.cml"),
            path => path.Contains("under", StringComparison.Ordinal)
                ? SolidPng(32, 16, 10, 220, 10)
                : SolidPng(32, 16, 250, 0, 0, 0));

        // Floor takes every layer but the last, so the "under" tile is the one that draws.
        var render = source.Render(Level2DLayer.Floor)!.Value;

        Assert.Equal((byte)220, Pixel(render, 16, 8).G);
    }

    /// <summary>The mirror flag flips the source column, not the destination.</summary>
    [Fact]
    public void TheMirrorFlagReadsTheSourceRowBackwards()
    {
        var jtm = Jtm(1, 1, Run(1, 1), Literals(1));
        var cml = Cml("gfx/", Entry(1, "half.png", Tile(1, 0, 0, 4, 1, mirror: 1)));

        // A 4x1 sheet whose leftmost texel is red and the rest blue.
        var rgba = new byte[4 * 4];
        rgba[0] = 255;
        rgba[3] = 255;
        for (var x = 1; x < 4; x++)
        {
            rgba[x * 4 + 2] = 255;
            rgba[x * 4 + 3] = 255;
        }

        var source = OblivionMobileLevel2DSource.ForMap(
            OblivionMobileTileMap.Parse(jtm, "l.jtm"),
            OblivionMobileAtlas.Parse(cml, "l.cml"),
            _ => PngWriter.EncodeRgba(rgba, 4, 1));

        var render = source.Render(Level2DLayer.Overlay)!.Value;

        // Mirrored, the red source texel lands on the RIGHT of the destination.
        Assert.Equal((byte)255, Pixel(render, 3, 0).R);
        Assert.Equal((byte)255, Pixel(render, 0, 0).B);
    }

    /// <summary>
    ///     A sheet that will not load leaves a hole rather than failing the level — a browser
    ///     opening a partially-extracted install should still show what it has.
    /// </summary>
    [Fact]
    public void AMissingSheetLeavesAHoleRatherThanThrowing()
    {
        var jtm = Jtm(1, 1, Run(1, 1), Literals(1));
        var cml = Cml("gfx/", Entry(1, "gone.png", Tile(1, 0, 0, 32, 16)));

        var source = OblivionMobileLevel2DSource.ForMap(
            OblivionMobileTileMap.Parse(jtm, "l.jtm"),
            OblivionMobileAtlas.Parse(cml, "l.cml"),
            _ => null);

        Assert.Null(source.Render(Level2DLayer.Overlay));
    }

    /// <summary>A sheet is decoded once however many cells reference it.</summary>
    [Fact]
    public void ASheetIsDecodedOncePerRender()
    {
        var jtm = Jtm(4, 4, Run(16, 1), Run(16, 1));
        var cml = Cml("gfx/", Entry(1, "one.png", Tile(1, 0, 0, 32, 16)));

        var loads = 0;
        var source = OblivionMobileLevel2DSource.ForMap(
            OblivionMobileTileMap.Parse(jtm, "l.jtm"),
            OblivionMobileAtlas.Parse(cml, "l.cml"),
            _ =>
            {
                loads++;
                return SolidPng(32, 16, 40, 40, 40);
            });

        source.Render(Level2DLayer.Overlay);

        Assert.Equal(1, loads);
    }

    // ---------------------------------------------------------------- passability

    /// <summary>The passability layer must separate blocked from open — that is its whole job.</summary>
    [Fact]
    public void ThePassabilityLayerSeparatesBlockedFromOpen()
    {
        var jtm = Jtm(2, 1, Literals(0, 1), Literals(0, 0));
        var cml = Cml("gfx/", Entry(1, "t.png", Tile(1, 0, 0, 32, 16)));

        var source = OblivionMobileLevel2DSource.ForMap(
            OblivionMobileTileMap.Parse(jtm, "l.jtm"),
            OblivionMobileAtlas.Parse(cml, "l.cml"),
            _ => SolidPng(32, 16, 40, 40, 40));

        var render = source.Render(Level2DLayer.Walls);
        Assert.NotNull(render);

        var colours = new HashSet<(byte, byte, byte, byte)>();
        for (var y = 0; y < render.Value.Height; y++)
        {
            for (var x = 0; x < render.Value.Width; x++)
            {
                colours.Add(Pixel(render.Value, x, y));
            }
        }

        // Blocked, open, and the untouched background between the diamonds.
        Assert.True(colours.Count >= 3, $"The passability layer drew only {colours.Count} colours.");
    }

    [Fact]
    public void AllThreeLayersAreOffered()
    {
        var jtm = Jtm(1, 1, Run(1, 1), Literals(1));
        var cml = Cml("gfx/", Entry(1, "t.png", Tile(1, 0, 0, 32, 16)));

        var source = OblivionMobileLevel2DSource.ForMap(
            OblivionMobileTileMap.Parse(jtm, "l.jtm"),
            OblivionMobileAtlas.Parse(cml, "l.cml"),
            _ => SolidPng(32, 16, 40, 40, 40));

        Assert.Equal(
            new[] { Level2DLayer.Floor, Level2DLayer.Overlay, Level2DLayer.Walls },
            source.Layers);
    }

    [Fact]
    public void ForMap_RejectsNullInputs()
    {
        var map = OblivionMobileTileMap.Parse(Jtm(1, 1, Run(1, 1), Literals(0)), "l.jtm");
        var atlas = OblivionMobileAtlas.Parse(Cml("gfx/"), "l.cml");

        Assert.Throws<ArgumentNullException>(() => OblivionMobileLevel2DSource.ForMap(null!, atlas, _ => null));
        Assert.Throws<ArgumentNullException>(() => OblivionMobileLevel2DSource.ForMap(map, null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => OblivionMobileLevel2DSource.ForMap(map, atlas, null!));
    }
}