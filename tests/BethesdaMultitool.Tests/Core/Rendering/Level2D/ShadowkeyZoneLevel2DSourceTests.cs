using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Rendering.Level2D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Rendering.Level2D;

/// <summary>
///     The Shadowkey top-down view — the plan's "terrain layer plus rendered meshes, seen from
///     above" for a game with no heightmap.
///     <para>
///         The assertion that earns its place here is that the floor layer INTERPOLATES the four
///         corner heights instead of filling each cell flat. A flat fill produces a picture of the
///         same data that looks entirely reasonable and has every ramp, terrace and stair erased
///         from it, and it passes any check on dimensions, buffer length or colour range. So the
///         tests below look inside a single cell.
///     </para>
/// </summary>
public sealed class ShadowkeyZoneLevel2DSourceTests
{
    private const short OneTile = 256;

    private static ShadowkeyZoneMap Map(int width, int height, Func<int, bool> blocked, Func<int, int> prototypeOf)
    {
        var payload = new byte[ShadowkeyZoneMap.HeaderLength + (width * height * ShadowkeyZoneMap.CellLength)];
        "testzone"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(128), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(130), (ushort)height);
        for (var i = 0; i < width * height; i++)
        {
            var cell = payload.AsSpan(
                ShadowkeyZoneMap.HeaderLength + (i * ShadowkeyZoneMap.CellLength),
                ShadowkeyZoneMap.CellLength);
            cell[0] = blocked(i) ? ShadowkeyMapCell.BlockedFlag : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(cell[4..], (ushort)prototypeOf(i));
        }

        return ShadowkeyZoneMap.Parse(payload, "testzone.zmp");
    }

    private static ShadowkeyCellPrototypes Prototypes(params (short[] Floor, short[] Ceiling)[] records)
    {
        var payload = new byte[
            ShadowkeyCellPrototypes.HeaderLength + (records.Length * ShadowkeyCellPrototype.RecordLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)records.Length);
        for (var i = 0; i < records.Length; i++)
        {
            var record = payload.AsSpan(
                ShadowkeyCellPrototypes.HeaderLength + (i * ShadowkeyCellPrototype.RecordLength),
                ShadowkeyCellPrototype.RecordLength);
            for (var c = 0; c < ShadowkeyCellPrototype.CornerCount; c++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(record[(6 + (c * 2))..], records[i].Floor[c]);
                BinaryPrimitives.WriteInt16LittleEndian(record[(14 + (c * 2))..], records[i].Ceiling[c]);
            }

            record.Slice(22, ShadowkeyCellPrototype.SurfaceSlotCount).Fill(ShadowkeyCellPrototype.NoSurface);
        }

        return ShadowkeyCellPrototypes.Parse(payload, "testzone.zcp");
    }

    private static ShadowkeyCellPrototypes FlatRoom() =>
        Prototypes((new short[] { 0, 0, 0, 0 }, [OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4]));

    /// <summary>The RGB at a pixel of a render.</summary>
    private static (byte R, byte G, byte B) Pixel(Level2DRender render, int x, int y)
    {
        var offset = ((y * render.Width) + x) * 4;
        return (render.Rgba[offset], render.Rgba[offset + 1], render.Rgba[offset + 2]);
    }

    // ---------------------------------------------------------------- the interpolation claim

    /// <summary>
    ///     ⚑ A sloped cell must not render flat. The four corners here run 0, 1, 2 and 3 tiles, so a
    ///     correct render shows a gradient inside the single cell and a flat fill shows one colour.
    /// </summary>
    [Fact]
    public void ASlopedCellRendersAGradientNotAFlatFill()
    {
        var prototypes = Prototypes((
            new short[] { 0, OneTile, OneTile * 2, OneTile * 3 },
            [OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4]));
        var source = ShadowkeyZoneLevel2DSource.ForZone(
            Map(1, 1, static _ => false, static _ => 0), prototypes, scale: 8);

        var render = source.Render(Level2DLayer.Floor);
        Assert.NotNull(render);

        var distinct = new HashSet<(byte, byte, byte)>();
        for (var y = 0; y < render!.Value.Height; y++)
        {
            for (var x = 0; x < render.Value.Width; x++)
            {
                distinct.Add(Pixel(render.Value, x, y));
            }
        }

        Assert.True(distinct.Count > 4, $"A sloped cell rendered only {distinct.Count} distinct colours.");
    }

    /// <summary>
    ///     ⚑ And it must interpolate the corners the MEASURED way round. Corner slot 3 is (x, y),
    ///     the top-left pixel of the cell, and slot 1 is (x+1, y+1), the bottom-right. Raising only
    ///     slot 3 must therefore brighten the top-left corner and leave the bottom-right dark; a
    ///     rotated or mirrored reading brightens the wrong corner while still producing a perfectly
    ///     smooth gradient.
    /// </summary>
    [Fact]
    public void TheGradientFollowsTheMeasuredCornerOrder()
    {
        var prototypes = Prototypes((
            new short[] { 0, 0, 0, OneTile * 4 },
            [OneTile * 8, OneTile * 8, OneTile * 8, OneTile * 8]));
        var source = ShadowkeyZoneLevel2DSource.ForZone(
            Map(1, 1, static _ => false, static _ => 0), prototypes, scale: 8);

        var render = source.Render(Level2DLayer.Floor)!.Value;
        var last = render.Width - 1;

        var topLeft = Pixel(render, 0, 0);
        var bottomRight = Pixel(render, last, last);

        Assert.True(
            topLeft.R > bottomRight.R,
            $"Slot 3 is (x, y), so raising it must brighten the top-left; got {topLeft.R} vs {bottomRight.R}.");
        Assert.Equal(ShadowkeyZoneSceneBuilder.CornerOffset(3), (0, 0));
    }

    // ---------------------------------------------------------------- layers

    [Fact]
    public void TheThreeGeometryLayersAreAlwaysOffered()
    {
        var source = ShadowkeyZoneLevel2DSource.ForZone(
            Map(2, 2, static _ => false, static _ => 0), FlatRoom());

        Assert.Equal(
            new[] { Level2DLayer.Floor, Level2DLayer.Walls, Level2DLayer.Ceiling },
            source.Layers);
        Assert.Null(source.Render(Level2DLayer.Overlay));
    }

    /// <summary>The wall layer must SEPARATE solid from open — that is the whole of its job.</summary>
    [Fact]
    public void TheWallLayerSeparatesSolidFromOpen()
    {
        var source = ShadowkeyZoneLevel2DSource.ForZone(
            Map(2, 1, static i => i == 1, static _ => 0), FlatRoom(), scale: 4);

        var render = source.Render(Level2DLayer.Walls)!.Value;

        Assert.NotEqual(Pixel(render, 0, 0), Pixel(render, 5, 0));
    }

    [Fact]
    public void RenderDimensionsFollowTheGridAndScale()
    {
        var source = ShadowkeyZoneLevel2DSource.ForZone(
            Map(6, 3, static _ => false, static _ => 0), FlatRoom(), scale: 5);

        var render = source.Render(Level2DLayer.Floor)!.Value;

        Assert.Equal(30, render.Width);
        Assert.Equal(15, render.Height);
        Assert.Equal(30 * 15 * 4, render.Rgba.Length);
        Assert.All(Enumerable.Range(0, 30 * 15), i => Assert.Equal((byte)255, render.Rgba[(i * 4) + 3]));
    }

    // ---------------------------------------------------------------- clamping and ranging

    /// <summary>
    ///     A retail zone is 128 tiles square, so the scale has to be capped or a generous
    ///     pixels-per-tile turns into a 64 MB CPU surface. Clamped rather than rejected.
    /// </summary>
    [Theory]
    [InlineData(4, 4)]
    [InlineData(32, 32)]
    [InlineData(64, 32)]
    [InlineData(0, 1)]
    [InlineData(-8, 1)]
    public void ScaleIsClampedSoTheRasterStaysBounded(int requested, int expected)
    {
        var map = Map(128, 128, static _ => false, static _ => 0);

        Assert.Equal(expected, ShadowkeyZoneLevel2DSource.ClampScale(map, requested));
        Assert.True(128 * expected <= ShadowkeyZoneLevel2DSource.MaxRasterEdge);
    }

    /// <summary>
    ///     ⚠ The height ramp is taken from OPEN cells only. A blocked cell carries whatever filler
    ///     the editor left in its prototype, and letting that set the range compresses every real
    ///     slope into a couple of shades — the picture still renders, it just goes flat.
    /// </summary>
    [Fact]
    public void BlockedCellsDoNotFlattenTheHeightRamp()
    {
        var open = (new short[] { 0, 0, OneTile, OneTile }, new short[] { OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4 });
        var filler = (new short[] { 30000, 30000, 30000, 30000 }, new short[] { 30000, 30000, 30000, 30000 });

        var withoutFiller = ShadowkeyZoneLevel2DSource.ForZone(
            Map(2, 1, static i => i == 1, static _ => 0), Prototypes(open, filler), scale: 8);
        var withFiller = ShadowkeyZoneLevel2DSource.ForZone(
            Map(2, 1, static i => i == 1, static i => i), Prototypes(open, filler), scale: 8);

        var a = withoutFiller.Render(Level2DLayer.Floor)!.Value;
        var b = withFiller.Render(Level2DLayer.Floor)!.Value;

        // The open cell is identical in both, because the blocked neighbour never reaches the ramp.
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                Assert.Equal(Pixel(a, x, y), Pixel(b, x, y));
            }
        }
    }

    // ---------------------------------------------------------------- overlay

    /// <summary>
    ///     Placements are already in tiles — the same unit the grid is drawn in — so the overlay
    ///     needs no conversion, and a dot must land on the tile the record names.
    /// </summary>
    [Fact]
    public void TheOverlayMarksPlacementsAtTheirOwnTiles()
    {
        var placement = new ShadowkeyEntity(
            RawX: 3 * 256, RawY: 1 * 256, RawZ: 0, Angle0: 0, Angle1: 0, Angle2: 0,
            RawScale: 256, EntityId: 7, Name: "crate", Script: "crate");
        var source = ShadowkeyZoneLevel2DSource.ForZone(
            Map(6, 3, static _ => false, static _ => 0), FlatRoom(),
            new ShadowkeyEntityList([placement], HasNames: true), scale: 6);

        Assert.Contains(Level2DLayer.Overlay, source.Layers);
        var render = source.Render(Level2DLayer.Overlay)!.Value;

        Assert.Equal((byte)255, Pixel(render, 3 * 6, 1 * 6).R);
        Assert.NotEqual((byte)255, Pixel(render, 0, 0).R);
    }

    [Fact]
    public void ForZone_RejectsNullInputsAndBadPrototypeIndices()
    {
        Assert.Throws<ArgumentNullException>(
            () => ShadowkeyZoneLevel2DSource.ForZone(null!, FlatRoom()));
        Assert.Throws<ArgumentNullException>(
            () => ShadowkeyZoneLevel2DSource.ForZone(Map(1, 1, static _ => false, static _ => 0), null!));
        Assert.Throws<InvalidDataException>(
            () => ShadowkeyZoneLevel2DSource.ForZone(Map(1, 1, static _ => false, static _ => 9), FlatRoom()));
    }

    /// <summary>A Shadowkey zone grid opens in the 2D view by default.</summary>
    [Fact]
    public void TheViewPolicyOffersZoneGrids()
    {
        Assert.True(Level2DViewPolicy.Supports("azra.zmp"));
        Assert.True(Level2DViewPolicy.DefaultsToTwoDimensional("azra.zmp"));
    }
}
