using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Travels.Stormhold;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for <see cref="StormholdCusImage" />, shaped after the 37 retail
///     Stormhold <c>.cus</c> resources surveyed 2026-09-05. Two things carry the weight here and
///     neither is visible in a "does it parse" check: the palette entry is a BIG-endian u16 read
///     as R = bits 11-8, G = 7-4, B = 3-0 (so the fixtures below use asymmetric colours a swapped
///     order could not reproduce), and the transparent slot is the FIRST match on the key colour
///     rather than slot 0.
/// </summary>
public sealed class StormholdCusImageTests
{
    /// <summary>
    ///     Assembles a <c>.cus</c> exactly as the format tiles it: 12-byte header, N big-endian
    ///     palette entries, then one index byte per pixel.
    /// </summary>
    private static byte[] BuildCus(int width, int height, byte flag, ushort key, ushort[] palette, byte[] pixels)
    {
        var bytes = new byte[12 + 2 * palette.Length + pixels.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), height);
        bytes[8] = flag;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(9), key);
        bytes[11] = (byte)palette.Length;
        for (var i = 0; i < palette.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12 + i * 2), palette[i]);
        }

        pixels.CopyTo(bytes, 12 + 2 * palette.Length);
        return bytes;
    }

    /// <summary>
    ///     The reference 2x2: four asymmetric entries and one pixel each. Slot 0 is the key, so it
    ///     is also the transparent slot in this file.
    /// </summary>
    private static byte[] ReferenceImage(ushort key = 0x0F00, byte flag = 1)
    {
        return BuildCus(2, 2, flag, key, [0x0F00, 0x00F0, 0x0008, 0x0123], [0, 1, 2, 3]);
    }

    [Fact]
    public void Parse_ReadsTheHeaderAndTilesTheFileExactly()
    {
        var image = StormholdCusImage.Parse(ReferenceImage(), "reference.cus");

        Assert.Equal("reference.cus", image.Name);
        Assert.Equal(2, image.Width);
        Assert.Equal(2, image.Height);
        Assert.True(image.HasKeyColour);
        Assert.Equal(0x0F00, image.KeyColour444);
        Assert.Equal(new ushort[] { 0x0F00, 0x00F0, 0x0008, 0x0123 }, image.Palette444);
        Assert.Equal(24, ReferenceImage().Length);
    }

    /// <summary>
    ///     Pins BOTH the channel order and the <c>c4 * 17</c> nibble replication. 0x0123 decodes to
    ///     (17, 34, 51): red from the HIGH nibble of the low byte, blue from the lowest. A BGR
    ///     reading would give (51, 34, 17) and a shift-based expansion (16, 32, 48).
    /// </summary>
    [Fact]
    public void Parse_ExpandsEachChannelByNibbleReplicationInRgbOrder()
    {
        var image = StormholdCusImage.Parse(ReferenceImage(), "reference.cus");

        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)0), image.Palette.GetEntry(0));
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), image.Palette.GetEntry(1));
        Assert.Equal(((byte)0, (byte)0, (byte)136, (byte)255), image.Palette.GetEntry(2));
        Assert.Equal(((byte)17, (byte)34, (byte)51, (byte)255), image.Palette.GetEntry(3));
    }

    /// <summary>Slots past N are unreachable, but must still be well-formed transparent black.</summary>
    [Fact]
    public void Parse_FillsTheUnusedPaletteTailWithTransparentBlack()
    {
        var image = StormholdCusImage.Parse(ReferenceImage(), "reference.cus");

        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), image.Palette.GetEntry(4));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), image.Palette.GetEntry(255));
    }

    [Fact]
    public void Parse_CopiesThePixelBytesVerbatimAsTheIndexPlane()
    {
        var image = StormholdCusImage.Parse(ReferenceImage(), "reference.cus");

        Assert.Equal(2, image.Bitmap.Width);
        Assert.Equal(2, image.Bitmap.Height);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, image.Bitmap.Indices);
    }

    /// <summary>
    ///     The retail trap: <c>trainerfembelt1</c> and <c>trainerfemneck</c> key on slot 4 and
    ///     <c>wardenbody2f2half</c> on slot 2, so the transparent slot must be searched for.
    /// </summary>
    [Fact]
    public void Parse_TakesTheTransparentSlotFromTheKeyColourNotFromIndexZero()
    {
        var image = StormholdCusImage.Parse(ReferenceImage(0x0008), "keyed.cus");

        Assert.Equal(2, image.TransparentIndex);
        Assert.Equal((byte)255, image.Palette.GetEntry(0).A);
        Assert.Equal((byte)255, image.Palette.GetEntry(1).A);
        Assert.Equal((byte)0, image.Palette.GetEntry(2).A);
        Assert.Equal((byte)255, image.Palette.GetEntry(3).A);
    }

    [Fact]
    public void Parse_TakesTheFirstSlotMatchingTheKeyWhenTheKeyRepeats()
    {
        var bytes = BuildCus(2, 1, 1, 0x0123, [0x0F00, 0x0123, 0x0123], [1, 2]);

        var image = StormholdCusImage.Parse(bytes, "duplicate.cus");

        Assert.Equal(1, image.TransparentIndex);
        Assert.Equal((byte)0, image.Palette.GetEntry(1).A);
        Assert.Equal((byte)255, image.Palette.GetEntry(2).A);
    }

    /// <summary>A clear flag byte is legal and leaves the image fully opaque (never seen on retail).</summary>
    [Fact]
    public void Parse_LeavesEveryEntryOpaqueWhenTheTransparencyFlagIsClear()
    {
        var image = StormholdCusImage.Parse(ReferenceImage(flag: 0), "opaque.cus");

        Assert.False(image.HasKeyColour);
        Assert.Equal(-1, image.TransparentIndex);
        Assert.Equal((byte)255, image.Palette.GetEntry(0).A);
    }

    /// <summary>A key colour absent from the palette is legal too: no slot goes transparent.</summary>
    [Fact]
    public void Parse_ReportsNoTransparentSlotWhenTheKeyIsNotInThePalette()
    {
        var image = StormholdCusImage.Parse(ReferenceImage(0x0ABC), "unkeyed.cus");

        Assert.True(image.HasKeyColour);
        Assert.Equal(-1, image.TransparentIndex);
        Assert.Equal((byte)255, image.Palette.GetEntry(0).A);
    }

    [Fact]
    public void Parse_RejectsAFileShorterThanTheHeader()
    {
        var bytes = new byte[11];

        var error = Assert.Throws<InvalidDataException>(() => StormholdCusImage.Parse(bytes, "short.cus"));
        Assert.Contains("short.cus", error.Message, StringComparison.Ordinal);
        Assert.Contains("byte 11", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public void Parse_RejectsAWidthOutsideTheProbeBounds(int width)
    {
        var bytes = ReferenceImage();
        BinaryPrimitives.WriteInt32BigEndian(bytes, width);

        var error = Assert.Throws<InvalidDataException>(() => StormholdCusImage.Parse(bytes, "width.cus"));
        Assert.Contains("width.cus", error.Message, StringComparison.Ordinal);
        Assert.Contains("byte 0", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public void Parse_RejectsAHeightOutsideTheProbeBounds(int height)
    {
        var bytes = ReferenceImage();
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), height);

        var error = Assert.Throws<InvalidDataException>(() => StormholdCusImage.Parse(bytes, "height.cus"));
        Assert.Contains("height.cus", error.Message, StringComparison.Ordinal);
        Assert.Contains("byte 4", error.Message, StringComparison.Ordinal);
    }

    /// <summary>N is a byte, so a corrupt count can claim a palette longer than the whole file.</summary>
    [Fact]
    public void Parse_RejectsAPaletteRunningPastTheEndOfTheFile()
    {
        var bytes = ReferenceImage();
        bytes[11] = 200;

        var error = Assert.Throws<InvalidDataException>(() => StormholdCusImage.Parse(bytes, "palette.cus"));
        Assert.Contains("palette.cus", error.Message, StringComparison.Ordinal);
        Assert.Contains("ends at byte 412", error.Message, StringComparison.Ordinal);
        Assert.Contains("24-byte file", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The exact tiling IS the format check — one trailing byte is enough to fail it.</summary>
    [Fact]
    public void Parse_RejectsAFileThatDoesNotTileExactly()
    {
        var bytes = new byte[ReferenceImage().Length + 1];
        ReferenceImage().CopyTo(bytes, 0);

        var error = Assert.Throws<InvalidDataException>(() => StormholdCusImage.Parse(bytes, "trailer.cus"));
        Assert.Contains("trailer.cus", error.Message, StringComparison.Ordinal);
        Assert.Contains("2x2 pixels from byte 20", error.Message, StringComparison.Ordinal);
        Assert.Contains("the file is 25", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAPixelIndexOutsideThePalette()
    {
        var bytes = BuildCus(2, 2, 1, 0x0F00, [0x0F00, 0x00F0, 0x0008, 0x0123], [0, 1, 2, 4]);

        var error = Assert.Throws<InvalidDataException>(() => StormholdCusImage.Parse(bytes, "index.cus"));
        Assert.Contains("index.cus", error.Message, StringComparison.Ordinal);
        Assert.Contains("pixel index 4 at byte 23", error.Message, StringComparison.Ordinal);
        Assert.Contains("4-entry palette", error.Message, StringComparison.Ordinal);
    }

    /// <summary>N = 0 cannot describe any pixel, so rule 5 rejects it for a non-empty image.</summary>
    [Fact]
    public void Parse_RejectsAnEmptyPaletteBecauseNoIndexCanSatisfyIt()
    {
        var bytes = BuildCus(2, 2, 0, 0, [], [0, 0, 0, 0]);

        Assert.Throws<InvalidDataException>(() => StormholdCusImage.Parse(bytes, "nopalette.cus"));
    }

    [Fact]
    public void TryProbe_AcceptsAWellFormedImage()
    {
        Assert.True(StormholdCusImage.TryProbe(ReferenceImage()));
    }

    [Fact]
    public void TryProbe_RejectsABufferShorterThanTheHeader()
    {
        Assert.False(StormholdCusImage.TryProbe(new byte[11]));
    }

    /// <summary>
    ///     The three neighbours a <c>.cus</c> shares its JAR directory with. A PNG and a
    ///     <c>.class</c> both open with a high bit set, so their "width" reads negative; a
    ///     <c>readUTF</c> <c>.dat</c> list opens with a small length word and a '/' name, giving a
    ///     width far past the cap. The full sweep found zero false positives over 2,147 files.
    /// </summary>
    [Theory]
    [InlineData(new byte[]
        { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 })]
    [InlineData(new byte[]
        { 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x34, 0x00, 0x2A, 0x0A, 0x00, 0x0C, 0x00, 0x22, 0x09 })]
    [InlineData(new byte[]
        { 0x00, 0x0B, 0x2F, 0x62, 0x61, 0x67, 0x73, 0x6D, 0x61, 0x6C, 0x6C, 0x00, 0x00, 0x0A, 0x2F, 0x62 })]
    public void TryProbe_RejectsTheOtherResourcesInTheSameJar(byte[] bytes)
    {
        Assert.False(StormholdCusImage.TryProbe(bytes));
    }

    /// <summary>A file whose arithmetic is one byte off is not a <c>.cus</c> — the probe is exact.</summary>
    [Fact]
    public void TryProbe_RejectsAnImageThatDoesNotTileExactly()
    {
        var bytes = new byte[ReferenceImage().Length + 1];
        ReferenceImage().CopyTo(bytes, 0);

        Assert.False(StormholdCusImage.TryProbe(bytes));
    }

    /// <summary>
    ///     Strip slicing: 6 columns as 3 frames gives 2-column frames, and frame 1 is columns 2..3
    ///     of every row.
    /// </summary>
    [Fact]
    public void Frame_SlicesTheStripIntoEqualWidthColumns()
    {
        var bytes = BuildCus(6, 2, 1, 0x0F00, [0x0F00, 0x0111, 0x0222, 0x0333, 0x0444, 0x0555],
            [0, 1, 2, 3, 4, 5, 5, 4, 3, 2, 1, 0]);
        var image = StormholdCusImage.Parse(bytes, "strip.cus");

        var frame = image.Frame(1, 3);

        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(new byte[] { 2, 3, 3, 2 }, frame.Indices);
    }

    /// <summary>
    ///     The <c>wardenheads4bit.cus</c> case: 137 pixels over 3 frames is 45 each and the game
    ///     clips the remaining columns. The slice reproduces that integer arithmetic rather than
    ///     rounding up or throwing.
    /// </summary>
    [Fact]
    public void Frame_DropsTheRemainderColumnsExactlyAsTheGameClipsThem()
    {
        var bytes = BuildCus(7, 1, 1, 0x0F00,
            [0x0F00, 0x0111, 0x0222, 0x0333, 0x0444, 0x0555, 0x0666], [0, 1, 2, 3, 4, 5, 6]);
        var image = StormholdCusImage.Parse(bytes, "warden.cus");

        Assert.Equal(new byte[] { 0, 1 }, image.Frame(0, 3).Indices);
        Assert.Equal(new byte[] { 4, 5 }, image.Frame(2, 3).Indices);
    }

    [Fact]
    public void Frame_WithOneFrameReturnsTheWholeImage()
    {
        var image = StormholdCusImage.Parse(ReferenceImage(), "reference.cus");

        var frame = image.Frame(0, 1);

        Assert.Equal(2, frame.Width);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, frame.Indices);
    }

    /// <summary>
    ///     The frame count is NOT in the file — the game supplies it — so every argument is
    ///     caller-supplied and has to be validated here.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 3)]
    [InlineData(-1, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void Frame_RejectsACountOrIndexTheImageCannotSatisfy(int index, int count)
    {
        var image = StormholdCusImage.Parse(ReferenceImage(), "reference.cus");

        Assert.Throws<ArgumentOutOfRangeException>(() => image.Frame(index, count));
    }
}