using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Redguard;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic vectors for <see cref="RedguardWldFile" />, shaped after the layout RG.EXE's scape
///     loader reads (2026-09-08): a 144-byte header, a four-entry tile table, a 1,024-byte level
///     table, four tile records of a 22-byte header plus four 128 x 128 quarters, and the TULO trailer.
/// </summary>
public sealed class RedguardWldFileTests
{
    private const int FirstTile = 160 + RedguardWldFile.LevelTableLength;
    private const int TileStride = RedguardWldFile.TileRecordHeaderLength + RedguardWldFile.TilePayloadLength;

    /// <summary>
    ///     Every quarter <c>k</c> of tile <c>i</c> is filled with a value that names it
    ///     (<c>1 + 16·i + k</c>), except the cells <paramref name="cell" /> overrides.
    /// </summary>
    private static byte[] Build(Action<byte[]>? mutate = null, Func<int, int, int, int, byte?>? cell = null)
    {
        var bytes = new byte[RedguardWldFile.FileLength];
        uint[] header = [16, 2, 2, 0, 160, 1, 22, RedguardWldFile.FileLength - 16];
        for (var i = 0; i < header.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), header[i]);
        }

        bytes[160] = 25;
        bytes[161] = 30;
        bytes[162] = 80;
        bytes[163] = 127;

        for (var tile = 0; tile < 4; tile++)
        {
            var at = FirstTile + tile * TileStride;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RedguardWldFile.TileTableOffset + 4 * tile),
                (uint)at);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)(1000 + tile));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 6), 302);
            bytes[at + 9] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 10), 1);

            var payload = at + RedguardWldFile.TileRecordHeaderLength;
            for (var quarter = 0; quarter < 4; quarter++)
            {
                for (var row = 0; row < RedguardWldFile.TileSize; row++)
                {
                    for (var col = 0; col < RedguardWldFile.TileSize; col++)
                    {
                        var value = cell?.Invoke(tile, quarter, col, row) ?? (byte)(1 + 16 * tile + quarter);
                        bytes[
                            payload + quarter * RedguardWldFile.TileLayerLength + row * RedguardWldFile.TileSize +
                            col] = value;
                    }
                }
            }
        }

        "TULO"u8.CopyTo(bytes.AsSpan(RedguardWldFile.FileLength - RedguardWldFile.TrailerLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RedguardWldFile.FileLength - 12), 0x43C028);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RedguardWldFile.FileLength - 8), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RedguardWldFile.FileLength - 4), 0x43735);

        mutate?.Invoke(bytes);
        return bytes;
    }

    [Fact]
    public void Parse_ReadsHeaderTilesLevelTableAndTrailer()
    {
        var wld = RedguardWldFile.Parse(Build(), "ISLAND.WLD");

        Assert.Equal([16u, 2u, 2u, 0u, 160u, 1u, 22u, 263416u], wld.Header);
        Assert.Equal((2, 2), (wld.TilesX, wld.TilesZ));
        Assert.Equal([0x43C028u, 0xFFFFFFFFu, 0x43735u], wld.Trailer);
        Assert.Equal([25, 30, 80, 127], wld.LevelTable.Take(4));
        Assert.Equal(302, wld.TextureSet);

        Assert.Equal(4, wld.Tiles.Count);
        Assert.Equal([(0, 0), (1, 0), (0, 1), (1, 1)], wld.Tiles.Select(t => (t.TileX, t.TileZ)));
        Assert.Equal([1184, 66742, 132300, 197858], wld.Tiles.Select(t => t.Offset));
        Assert.Equal([1000u, 1001u, 1002u, 1003u], wld.Tiles.Select(t => t.Word0));
        Assert.All(wld.Tiles, t => Assert.True(t.IsStored));
    }

    [Fact]
    public void Parse_ReadsTextureSetAfterTheUninterpretedHeaderWord()
    {
        // The retail tile header stores 2E 01 at +6. Give the preceding word a distinct
        // value so reading +4 cannot pass, and preserve that unknown field verbatim.
        byte[] recordHeader =
        [
            0x68, 0x08, 0, 0, 0xEF, 0xBE, 0x2E, 0x01, 0, 1, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        ];
        var wld = RedguardWldFile.Parse(Build(bytes => recordHeader.CopyTo(bytes, FirstTile)), "ISLAND.WLD");

        Assert.Equal(302, wld.TextureSet);
        Assert.Equal(302, wld.Tiles[0].TextureSet);
        Assert.Equal(recordHeader, wld.Tiles[0].RawHeader);
    }

    [Fact]
    public void Parse_PlacesEachQuarterOfEachTileInItsOwnLayerAndQuadrant()
    {
        var wld = RedguardWldFile.Parse(Build(), "ISLAND.WLD");

        Assert.Equal(4, wld.Layers.Count);
        Assert.All(wld.Layers, layer => Assert.Equal((256, 256), (layer.Width, layer.Height)));

        // Quarter k of tile i lands in layer k at (128·(i mod 2), 128·(i div 2)).
        for (var layer = 0; layer < 4; layer++)
        {
            var indices = wld.Layers[layer].Indices;
            Assert.Equal(1 + layer, indices[0]);
            Assert.Equal(1 + layer, indices[127 * 256 + 127]);
            Assert.Equal(17 + layer, indices[128]);
            Assert.Equal(17 + layer, indices[127 * 256 + 255]);
            Assert.Equal(33 + layer, indices[128 * 256]);
            Assert.Equal(49 + layer, indices[255 * 256 + 255]);
        }

        Assert.Same(wld.Layers[0], wld.HeightLayer);
        Assert.Same(wld.Layers[1], wld.ScatterLayer);
        Assert.Same(wld.Layers[2], wld.SurfaceLayer);
        Assert.Same(wld.Layers[3], wld.UnusedLayer);
    }

    [Fact]
    public void HeightSurfaceAndScatterDecodeTheirBitFields()
    {
        // Tile 0, quarter 0 cell (5, 7): height index 100 with the stored split bit; quarter 2:
        // texture 39 rotated twice; quarter 1: scatter kind 12 with low bits 3.
        var wld = RedguardWldFile.Parse(Build(cell: (tile, quarter, col, row) =>
            (tile, quarter, col, row) switch
            {
                (0, 0, 5, 7) => 0x80 | 100,
                (0, 2, 5, 7) => (2 << 6) | 39,
                (0, 1, 5, 7) => (12 << 2) | 3,
                _ => null
            }), "ISLAND.WLD");

        Assert.Equal(100, wld.HeightIndexAt(5, 7));
        Assert.Equal(-2440, wld.WorldHeightAt(5, 7));
        Assert.True(wld.StoredQuadSplitAt(5, 7));
        Assert.False(wld.StoredQuadSplitAt(6, 7));
        Assert.Equal(39, wld.SurfaceTextureAt(5, 7));
        Assert.Equal(2, wld.SurfaceRotationAt(5, 7));
        Assert.Equal(12, wld.ScatterKindAt(5, 7));
    }

    [Fact]
    public void HeightTableIsTheGamesNonLinearRamp()
    {
        Assert.Equal(128, RedguardWldFile.HeightTable.Count);
        Assert.Equal(0, RedguardWldFile.HeightTable[0]);
        Assert.Equal(40, RedguardWldFile.HeightTable[1]);
        Assert.Equal(1000, RedguardWldFile.HeightTable[58]);
        Assert.Equal(7760, RedguardWldFile.HeightTable[127]);
        for (var i = 1; i < RedguardWldFile.HeightTable.Count; i++)
        {
            Assert.True(RedguardWldFile.HeightTable[i] >= RedguardWldFile.HeightTable[i - 1], $"entry {i} steps down");
        }
    }

    [Fact]
    public void WorldToCellMappingFlipsZAndUses256UnitCells()
    {
        Assert.True(RedguardWldFile.TryMapWorldToCell(0, 65536, out var x, out var z));
        Assert.Equal((0, 0), (x, z));

        Assert.True(RedguardWldFile.TryMapWorldToCell(65535, 1, out x, out z));
        Assert.Equal((255, 255), (x, z));

        // 40,000 across and 30,000 up: column 156, row (65536 - 30000) >> 8 = 138.
        Assert.True(RedguardWldFile.TryMapWorldToCell(40000, 30000, out x, out z));
        Assert.Equal((156, 138), (x, z));

        Assert.False(RedguardWldFile.TryMapWorldToCell(-1, 30000, out _, out _));
        Assert.False(RedguardWldFile.TryMapWorldToCell(65536, 30000, out _, out _));
        Assert.False(RedguardWldFile.TryMapWorldToCell(30000, 0, out _, out _));
        Assert.False(RedguardWldFile.TryMapWorldToCell(30000, 65537, out _, out _));
    }

    [Fact]
    public void SampleWorldHeight_InterpolatesBetweenCellCornersAndNegates()
    {
        // Row 0 (z near 65536): cell (0,0) index 1 (40), cell (1,0) index 4 (80); row 1: both index 7 (120).
        var wld = RedguardWldFile.Parse(Build(cell: (tile, quarter, col, row) =>
            (tile, quarter, col, row) switch
            {
                (0, 0, 0, 0) => 1,
                (0, 0, 1, 0) => 4,
                (0, 0, 0, 1) => 7,
                (0, 0, 1, 1) => 7,
                _ => null
            }), "ISLAND.WLD");

        Assert.Equal(-40, wld.SampleWorldHeight(0, 65536));
        Assert.Equal(-60, wld.SampleWorldHeight(128, 65536));
        Assert.Equal(-80, wld.SampleWorldHeight(256, 65536));
        Assert.Equal(-90, wld.SampleWorldHeight(128, 65536 - 128), 6);
        Assert.True(double.IsNaN(wld.SampleWorldHeight(-5, 1000)));
    }

    [Fact]
    public void ComputedQuadSplit_FlagsOnlyQuadsWhoseDiagonalsDisagree()
    {
        // Cell (2,2) corners 0/1/1/... : planar ramp (0,40,40,80 = indices 0,1,1,4) has equal
        // diagonals; a lone bump (indices 0,0,0,1) does not.
        var wld = RedguardWldFile.Parse(Build(cell: (tile, quarter, col, row) =>
            (tile, quarter, col, row) switch
            {
                (0, 0, 2, 2) => 0,
                (0, 0, 3, 2) => 1,
                (0, 0, 2, 3) => 1,
                (0, 0, 3, 3) => 4,
                (0, 0, 10, 10) => 0,
                (0, 0, 11, 10) => 0,
                (0, 0, 10, 11) => 0,
                (0, 0, 11, 11) => 1,
                _ => null
            }), "ISLAND.WLD");

        Assert.False(wld.ComputedQuadSplitAt(2, 2));
        Assert.True(wld.ComputedQuadSplitAt(10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => wld.ComputedQuadSplitAt(255, 10));
    }

    [Theory]
    [InlineData(0, 3, 5, 3, 5)]
    [InlineData(1, 3, 5, 58, 3)]
    [InlineData(2, 3, 5, 60, 58)]
    [InlineData(3, 3, 5, 5, 60)]
    public void RotateTexel_TurnsInQuarterSteps(int rotation, int u, int v, int expectedU, int expectedV)
    {
        Assert.Equal((expectedU, expectedV), RedguardWldFile.RotateTexel(u, v, rotation, 64));
    }

    [Fact]
    public void Parse_WrongLength_Throws()
    {
        Assert.Throws<InvalidDataException>(() => RedguardWldFile.Parse(Build().Take(1000).ToArray(), "BAD.WLD"));
    }

    [Fact]
    public void Parse_HeaderLengthWordThatDoesNotMatchTheFile_Throws()
    {
        var bytes = Build(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 1));

        Assert.Throws<InvalidDataException>(() => RedguardWldFile.Parse(bytes, "BAD.WLD"));
    }

    [Fact]
    public void Parse_TileTableThatDoesNotTile_Throws()
    {
        var bytes = Build(b =>
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(RedguardWldFile.TileTableOffset + 4), 66742 + 1));

        Assert.Throws<InvalidDataException>(() => RedguardWldFile.Parse(bytes, "BAD.WLD"));
    }

    [Fact]
    public void Parse_ProceduralSeedTile_IsRefusedNotGuessed()
    {
        var bytes = Build(b => b[FirstTile + TileStride + 9] = 0);

        Assert.Throws<NotSupportedException>(() => RedguardWldFile.Parse(bytes, "BAD.WLD"));
    }

    [Fact]
    public void Parse_MissingTrailerTag_Throws()
    {
        var bytes = Build(b => b[RedguardWldFile.FileLength - RedguardWldFile.TrailerLength] = (byte)'X');

        Assert.Throws<InvalidDataException>(() => RedguardWldFile.Parse(bytes, "BAD.WLD"));
    }

    [Fact]
    public void IsWldFile_NeedsTheFixedLengthAndTheTrailer()
    {
        Assert.True(RedguardWldFile.IsWldFile(Build()));
        Assert.False(RedguardWldFile.IsWldFile(Build().Take(RedguardWldFile.FileLength - 1).ToArray()));
        Assert.False(RedguardWldFile.IsWldFile(Build(b => b[RedguardWldFile.FileLength - 16] = 0)));
    }
}
