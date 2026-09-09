using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Png;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     Vectors for <see cref="BattlespireSaveImage" />: the 80 x 50 x555 thumbnail. The component
///     pins DISCRIMINATE the measured layout from the two refuted ones — under RGB565 0x7C00 would
///     not be pure red, and 0x10A4 splits to (4, 5, 4) under x555 but to (2, 2, 18) under
///     <c>BsiFile</c>'s HICL packing (<c>&gt;&gt;11, &gt;&gt;6, &gt;&gt;1</c>, bit 0 spare), so every
///     component of that pin separates the two.
/// </summary>
public sealed class BattlespireSaveImageTests
{
    private static byte[] Image(params (int Index, ushort Value)[] pixels)
    {
        var bytes = new byte[BattlespireSaveImage.FileLength];
        foreach (var (index, value) in pixels)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 2), value);
        }

        return bytes;
    }

    [Fact]
    public void Split_ReadsRedFromBits10To14GreenFrom5To9BlueFrom0To4()
    {
        // The fixture's pixel 0.
        Assert.Equal((4, 5, 4), BattlespireSaveImage.Split(0x10A4));
        Assert.Equal((31, 0, 0), BattlespireSaveImage.Split(0x7C00));
        Assert.Equal((0, 31, 0), BattlespireSaveImage.Split(0x03E0));
        Assert.Equal((0, 0, 31), BattlespireSaveImage.Split(0x001F));

        // Bit 15 is unused (set on 0/4,000 fixture pixels) and must not leak into a channel.
        Assert.Equal((0, 0, 0), BattlespireSaveImage.Split(0x8000));
    }

    [Fact]
    public void Widen_ReplicatesTheTopBitsSoFullScaleIs255()
    {
        Assert.Equal(0, BattlespireSaveImage.Widen(0));
        Assert.Equal(33, BattlespireSaveImage.Widen(4));
        Assert.Equal(41, BattlespireSaveImage.Widen(5));
        Assert.Equal(255, BattlespireSaveImage.Widen(31));
    }

    [Fact]
    public void ToRgba_LaysPixelsOutRowMajorWithAnEightyPixelStride()
    {
        var image = BattlespireSaveImage.Parse(Image((0, 0x10A4), (80, 0x7C00), (80 * 49 + 79, 0x7FFF)), "IMAGE.RAW");
        var rgba = image.ToRgba();

        Assert.Equal(80 * 50 * 4, rgba.Length);
        Assert.Equal<byte[]>([33, 41, 33, 255], rgba[..4]);
        Assert.Equal(0x7C00, image.PixelAt(0, 1));
        Assert.Equal<byte[]>([255, 0, 0, 255], rgba[(80 * 4)..(80 * 4 + 4)]);
        Assert.Equal<byte[]>([255, 255, 255, 255], rgba[^4..]);
    }

    [Fact]
    public void Parse_RejectsAnyOtherLength()
    {
        Assert.Throws<InvalidDataException>(() => BattlespireSaveImage.Parse(new byte[7_999], "IMAGE.RAW"));
        Assert.False(BattlespireSaveImage.IsSaveImage(new byte[8_001]));
        Assert.True(BattlespireSaveImage.IsSaveImage(new byte[8_000]));
    }

    [Fact]
    public void EncodePng_ProducesAnEightyByFiftyTruecolourPng()
    {
        var image = BattlespireSaveImage.Parse(Image((0, 0x10A4)), "IMAGE.RAW");
        var png = image.EncodePng();

        Assert.True(PngImageDecoder.HasPngSignature(png));
        var info = PngImageDecoder.ReadInfo(png);
        Assert.NotNull(info);
        Assert.Equal((80, 50), (info.Value.Width, info.Value.Height));
    }
}