using System.Text;
using BethesdaMultitool.CLI.Rendering.Sprite;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Hand-built vectors for Fallout Tactics <c>&lt;zar&gt;</c> images, authored from the codec as
///     read off BOS.exe (<c>FUN_006f7d50</c> reader, <c>FUN_006f8c50</c> row rule, <c>FUN_006f9a10</c>
///     32-bit decode, <c>FUN_006f9cd0</c> index + alpha decode). Every byte below and every expected
///     pixel was written by hand from those rules — none is derived from the reader under test.
/// </summary>
public sealed class TacticsZarImageTests
{
    /// <summary>
    ///     A 4x3 image exercising all four modes (control byte = count &lt;&lt; 2 | mode):
    ///     row 0: opaque x2 [1, 2], transparent x2;
    ///     row 1: translucent x1 [2, 0x80], shadow x3 [0x40, 0xC0, 0xFF];
    ///     row 2: the encoder's EMPTY translucent byte, shadow x1 [0x20], opaque x3 [0, 1, 2].
    /// </summary>
    private static readonly byte[] FourByThreeBlock =
    [
        0x09, 1, 2, 0x08,
        0x06, 2, 0x80, 0x0F, 0x40, 0xC0, 0xFF,
        0x02, 0x07, 0x20, 0x0D, 0, 1, 2
    ];

    /// <summary>The 4x3 image as R, G, B, A — written by hand from the palette and the rules, shadow colour = entry 5.</summary>
    private static readonly byte[] FourByThreeRgba =
    [
        30, 20, 10, 255, 255, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0,
        255, 0, 0, 0x80, 50, 100, 200, 0x40, 50, 100, 200, 0xC0, 50, 100, 200, 255,
        50, 100, 200, 0x20, 0, 0, 0, 255, 30, 20, 10, 255, 255, 0, 0, 255
    ];

    // Palette in the STORED order B, G, R, x. Entry 0 black; entry 1 = B 10, G 20, R 30 (stamp 1);
    // entry 2 = pure red (stamp 0xFF); entry 5 = B 200, G 100, R 50 (stamp 9) — the shadow colour.
    private static byte[] StoredPalette()
    {
        var palette = new byte[1024];
        palette[3] = 0xFF;
        palette[4] = 10;
        palette[5] = 20;
        palette[6] = 30;
        palette[7] = 1;
        palette[10] = 255;
        palette[11] = 0xFF;
        palette[20] = 200;
        palette[21] = 100;
        palette[22] = 50;
        palette[23] = 9;
        return palette;
    }

    private static byte[] Zar(
        string version,
        uint width,
        uint height,
        byte[] block,
        bool withPalette = true,
        bool? withShadowByte = null,
        byte shadowIndex = 5,
        int lengthBias = 0)
    {
        var b = new List<byte>();
        b.AddRange("<zar>"u8);
        b.Add(0);
        b.AddRange(Encoding.ASCII.GetBytes(version));
        b.Add(0);
        b.AddRange(BitConverter.GetBytes(width));
        b.AddRange(BitConverter.GetBytes(height));
        b.Add((byte)(withPalette ? 1 : 0));
        if (withPalette)
        {
            b.AddRange(BitConverter.GetBytes(256u));
            b.AddRange(StoredPalette());
            if (withShadowByte ?? version == "4")
            {
                b.Add(shadowIndex);
            }
        }

        b.AddRange(BitConverter.GetBytes((uint)(block.Length + lengthBias)));
        b.AddRange(block);
        return [.. b];
    }

    /// <summary>An empty save slot: 0x0, no palette, length 0 — 21 bytes and nothing else.</summary>
    private static byte[] EmptySlot()
    {
        var b = new List<byte>("<zar>\04\0"u8.ToArray());
        b.AddRange(new byte[13]);
        return [.. b];
    }

    [Fact]
    public void Decode_AllFourModes_MatchTheHandWrittenRgba()
    {
        var bytes = Zar("4", 4, 3, FourByThreeBlock);

        var image = TacticsZarImage.Parse(bytes, "v.zar");
        var texture = image.Decode();

        Assert.Equal(4, image.Version);
        Assert.Equal(4, image.Width);
        Assert.Equal(3, image.Height);
        Assert.True(image.HasPalette);
        Assert.Equal(256, image.PaletteEntries);
        Assert.Equal(5, image.ShadowIndex);
        Assert.Equal(TacticsZarImage.RetailPixelBlockOffset + FourByThreeBlock.Length, image.RecordLength);
        Assert.Equal(1068, bytes.Length);
        Assert.Equal(FourByThreeRgba, texture.Pixels);
    }

    [Fact]
    public void DecodeIndexed_ProducesTheGamesIndexAndAlphaPlanes()
    {
        // FUN_006f9cd0: mode 0 -> index 0 / alpha 0; mode 3 -> index = shadowIndex / alpha = payload.
        var image = TacticsZarImage.Parse(Zar("4", 4, 3, FourByThreeBlock), "v.zar");

        var planes = image.DecodeIndexed();

        Assert.Equal(new byte[] { 1, 2, 0, 0, 2, 5, 5, 5, 5, 0, 1, 2 }, planes.Indices.Indices);
        Assert.Equal(new byte[] { 255, 255, 0, 0, 0x80, 0x40, 0xC0, 0xFF, 0x20, 255, 255, 255 }, planes.Alpha);
    }

    [Fact]
    public void CountRuns_CountsControlBytesIncludingTheEmptyOne()
    {
        var image = TacticsZarImage.Parse(Zar("4", 4, 3, FourByThreeBlock), "v.zar");

        var census = image.CountRuns();

        Assert.Equal(new TacticsZarImage.RunCensus(1, 2, 2, 2), census);
        Assert.Equal(7, census.Total);
    }

    [Fact]
    public void Read_Version3_HasNoShadowByteAndShadowsThroughEntryZero()
    {
        // 2x1: shadow x1 [0x80] then opaque x1 [1]. Entry 0 is black.
        byte[] block = [0x07, 0x80, 0x05, 1];
        var bytes = Zar("3", 2, 1, block);

        var image = TacticsZarImage.Parse(bytes, "v3.zar");
        var texture = image.Decode();

        Assert.Equal(3, image.Version);
        Assert.Equal(0, image.ShadowIndex);
        Assert.Equal(TacticsZarImage.RetailPixelBlockOffset - 1 + block.Length, image.RecordLength);
        Assert.Equal(new byte[] { 0, 0, 0, 0x80, 30, 20, 10, 255 }, texture.Pixels);
    }

    [Fact]
    public void Read_TheShadowByteIsGatedOnTheVersion_EitherWayRoundFails()
    {
        // ⚑ The control that settles the gate: a v3 stream WITH a shadow byte and a v4 stream
        // WITHOUT one both put the length field one byte off, and nothing downstream tiles.
        // (Retail: reading one on the 4,534 v3 tile streams scores 0/4,534.)
        byte[] block = [0x07, 0x80, 0x05, 1];

        Assert.False(TacticsZarImage.TryParse(Zar("3", 2, 1, block, withShadowByte: true), "v3.zar", out _, out _));
        Assert.False(TacticsZarImage.TryParse(Zar("4", 2, 1, block, withShadowByte: false), "v4.zar", out _, out _));
        Assert.True(TacticsZarImage.TryParse(Zar("4", 2, 1, block), "v4.zar", out _, out _));
    }

    [Fact]
    public void Read_APaletteLessStream_HasNoShadowByteAndDecodesThroughAnExternalPalette()
    {
        // A sprite-embedded <zar>: hasPalette 0, so no count, no entries and NO shadow byte even at
        // v4 (the byte is read inside the hasPalette branch of FUN_006f7d50). 3x1: opaque [1],
        // shadow [0x40], transparent.
        byte[] block = [0x05, 1, 0x07, 0x40, 0x04];
        var bytes = Zar("4", 3, 1, block, false);

        var image = TacticsZarImage.Parse(bytes, "layer.zar");

        Assert.False(image.HasPalette);
        Assert.Equal(0, image.ShadowIndex);
        Assert.Equal(21 + block.Length, image.RecordLength);
        Assert.Throws<InvalidOperationException>(() => image.Decode());

        var rgb = new byte[Palette.RgbByteCount];
        rgb[0] = 9;
        rgb[1] = 8;
        rgb[2] = 7; // entry 0 = the shadow colour here
        rgb[3] = 1;
        rgb[4] = 2;
        rgb[5] = 3; // entry 1
        var texture = image.Decode(Palette.FromRgb8(rgb));

        Assert.Equal(new byte[] { 1, 2, 3, 255, 9, 8, 7, 0x40, 0, 0, 0, 0 }, texture.Pixels);
    }

    [Fact]
    public void Read_AnEmptySlotIsExactlyTwentyOneBytes()
    {
        // 3/3 empty slots in the outer save header and 8/8 in the embedded one: 0x0, no palette,
        // then the unconditional u32 length = 0. HasImage is false; the record still steps past.
        var bytes = EmptySlot();

        var image = TacticsZarImage.Parse(bytes, "empty.zar");

        Assert.False(image.HasImage);
        Assert.False(image.HasPalette);
        Assert.Equal(TacticsZarImage.EmptyRecordLength, image.RecordLength);
        Assert.Equal(21, bytes.Length);
        Assert.Throws<InvalidOperationException>(() => image.Decode());
    }

    [Fact]
    public void Decode_ARunPastTheRowsEdgeIsTheGamesError()
    {
        // 4x1 with a run of 5: FUN_006f8c50's "ZAR run went past edge of image".
        var image = TacticsZarImage.Parse(Zar("4", 4, 1, [0x15, 1, 1, 1, 1, 1]), "v.zar");

        var error = Assert.Throws<InvalidDataException>(() => image.Decode());
        Assert.Contains(TacticsZarImage.RunPastEdgeMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RunsNeverCrossRows()
    {
        // 2x2 written as ONE run of 4: a reader that lets runs flow across rows would accept it;
        // the game closes a row only at x == width exactly, and x = 4 on a width of 2 is the error.
        var image = TacticsZarImage.Parse(Zar("4", 2, 2, [0x11, 1, 1, 1, 1]), "v.zar");

        var error = Assert.Throws<InvalidDataException>(() => image.Decode());
        Assert.Contains(TacticsZarImage.RunPastEdgeMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RefusesABlockWithBytesLeftOver()
    {
        var image = TacticsZarImage.Parse(Zar("4", 2, 1, [0x09, 1, 2, 0x99]), "v.zar");

        var error = Assert.Throws<InvalidDataException>(() => image.Decode());
        Assert.Contains("1 left over", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RefusesABlockThatRunsOutMidRowOrMidPayload()
    {
        // Row 0 closes at 2 of 4 with the block spent.
        Assert.Throws<InvalidDataException>(() =>
            TacticsZarImage.Parse(Zar("4", 4, 1, [0x09, 1, 2]), "v.zar").Decode());
        // A translucent run of 2 needs 4 payload bytes; 3 are there.
        Assert.Throws<InvalidDataException>(() =>
            TacticsZarImage.Parse(Zar("4", 2, 1, [0x0A, 1, 2, 3]), "v.zar").Decode());
    }

    [Fact]
    public void Read_RefusesALengthThatDoesNotFitAndALooseFileWithATail()
    {
        Assert.False(TacticsZarImage.TryParse(Zar("4", 2, 1, [0x09, 1, 2], lengthBias: 1), "v.zar", out _,
            out var error));
        Assert.Contains("pixel block", error, StringComparison.Ordinal);

        // Parse is the loose-file route: the record must end where the file does (839/839 do).
        var trailing = new List<byte>(Zar("4", 2, 1, [0x09, 1, 2])) { 0xEE };
        Assert.False(TacticsZarImage.TryParse(trailing.ToArray(), "v.zar", out _, out error));
        Assert.Contains("ends at", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_RefusesVersionsOutsideTwoToFour()
    {
        Assert.False(TacticsZarImage.TryParse(Zar("5", 2, 1, [0x09, 1, 2], withShadowByte: true), "v.zar", out _,
            out var error));
        Assert.Contains(TacticsZarImage.UnknownVersionMessage, error, StringComparison.Ordinal);
        Assert.False(TacticsZarImage.TryParse(Zar("1", 2, 1, [0x09, 1, 2], withShadowByte: false), "v.zar", out _,
            out error));
        Assert.Contains(TacticsZarImage.UnknownVersionMessage, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_LeavesTheCursorAfterTheRecordSoAHeaderCanHoldEight()
    {
        var bytes = new List<byte>(Zar("4", 2, 1, [0x09, 1, 2]));
        bytes.AddRange(EmptySlot());
        bytes.Add(0xEE);
        var cursor = new TacticsCursor(bytes.ToArray(), "v");

        var first = TacticsZarImage.Read(cursor);
        var second = TacticsZarImage.Read(cursor);

        Assert.True(first.HasImage);
        Assert.False(second.HasImage);
        Assert.Equal(0xEE, cursor.U8());
    }

    [Fact]
    public void SpritePipeline_RoutesAZarByContentToTheRgbaPath()
    {
        // The browser/CLI seam: a <zar> is recognised by its tag, needs no companion palette, and
        // comes out with the per-pixel alpha an index plane could not carry.
        var decoded = SpriteRenderPipeline.DecodeBytes(Zar("4", 4, 3, FourByThreeBlock), "v.zar", _ => null);

        Assert.Equal(ClassicSpriteGame.Tactics, decoded.Game);
        Assert.Equal("embedded", decoded.PaletteSource);
        var frame = Assert.Single(decoded.Frames);
        Assert.Equal(4, frame.Texture.Width);
        Assert.Equal(3, frame.Texture.Height);
        Assert.Equal(FourByThreeRgba, frame.Texture.Pixels);
    }

    [Fact]
    public void SpritePipeline_TacticsOnANonZarSaysSo()
    {
        Assert.Throws<InvalidDataException>(() =>
            SpriteRenderPipeline.DecodeBytes([1, 2, 3, 4], "x.bin", _ => null, ClassicSpriteGame.Tactics));
    }
}