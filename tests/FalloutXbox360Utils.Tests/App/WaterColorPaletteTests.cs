using FalloutXbox360Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

public sealed class WaterColorPaletteTests
{
    // Byte-order claim: per the decompile of TESWaterSystem::UpdateWaterShaderProperties,
    // the packed uint32 in WATR DNAM maps to RGB as R = packed & 0xFF, G = (packed>>8) & 0xFF,
    // B = (packed>>16) & 0xFF. This test pins down the convention — if anyone refactors the
    // schema reader or the extractor and the bytes get reordered, this fails immediately
    // instead of waiting for a visual regression on screen.
    [Fact]
    public void FromVisualProperties_ExtractsByteZeroAsRedByteOneAsGreenByteTwoAsBlue()
    {
        // 0xAABBGGRR = canonical-LE packing: R in lowest byte, A in highest.
        // Distinct primes per channel make any 2-byte swap detectable.
        const uint shallow = 0xAA_33_55_77;
        const uint deep = 0xFF_11_22_44;

        var props = new Dictionary<string, object?>
        {
            ["ShallowColor"] = shallow,
            ["DeepColor"] = deep
        };

        var palette = WaterColorPalette.FromVisualProperties(props);

        Assert.NotNull(palette);
        Assert.Equal((R: (byte)0x77, G: (byte)0x55, B: (byte)0x33), palette!.Shallow);
        Assert.Equal((R: (byte)0x44, G: (byte)0x22, B: (byte)0x11), palette.Deep);
    }

    [Fact]
    public void FromVisualProperties_ReturnsNullWhenPropsMissing()
    {
        Assert.Null(WaterColorPalette.FromVisualProperties(null));
    }

    [Fact]
    public void FromVisualProperties_ReturnsNullWhenBothColorsAbsentOrZero()
    {
        Assert.Null(WaterColorPalette.FromVisualProperties(
            new Dictionary<string, object?>()));
        Assert.Null(WaterColorPalette.FromVisualProperties(
            new Dictionary<string, object?> { ["ShallowColor"] = 0u, ["DeepColor"] = 0u }));
    }

    [Fact]
    public void FromVisualProperties_MirrorsMissingEndpointSoLerpDegeneratesToSingleColor()
    {
        // Only ShallowColor present — DeepColor mirrors it. This means the overlay's
        // Shallow→Deep lerp produces the same colour everywhere instead of fading toward
        // an uninitialised/black Deep endpoint.
        var palette = WaterColorPalette.FromVisualProperties(
            new Dictionary<string, object?> { ["ShallowColor"] = 0x00_80_60_40u });

        Assert.NotNull(palette);
        Assert.Equal(palette!.Shallow, palette.Deep);
        Assert.Equal((R: (byte)0x40, G: (byte)0x60, B: (byte)0x80), palette.Shallow);
    }
}
