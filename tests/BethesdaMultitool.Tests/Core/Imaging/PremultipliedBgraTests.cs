using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Imaging;

/// <summary>
///     Pins the RGBA → premultiplied-BGRA conversion the GUI surfaces need. The channel swap is the
///     obvious half; the half that actually bites is zeroing colour under zero alpha.
/// </summary>
public sealed class PremultipliedBgraTests
{
    [Fact]
    public void FromRgba_SwapsRedAndBlueAndKeepsOpaquePixelsExact()
    {
        // R=10 G=20 B=30 A=255 -> B,G,R,A with no scaling.
        var bgra = PremultipliedBgra.FromRgba([10, 20, 30, 255]);

        Assert.Equal<byte[]>([30, 20, 10, 255], bgra);
    }

    [Fact]
    public void FromRgba_PremultipliesPartialAlpha()
    {
        var bgra = PremultipliedBgra.FromRgba([200, 100, 50, 128]);

        Assert.Equal((byte)(50 * 128 / 255), bgra[0]);
        Assert.Equal((byte)(100 * 128 / 255), bgra[1]);
        Assert.Equal((byte)(200 * 128 / 255), bgra[2]);
        Assert.Equal((byte)128, bgra[3]);
    }

    [Fact]
    public void FromRgba_ZeroesTheKeyColourUnderZeroAlpha()
    {
        // ⚠ This is the real defect this conversion prevents. Palette.WithTransparentIndex clears
        // ONLY alpha, and Fallout's COLOR.PAL holds FF FF FF at index 0 — the transparent index. A
        // premultiplied surface would render that as white ghosting through every transparent pixel.
        var bgra = PremultipliedBgra.FromRgba([255, 255, 255, 0]);

        Assert.Equal<byte[]>([0, 0, 0, 0], bgra);
    }

    [Fact]
    public void FromRgba_ConvertsEveryPixelOfAMultiPixelBuffer()
    {
        var bgra = PremultipliedBgra.FromRgba([1, 2, 3, 255, 4, 5, 6, 255]);

        Assert.Equal<byte[]>([3, 2, 1, 255, 6, 5, 4, 255], bgra);
    }

    [Fact]
    public void FromRgba_BufferThatIsNotWholePixels_Throws()
    {
        Assert.Throws<ArgumentException>(() => PremultipliedBgra.FromRgba(new byte[7]));
    }

    [Fact]
    public void FromRgba_EmptyBuffer_ReturnsEmpty()
    {
        Assert.Empty(PremultipliedBgra.FromRgba([]));
    }
}