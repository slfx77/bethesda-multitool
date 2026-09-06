using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.OblivionMobile;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for the Oblivion Mobile level pair — <see cref="OblivionMobileTileMap" />
///     (<c>.jtm</c>), <see cref="OblivionMobileAtlas" /> (<c>.cml</c>) and the shared
///     <see cref="OblivionMobileIsometric" /> projection. Both formats are big-endian J2ME
///     resources whose length is carried entirely by counts and EOF, so the interesting cases are
///     the boundaries: a run that spans a row, a stream that stops mid-layer, an attribute mask
///     that claims a field the bytes do not hold.
/// </summary>
public sealed class OblivionMobileLevelTests
{
    // ---------------------------------------------------------------- .jtm builders

    /// <summary>A run token: <c>0xFF cnt val</c>.</summary>
    private static byte[] Run(int count, int value)
    {
        return [OblivionMobileTileMap.RunMarker, (byte)count, (byte)value];
    }

    /// <summary>A whole layer as literals — never 0xFF, which the format cannot encode literally.</summary>
    private static byte[] Literals(params int[] values)
    {
        return [.. values.Select(v => (byte)v)];
    }

    private static byte[] Jtm(int width, int height, params byte[][] streamParts)
    {
        return [(byte)width, (byte)height, .. streamParts.SelectMany(p => p)];
    }

    // ---------------------------------------------------------------- .cml builders

    private static byte[] Attributes(params (int Bit, int[] Bytes)[] fields)
    {
        var mask = 0;
        var payload = new List<byte>();
        // The reader expects fields in bit order 9..0; feed them the same way.
        foreach (var (bit, value) in fields.OrderByDescending(f => f.Bit))
        {
            mask |= 1 << bit;
            payload.AddRange(value.Select(b => (byte)b));
        }

        return [(byte)(mask >> 8), (byte)(mask & 0xFF), .. payload];
    }

    private static byte[] RawMask(int mask)
    {
        return [(byte)(mask >> 8), (byte)(mask & 0xFF)];
    }

    private static byte[] Entry(int defaultId, string path, byte[] imageAttributes, params byte[][] sprites)
    {
        var name = Encoding.ASCII.GetBytes(path);
        return
        [
            (byte)defaultId,
            (byte)name.Length,
            .. name,
            .. imageAttributes,
            0, // pairCount — empty on 48/48 retail entries
            (byte)sprites.Length,
            .. sprites.SelectMany(s => s),
        ];
    }

    private static byte[] Sprite(byte[] spriteAttributes, params byte[][] frames)
    {
        return [.. spriteAttributes, (byte)frames.Length, .. frames.SelectMany(f => f)];
    }

    /// <summary>The mask 0x204 sprite header the retail atlases use: id plus loop.</summary>
    private static byte[] SpriteHeader(int id, int loop = 0)
    {
        return Attributes(
            (OblivionMobileAttributes.IdBit, [id]),
            (OblivionMobileAttributes.LoopBit, [loop]));
    }

    private static byte[] Cml(string prefix, params byte[][] entries)
    {
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        return [(byte)prefixBytes.Length, .. prefixBytes, .. entries.SelectMany(e => e)];
    }

    // ================================================================ .jtm happy path

    [Fact]
    public void Parse_DecodesRunsAndLiteralsIntoTwoLayers()
    {
        // 2x2: passability all-open as one run, then a tile layer of four literals.
        var bytes = Jtm(2, 2, Run(4, 0), Literals(10, 11, 12, 13));

        var map = OblivionMobileTileMap.Parse(bytes, "synthetic.jtm");

        Assert.Equal(2, map.Width);
        Assert.Equal(2, map.Height);
        Assert.Equal(4, map.CellCount);
        Assert.Equal(2, map.LayerCount);
        Assert.Single(map.TileLayers);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, map.Passability);
        Assert.Equal(new byte[] { 10, 11, 12, 13 }, map.TileLayers[0]);
    }

    [Fact]
    public void Parse_CellsAreJMajorSoIRunsFastest()
    {
        // 3x2. Tile layer, row j=0 then row j=1.
        var bytes = Jtm(3, 2, Run(6, 0), Literals(1, 2, 3, 4, 5, 6));

        var map = OblivionMobileTileMap.Parse(bytes, "jmajor.jtm");

        // stream[j * W + i]: (2, 0) is index 2, (0, 1) is index 3.
        Assert.Equal(1, map.Cell(1, 0, 0));
        Assert.Equal(3, map.Cell(1, 2, 0));
        Assert.Equal(4, map.Cell(1, 0, 1));
        Assert.Equal(6, map.Cell(1, 2, 1));
        // The transposed reading would put 4 at (1, 0) — pin that it does not.
        Assert.NotEqual(4, map.Cell(1, 1, 0));
    }

    [Fact]
    public void Parse_RunSpansARowBoundaryWithinItsLayer()
    {
        // 3x2 tile layer: one literal, then a 5-cell run that crosses from row 0 into row 1.
        var bytes = Jtm(3, 2, Run(6, 0), [.. Literals(7), .. Run(5, 9)]);

        var map = OblivionMobileTileMap.Parse(bytes, "spanrow.jtm");

        Assert.Equal(new byte[] { 7, 9, 9, 9, 9, 9 }, map.TileLayers[0]);
        Assert.Equal(9, map.Cell(1, 2, 0));
        Assert.Equal(9, map.Cell(1, 0, 1));
    }

    [Fact]
    public void Parse_DecodesLayersUntilExactlyEof()
    {
        // 1x1 with five layers — the layer count is not stored anywhere.
        var bytes = Jtm(1, 1, Literals(0, 1, 2, 3, 4));

        var map = OblivionMobileTileMap.Parse(bytes, "fivelayer.jtm");

        Assert.Equal(5, map.LayerCount);
        Assert.Equal(4, map.TileLayers.Count);
        Assert.Equal(4, map.ForegroundLayer[0]);
    }

    [Fact]
    public void Parse_MatchesTheSpecWorkedExampleForL14()
    {
        // The first bytes of retail l14_1.jtm: 0f 0f | ff 14 00 ff 0a 01 ff 05 00 ...
        // Row j=0 is 15 open cells; row j=1 is 5 open then 10 blocked.
        var head = new byte[] { 0x0F, 0x0F };
        var stream = new List<byte>();
        stream.AddRange(Run(0x14, 0x00));
        stream.AddRange(Run(0x0A, 0x01));
        stream.AddRange(Run(0x05, 0x00));
        // Pad out the remaining passability cells plus one whole tile layer.
        var remaining = (15 * 15) - 0x14 - 0x0A - 0x05;
        stream.AddRange(Run(remaining, 0x00));
        stream.AddRange(Run(126, 0x08));
        stream.AddRange(Run(99, 0x08));

        var map = OblivionMobileTileMap.Parse([.. head, .. stream], "l14_head.jtm");

        Assert.Equal(15, map.Width);
        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(0, map.Cell(0, i, 0));
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(0, map.Cell(0, i, 1));
        }

        for (var i = 5; i < 15; i++)
        {
            Assert.Equal(1, map.Cell(0, i, 1));
        }
    }

    [Fact]
    public void Parse_CountsBlockedCellsPlacedTilesAndDistinctIds()
    {
        var bytes = Jtm(2, 2, Literals(0, 1, 5, 0), Literals(0, 7, 7, 8), Literals(0, 0, 0, 9));

        var map = OblivionMobileTileMap.Parse(bytes, "census.jtm");

        Assert.Equal(2, map.BlockedCellCount);
        Assert.Equal(4, map.PlacedTileCount);
        Assert.Equal(new byte[] { 7, 8, 9 }, map.DistinctTileIds.Order());
        var census = map.PassabilityCensus();
        Assert.Equal(2, census[0]);
        Assert.Equal(1, census[1]);
        Assert.Equal(1, census[5]);
    }

    // ================================================================ .jtm rejections

    [Fact]
    public void Parse_RejectsAFileShorterThanTheHeader()
    {
        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileTileMap.Parse([0x05], "tiny.jtm"));
        Assert.Contains("tiny.jtm", error.Message, StringComparison.Ordinal);
        Assert.Contains("too short", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(0, 0)]
    public void Parse_RejectsAMapWithNoCells(int width, int height)
    {
        var bytes = Jtm(width, height, Literals(1, 2));

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileTileMap.Parse(bytes, "empty.jtm"));
        Assert.Contains("no cells", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsATruncatedRunHeader()
    {
        // The run marker is the last byte: its count and value are missing.
        var bytes = Jtm(2, 2, Run(4, 0), [OblivionMobileTileMap.RunMarker]);

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileTileMap.Parse(bytes, "run.jtm"));
        Assert.Contains("run header at byte 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsARunHeaderMissingItsValueByte()
    {
        var bytes = Jtm(2, 2, Run(4, 0), [OblivionMobileTileMap.RunMarker, 0x02]);

        Assert.Throws<InvalidDataException>(() => OblivionMobileTileMap.Parse(bytes, "run2.jtm"));
    }

    [Fact]
    public void Parse_RejectsAStreamEndingInsideALayer()
    {
        // 3x3 wants nine cells per layer; the second layer supplies four.
        var bytes = Jtm(3, 3, Run(9, 0), Literals(1, 2, 3, 4));

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileTileMap.Parse(bytes, "partial.jtm"));
        Assert.Contains("4 of 9 cells", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsFewerThanTwoLayers()
    {
        var bytes = Jtm(2, 2, Run(4, 0));

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileTileMap.Parse(bytes, "onelayer.jtm"));
        Assert.Contains("decoded 1 layer(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsARunThatWouldCrossALayerBoundary()
    {
        // No retail file does this and the two possible decodings disagree, so refuse it.
        var bytes = Jtm(2, 2, Run(4, 0), Run(5, 3));

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileTileMap.Parse(bytes, "cross.jtm"));
        Assert.Contains("only 4 of its 4 cells left", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cell_RejectsCoordinatesOutsideTheMap()
    {
        var map = OblivionMobileTileMap.Parse(Jtm(2, 2, Run(4, 0), Run(4, 1)), "bounds.jtm");

        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cell(2, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cell(0, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cell(0, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cell(-1, 0, 0));
    }

    // ================================================================ .cml happy path

    [Fact]
    public void AtlasParse_ReadsTheSpecWorkedExampleFromL01()
    {
        // l01_1.cml offset 0x00: prefix 0, entry default id 1, "/ts_lvl1.png", mask 0, no pairs,
        // then the first sprite: mask 0x204 -> id 0x5E loop 0, one frame mask 0x168 -> sx 64,
        // w 36, h 22, dy -24.
        var frame = Attributes(
            (OblivionMobileAttributes.SourceXBit, [0x00, 0x40]),
            (OblivionMobileAttributes.WidthBit, [0x24]),
            (OblivionMobileAttributes.HeightBit, [0x16]),
            (OblivionMobileAttributes.OffsetYBit, [0xE8]));
        var bytes = Cml(string.Empty, Entry(1, "/ts_lvl1.png", RawMask(0), Sprite(SpriteHeader(0x5E), frame)));

        var atlas = OblivionMobileAtlas.Parse(bytes, "l01_1.cml");

        Assert.Equal(string.Empty, atlas.Prefix);
        var sheet = Assert.Single(atlas.Sheets);
        Assert.Equal("/ts_lvl1.png", sheet.Path);
        Assert.Equal(1, sheet.DefaultId);
        Assert.Empty(sheet.Pairs);
        Assert.False(sheet.IsWholeImage);

        var sprite = Assert.Single(sheet.Sprites);
        Assert.Equal(0x5E, sprite.Id);
        Assert.False(sprite.Loops);
        Assert.Equal(0x204, sprite.Attributes.Mask);

        var only = Assert.Single(sprite.Frames);
        Assert.Equal(64, only.SourceX);
        Assert.Equal(0, only.SourceY);
        Assert.Equal(36, only.Width);
        Assert.Equal(22, only.Height);
        Assert.Equal(0, only.OffsetX);
        Assert.Equal(-24, only.OffsetY);
        Assert.False(only.Mirror);

        Assert.True(atlas.TryGetTile(0x5E, out var resolved, out var path));
        Assert.Equal(only, resolved);
        Assert.Equal("/ts_lvl1.png", path);
    }

    [Fact]
    public void AtlasParse_ReadsEveryMaskedFieldInBitOrder()
    {
        var all = Attributes(
            (OblivionMobileAttributes.IdBit, [0x2A]),
            (OblivionMobileAttributes.SourceXBit, [0x01, 0x02]),
            (OblivionMobileAttributes.SourceYBit, [0x03, 0x04]),
            (OblivionMobileAttributes.WidthBit, [0x10]),
            (OblivionMobileAttributes.HeightBit, [0x11]),
            (OblivionMobileAttributes.OffsetXBit, [0xFF]),
            (OblivionMobileAttributes.OffsetYBit, [0x80]),
            (OblivionMobileAttributes.LoopBit, [0x01]),
            (OblivionMobileAttributes.MirrorBit, [0x01]),
            (OblivionMobileAttributes.UnusedBit, [0x7F]));
        var bytes = Cml(string.Empty, Entry(0, "/x.png", RawMask(0), Sprite(SpriteHeader(1, 1), all)));

        var atlas = OblivionMobileAtlas.Parse(bytes, "all.cml");
        var sprite = atlas.Sheets[0].Sprites[0];

        Assert.True(sprite.Loops);
        var frame = sprite.Frames[0];
        Assert.Equal(0x0102, frame.SourceX);
        Assert.Equal(0x0304, frame.SourceY);
        Assert.Equal(0x10, frame.Width);
        Assert.Equal(0x11, frame.Height);
        Assert.Equal(-1, frame.OffsetX);
        Assert.Equal(-128, frame.OffsetY);
        Assert.True(frame.Mirror);
    }

    [Fact]
    public void AtlasParse_TreatsAnEntryWithNoSpritesAsOneWholeImageTile()
    {
        // The retail /c9.png entry: default id 3, id 210, 16x37 at (0, 0) with dy -24.
        var image = Attributes(
            (OblivionMobileAttributes.IdBit, [210]),
            (OblivionMobileAttributes.SourceXBit, [0x00, 0x00]),
            (OblivionMobileAttributes.WidthBit, [0x10]),
            (OblivionMobileAttributes.HeightBit, [0x25]),
            (OblivionMobileAttributes.OffsetYBit, [0xE8]));
        var bytes = Cml(string.Empty, Entry(3, "/c9.png", image));

        var atlas = OblivionMobileAtlas.Parse(bytes, "whole.cml");

        var sheet = Assert.Single(atlas.Sheets);
        Assert.True(sheet.IsWholeImage);
        Assert.Empty(sheet.Sprites);
        Assert.True(atlas.TryGetTile(210, out var frame, out var path));
        Assert.Equal("/c9.png", path);
        Assert.Equal(16, frame.Width);
        Assert.Equal(37, frame.Height);
        Assert.Equal(-24, frame.OffsetY);
    }

    [Fact]
    public void AtlasParse_WholeImageEntryFallsBackToTheEntryDefaultIdWhenTheMaskOmitsOne()
    {
        var bytes = Cml(string.Empty, Entry(77, "/splash.png", RawMask(0)));

        var atlas = OblivionMobileAtlas.Parse(bytes, "default.cml");

        Assert.True(atlas.TryGetTile(77, out _, out var path));
        Assert.Equal("/splash.png", path);
    }

    [Fact]
    public void AtlasParse_ASpriteWithoutAnIdTakesIdZero()
    {
        // One sprite in retail oh_magic.cml omits its id; the engine leaves it at 0.
        var header = Attributes((OblivionMobileAttributes.LoopBit, [0]));
        var frame = Attributes(
            (OblivionMobileAttributes.WidthBit, [4]),
            (OblivionMobileAttributes.HeightBit, [4]));
        var bytes = Cml(string.Empty, Entry(9, "/magic.png", RawMask(0), Sprite(header, frame)));

        var atlas = OblivionMobileAtlas.Parse(bytes, "idless.cml");

        Assert.Equal(0, atlas.Sheets[0].Sprites[0].Id);
        Assert.True(atlas.TryGetTile(0, out _, out _));
    }

    [Fact]
    public void AtlasParse_ResolvesATileToTheFirstFrameOfAMultiFrameSprite()
    {
        var first = Attributes(
            (OblivionMobileAttributes.SourceXBit, [0x00, 0x05]),
            (OblivionMobileAttributes.WidthBit, [8]),
            (OblivionMobileAttributes.HeightBit, [8]));
        var second = Attributes(
            (OblivionMobileAttributes.SourceXBit, [0x00, 0x0D]),
            (OblivionMobileAttributes.WidthBit, [8]),
            (OblivionMobileAttributes.HeightBit, [8]));
        var bytes = Cml(string.Empty, Entry(0, "/c1.png", RawMask(0), Sprite(SpriteHeader(4, 1), first, second)));

        var atlas = OblivionMobileAtlas.Parse(bytes, "anim.cml");

        Assert.Equal(2, atlas.FrameCount);
        Assert.Equal(1, atlas.SpriteCount);
        Assert.True(atlas.TryGetTile(4, out var frame, out _));
        Assert.Equal(5, frame.SourceX);
    }

    [Fact]
    public void AtlasParse_ReadsSeveralEntriesAndAPrefix()
    {
        var frame = Attributes(
            (OblivionMobileAttributes.WidthBit, [2]),
            (OblivionMobileAttributes.HeightBit, [2]));
        var bytes = Cml(
            "gfx",
            Entry(0, "/a.png", RawMask(0), Sprite(SpriteHeader(1), frame)),
            Entry(0, "/b.png", RawMask(0), Sprite(SpriteHeader(2), frame), Sprite(SpriteHeader(3), frame)));

        var atlas = OblivionMobileAtlas.Parse(bytes, "two.cml");

        Assert.Equal("gfx", atlas.Prefix);
        Assert.Equal(2, atlas.Sheets.Count);
        Assert.Equal(3, atlas.SpriteCount);
        Assert.Equal(new byte[] { 1, 2, 3 }, atlas.TileIds.Order());
        Assert.True(atlas.TryGetTile(3, out _, out var path));
        Assert.Equal("/b.png", path);
        Assert.False(atlas.TryGetTile(4, out _, out var missing));
        Assert.Equal(string.Empty, missing);
    }

    [Fact]
    public void AtlasParse_SkipsThePairTableWithoutInterpretingIt()
    {
        var name = Encoding.ASCII.GetBytes("/p.png");
        var frame = Attributes(
            (OblivionMobileAttributes.WidthBit, [1]),
            (OblivionMobileAttributes.HeightBit, [1]));
        byte[] entry =
        [
            0, (byte)name.Length, .. name, .. RawMask(0),
            2, // pairCount
            0x11, 0x22, 0x33, 0x44, 0x55, 0x66,
            0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC,
            1, .. Sprite(SpriteHeader(6), frame),
        ];

        var atlas = OblivionMobileAtlas.Parse(Cml(string.Empty, entry), "pairs.cml");

        Assert.Equal(2 * OblivionMobileAtlas.PairLength, atlas.Sheets[0].Pairs.Count);
        Assert.Equal(0x11, atlas.Sheets[0].Pairs[0]);
        Assert.True(atlas.TryGetTile(6, out _, out _));
    }

    // ================================================================ .cml rejections

    [Theory]
    [InlineData(0x0400)]
    [InlineData(0x8000)]
    [InlineData(0xFFFF)]
    public void AtlasParse_RejectsAMaskWithReservedBitsSet(int mask)
    {
        var bytes = Cml(string.Empty, Entry(0, "/x.png", RawMask(mask)));

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse(bytes, "mask.cml"));
        Assert.Contains("bits 15..10 are", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasParse_RejectsANameLengthThatRunsPastEof()
    {
        // defaultId 0, nameLen 40, but only four name bytes follow.
        byte[] bytes = [0, 0, 40, .. Encoding.ASCII.GetBytes("/a.p")];

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse(bytes, "name.cml"));
        Assert.Contains("40 bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("name.cml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasParse_RejectsAPrefixLengthThatRunsPastEof()
    {
        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse([9, 1, 2], "prefix.cml"));
        Assert.Contains("prefix", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasParse_RejectsAnEmptyFile()
    {
        Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse([], "nothing.cml"));
    }

    [Fact]
    public void AtlasParse_RejectsATruncatedAttributeMask()
    {
        byte[] bytes = [0, 0, 6, .. Encoding.ASCII.GetBytes("/a.png"), 0x01];

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse(bytes, "mask2.cml"));
        Assert.Contains("attribute mask", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasParse_RejectsAMaskWhoseFieldsRunPastEof()
    {
        // Mask claims sx (u16) but only one byte follows it.
        byte[] bytes =
        [
            0, 0, 6, .. Encoding.ASCII.GetBytes("/a.png"),
            .. RawMask(1 << OblivionMobileAttributes.SourceXBit), 0x00,
        ];

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse(bytes, "sx.cml"));
        Assert.Contains("source x", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasParse_RejectsAPairTableThatRunsPastEof()
    {
        byte[] bytes = [0, 0, 6, .. Encoding.ASCII.GetBytes("/a.png"), .. RawMask(0), 4, 1, 2, 3];

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse(bytes, "pair.cml"));
        Assert.Contains("4 pair(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasParse_RejectsASpriteCountTheFileCannotHold()
    {
        byte[] bytes = [0, 0, 6, .. Encoding.ASCII.GetBytes("/a.png"), .. RawMask(0), 0, 3];

        Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse(bytes, "sprites.cml"));
    }

    [Fact]
    public void AtlasParse_RejectsADuplicateIdWithinTheFile()
    {
        var frame = Attributes(
            (OblivionMobileAttributes.WidthBit, [2]),
            (OblivionMobileAttributes.HeightBit, [2]));
        var bytes = Cml(
            string.Empty,
            Entry(0, "/a.png", RawMask(0), Sprite(SpriteHeader(12), frame)),
            Entry(0, "/b.png", RawMask(0), Sprite(SpriteHeader(12), frame)));

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse(bytes, "dup.cml"));
        Assert.Contains("tile id 12 is defined twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasParse_RejectsAWholeImageIdThatCollidesWithASprite()
    {
        var frame = Attributes(
            (OblivionMobileAttributes.WidthBit, [2]),
            (OblivionMobileAttributes.HeightBit, [2]));
        var bytes = Cml(
            string.Empty,
            Entry(0, "/a.png", RawMask(0), Sprite(SpriteHeader(210), frame)),
            Entry(0, "/c9.png", Attributes((OblivionMobileAttributes.IdBit, [210]))));

        Assert.Throws<InvalidDataException>(() => OblivionMobileAtlas.Parse(bytes, "collide.cml"));
    }

    // ================================================================ projection

    [Fact]
    public void CellOrigin_PutsCellsOnA32x16Diamond()
    {
        Assert.Equal(32, OblivionMobileIsometric.TileWidth);
        Assert.Equal(16, OblivionMobileIsometric.TileHeight);

        Assert.Equal((-16, 0), OblivionMobileIsometric.CellOrigin(0, 0));
        Assert.Equal((0, 8), OblivionMobileIsometric.CellOrigin(1, 0));
        Assert.Equal((-32, 8), OblivionMobileIsometric.CellOrigin(0, 1));
        Assert.Equal((-16, 16), OblivionMobileIsometric.CellOrigin(1, 1));
        Assert.Equal((32, 24), OblivionMobileIsometric.CellOrigin(3, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 3)]
    [InlineData(12, 40)]
    [InlineData(56, 58)]
    public void CellOrigin_MatchesTheEngineWorldFormula(int i, int j)
    {
        // sx = (wx - wy) / 8 - 16, sy = (wx + wy) / 16, with 128-unit cells.
        var wx = i * 128;
        var wy = j * 128;
        var expected = (((wx - wy) / 8) - 16, (wx + wy) / 16);

        Assert.Equal(expected, OblivionMobileIsometric.CellOrigin(i, j));
    }

    [Fact]
    public void TileOrigin_AddsTheAtlasFrameOffset()
    {
        var frame = new OblivionMobileFrame(0, 0, 36, 22, 2, -24, false);

        // Cell (1, 0) sits at (0, 8); the frame's (2, -24) lifts the tile out of its diamond.
        Assert.Equal((2, -16), OblivionMobileIsometric.TileOrigin(1, 0, frame));
        Assert.Equal(
            OblivionMobileIsometric.TileOrigin(1, 0, frame.OffsetX, frame.OffsetY),
            OblivionMobileIsometric.TileOrigin(1, 0, frame));
        Assert.Equal(OblivionMobileIsometric.CellOrigin(4, 2), OblivionMobileIsometric.TileOrigin(4, 2, 0, 0));
    }

    [Fact]
    public void DrawOrder_IsIOuterJInnerAscending()
    {
        var order = OblivionMobileIsometric.DrawOrder(3, 2).ToList();

        Assert.Equal(new[] { (0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (2, 1) }, order);
    }

    [Fact]
    public void DrawOrder_VisitsEveryCellOfAMapExactlyOnce()
    {
        var map = OblivionMobileTileMap.Parse(Jtm(4, 3, Run(12, 0), Run(12, 1)), "order.jtm");

        var visited = new HashSet<(int, int)>();
        foreach (var (i, j) in OblivionMobileIsometric.DrawOrder(map.Width, map.Height))
        {
            Assert.True(visited.Add((i, j)));
            Assert.Equal(1, map.Cell(1, i, j));
        }

        Assert.Equal(map.CellCount, visited.Count);
    }
}
