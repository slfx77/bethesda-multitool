using System.Globalization;
using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Synthetic vectors for the <c>.til</c> reader. Every fixture is written out byte by byte here
///     — nothing calls the reader to build its own expectation — and every pinned number was
///     computed by hand from the layout in <see cref="TacticsTileFile" /> before the reader ran.
///     <para>
///         The claim these guard is the one that is easy to get wrong: the flag word's SHAPE
///         changes with the header version (a u16 on v10, a u8 on v9 and v8, and two one-bit fields
///         separated by throwaway bytes below that), so the five versions have five different
///         record lengths — 33, 31, 32, 33 and 34 bytes for v10, v9, v8, v7 and v6.
///     </para>
/// </summary>
public sealed class TacticsTileFileTests
{
    /// <summary>A 4x2 <c>&lt;zar&gt;</c> with a two-entry palette: row 0 four opaque pixels, row 1 transparent.</summary>
    private static byte[] EmbeddedZar()
    {
        return TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.Tag("zar", "4"),
            TacticsSyntheticBytes.I32(4),
            TacticsSyntheticBytes.I32(2),
            [1],
            TacticsSyntheticBytes.U32(2),
            [0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x00],
            [0],
            TacticsSyntheticBytes.U32(6),
            [(4 << 2) | 1, 0, 1, 0, 1, (4 << 2) | 0]);
    }

    /// <summary>
    ///     The header body for one version, with the flag word laid out the way that version wants
    ///     it. <paramref name="flags" /> is what the reader should end up with.
    /// </summary>
    private static byte[] HeaderBody(int version, ushort flags)
    {
        var common = TacticsSyntheticBytes.Concat(
            [6, 1, 6],
            TacticsSyntheticBytes.I32(24),
            TacticsSyntheticBytes.I32(-3),
            TacticsSyntheticBytes.I32(4),
            TacticsSyntheticBytes.I32(2),
            [1],
            [2]);

        byte[] tail = version switch
        {
            10 => [(byte)(flags & 0xFF), (byte)(flags >> 8)],
            9 => [(byte)flags],
            8 => [100, (byte)flags],
            7 => [100, (byte)(flags & 1), (byte)((flags >> 1) & 1)],
            6 => [100, (byte)(flags & 1), 0, (byte)((flags >> 1) & 1)],
            _ => throw new ArgumentOutOfRangeException(nameof(version))
        };

        return TacticsSyntheticBytes.Concat(common, tail);
    }

    private static byte[] Tile(int version, ushort flags, int imageCount = 1)
    {
        var parts = new List<byte[]>
        {
            TacticsSyntheticBytes.Tag("tile", version.ToString(CultureInfo.InvariantCulture)),
            HeaderBody(version, flags),
            TacticsSyntheticBytes.Tag("tiledata", "1"),
            TacticsSyntheticBytes.U32((uint)imageCount)
        };

        for (var i = 0; i < imageCount; i++)
        {
            parts.Add(EmbeddedZar());
            parts.Add(TacticsSyntheticBytes.U32(0));
            parts.Add(TacticsSyntheticBytes.U32(0));
        }

        if (imageCount > 0)
        {
            parts.Add(TacticsSyntheticBytes.U32(2));
            parts.Add([0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x00]);
        }

        return TacticsSyntheticBytes.Concat([.. parts]);
    }

    [Fact]
    public void Version10_ReadsEveryNamedFieldAndTilesTheFile()
    {
        var tile = TacticsTileFile.Parse(Tile(10, 0x0809), "v10.til");

        Assert.Equal(10, tile.Header.Version);
        Assert.Equal(6, tile.Header.BoundingBoxX);
        Assert.Equal(1, tile.Header.BoundingBoxY);
        Assert.Equal(6, tile.Header.BoundingBoxZ);
        Assert.Equal(24, tile.Header.FootPositionX);
        Assert.Equal(-3, tile.Header.FootPositionY);
        Assert.Equal(4, tile.Header.ImageWidth);
        Assert.Equal(2, tile.Header.ImageHeight);
        Assert.Equal(TacticsTileType.Floor, tile.Header.Type);
        Assert.Equal(TacticsTileMaterial.Metal, tile.Header.Material);
        Assert.Equal(TacticsTileFlags.Ethereal | TacticsTileFlags.Cover | TacticsTileFlags.Trellis, tile.Header.Flags);
        Assert.Null(tile.Header.LegacyByteA);
        Assert.Null(tile.Header.LegacyByteB);

        Assert.Single(tile.Images);
        Assert.Equal(4, tile.Images[0].Image.Width);
        Assert.Equal(2, tile.Images[0].Image.Height);
        Assert.Equal(8, tile.SharedPaletteBgrx.Length);
    }

    /// <summary>
    ///     The board records the mission grid's <c>&lt;tile&gt;</c> stride as exactly 33 bytes on
    ///     99,493 of 99,493 consecutive pairs. 33 is <c>'&lt;tile&gt;' NUL "10" NUL</c> (10) plus a
    ///     23-byte body, so a v10 record read here must come to the same number.
    /// </summary>
    [Theory]
    [InlineData(10, 33)]
    [InlineData(9, 31)]
    [InlineData(8, 32)]
    [InlineData(7, 33)]
    [InlineData(6, 34)]
    public void EachVersionHasItsOwnRecordLength(int version, int expected)
    {
        var cursor = new TacticsCursor(
            TacticsSyntheticBytes.Concat(
                TacticsSyntheticBytes.Tag("tile", version.ToString(CultureInfo.InvariantCulture)),
                HeaderBody(version, 0)),
            $"v{version}.til");

        var header = TacticsTileFile.ReadHeader(cursor);
        Assert.Equal(expected, header.RecordLength);
        Assert.Equal(expected, cursor.Position);
        Assert.True(cursor.AtEnd);
    }

    /// <summary>
    ///     Below version 8 only bits 0 and 1 exist, each carried by its own byte, and the reader
    ///     must keep every other bit clear whatever those bytes hold above bit 0.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(6)]
    public void OldVersionsCarryOnlyTwoFlagBits(int version)
    {
        // Both flag bytes hold 0xFF: the reader masks each with 1, so only bits 0 and 1 may appear
        // in the answer. Reading either byte whole would give 0xFF or 0x1FF instead.
        byte[] tail = version == 6 ? [100, 0xFF, 0, 0xFF] : [100, 0xFF, 0xFF];
        var body = TacticsSyntheticBytes.Concat(
            [6, 1, 6],
            TacticsSyntheticBytes.I32(24),
            TacticsSyntheticBytes.I32(-3),
            TacticsSyntheticBytes.I32(4),
            TacticsSyntheticBytes.I32(2),
            [1],
            [2],
            tail);

        var cursor = new TacticsCursor(
            TacticsSyntheticBytes.Concat(
                TacticsSyntheticBytes.Tag("tile", version.ToString(CultureInfo.InvariantCulture)),
                body),
            $"v{version}.til");

        var header = TacticsTileFile.ReadHeader(cursor);
        Assert.Equal((TacticsTileFlags)0b11, header.Flags);
        Assert.Equal((byte)100, header.LegacyByteA);
        Assert.Equal(version == 6 ? (byte?)0 : null, header.LegacyByteB);
    }

    [Fact]
    public void Version9And8DifferOnlyByTheThrowawayByte()
    {
        var nine = TacticsTileFile.ReadHeader(new TacticsCursor(
            TacticsSyntheticBytes.Concat(TacticsSyntheticBytes.Tag("tile", "9"), HeaderBody(9, 0x2C)), "v9.til"));
        var eight = TacticsTileFile.ReadHeader(new TacticsCursor(
            TacticsSyntheticBytes.Concat(TacticsSyntheticBytes.Tag("tile", "8"), HeaderBody(8, 0x2C)), "v8.til"));

        Assert.Equal((TacticsTileFlags)0x2C, nine.Flags);
        Assert.Equal((TacticsTileFlags)0x2C, eight.Flags);
        Assert.Null(nine.LegacyByteA);
        Assert.Equal((byte)100, eight.LegacyByteA);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("11")]
    [InlineData("0")]
    public void AVersionOutsideSixToTenIsRefusedWithTheGamesMessage(string version)
    {
        var bytes = TacticsSyntheticBytes.Concat(TacticsSyntheticBytes.Tag("tile", version), HeaderBody(10, 0));
        var error = Assert.Throws<InvalidDataException>(() =>
            TacticsTileFile.ReadHeader(new TacticsCursor(bytes, "bad.til")));
        Assert.Contains(TacticsTileFile.UnknownTileMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATileDataVersionOtherThanOneIsRefusedWithTheGamesMessage()
    {
        var bytes = TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.Tag("tile", "10"),
            HeaderBody(10, 0),
            TacticsSyntheticBytes.Tag("tiledata", "2"),
            TacticsSyntheticBytes.U32(0));

        var error = Assert.Throws<InvalidDataException>(() => TacticsTileFile.Parse(bytes, "bad.til"));
        Assert.Contains(TacticsTileFile.UnknownTileDataMessage, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>FUN_006f0740</c> returns before the palette read when the image count is zero, so a
    ///     tile that ends right after the count is complete, not truncated.
    /// </summary>
    [Fact]
    public void AZeroImageCountEndsTheFileWithNoPalette()
    {
        var tile = TacticsTileFile.Parse(Tile(10, 0, 0), "empty.til");
        Assert.Empty(tile.Images);
        Assert.Equal(0, tile.SharedPaletteBgrx.Length);
    }

    [Fact]
    public void MultipleImagesEachCarryTheirOwnEightTrailingBytes()
    {
        var parts = new List<byte[]>
        {
            TacticsSyntheticBytes.Tag("tile", "10"),
            HeaderBody(10, 0),
            TacticsSyntheticBytes.Tag("tiledata", "1"),
            TacticsSyntheticBytes.U32(2),
            EmbeddedZar(),
            TacticsSyntheticBytes.U32(7),
            TacticsSyntheticBytes.U32(9),
            EmbeddedZar(),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.U32(2),
            new byte[] { 0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x00 }
        };

        var tile = TacticsTileFile.Parse(TacticsSyntheticBytes.Concat([.. parts]), "two.til");
        Assert.Equal(2, tile.Images.Count);
        Assert.Equal(7u, tile.Images[0].OffsetX);
        Assert.Equal(9u, tile.Images[0].OffsetY);
        Assert.Equal(0u, tile.Images[1].OffsetX);
    }

    [Fact]
    public void ATrailingByteAfterTheLastImageIsRefused()
    {
        var bytes = TacticsSyntheticBytes.Concat(Tile(10, 0), [0xAB]);
        Assert.Throws<InvalidDataException>(() => TacticsTileFile.Parse(bytes, "long.til"));
        Assert.False(TacticsTileFile.TryParse(bytes, "long.til", out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TheEmbeddedImageDecodesThroughItsOwnPalette()
    {
        var tile = TacticsTileFile.Parse(Tile(10, 0), "px.til");
        var pixels = tile.Images[0].Image.Decode().Pixels;

        // Row 0 alternates palette entries 0 (stored B,G,R = 00 00 FF, so red) and 1 (blue).
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[4..8]);

        // Row 1 is a single mode-0 run: fully transparent.
        Assert.Equal(new byte[16], pixels[16..]);
    }

    /// <summary>
    ///     <c>FUN_006f02d0</c> transcribed. These six inputs pick six different branches and the
    ///     expected class is read off the decompiled comparison chain, not off the method.
    /// </summary>
    [Theory]
    [InlineData(6, 6, 6, 5)]
    [InlineData(6, 1, 6, 2)]
    [InlineData(2, 12, 6, 1)]
    [InlineData(6, 12, 2, 3)]
    [InlineData(2, 12, 2, 4)]
    [InlineData(6, 8, 6, 0)]
    public void TheBoundingBoxClassFollowsTheGamesComparisonChain(byte x, byte y, byte z, int expected)
    {
        Assert.Equal(expected, TacticsTileHeader.Classify(x, y, z));
    }

    [Fact]
    public void IsTileRecognisesTheFramingAndRejectsEverythingElse()
    {
        Assert.True(TacticsTileFile.IsTile(Tile(10, 0)));
        Assert.False(TacticsTileFile.IsTile(TacticsSyntheticBytes.Tag("tiledata", "1")));
        Assert.False(TacticsTileFile.IsTile([0x50, 0x4B, 0x03, 0x04]));
        Assert.False(TacticsTileFile.IsTile([]));
    }
}