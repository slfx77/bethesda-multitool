using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the Oblivion WATR DATA struct decode (xEdit wbDefinitionsTES4): SunPower@16, Reflectivity@20,
///     Fresnel@24, and the three wbByteColors Shallow@44 / Deep@48 / Reflection@52. The colors land under
///     the same dictionary keys the FNV DNAM decoder uses so WaterAppearance handles Oblivion unchanged
///     (OBLIV-2 — without this every Oblivion water rendered with the default tint).
/// </summary>
public sealed class OblivionWaterDataTests
{
    private static byte[] BuildOblivionData()
    {
        var d = new byte[102];
        BitConverter.GetBytes(50f).CopyTo(d, 16);     // SunPower
        BitConverter.GetBytes(0.5f).CopyTo(d, 20);    // Reflectivity
        BitConverter.GetBytes(0.025f).CopyTo(d, 24);  // Fresnel
        // wbByteColors are a byte sequence R,G,B,A.
        d[44] = 0x10; d[45] = 0x20; d[46] = 0x30; d[47] = 0xFF; // Shallow
        d[48] = 0x40; d[49] = 0x50; d[50] = 0x60; d[51] = 0xFF; // Deep
        d[52] = 0x70; d[53] = 0x80; d[54] = 0x90; d[55] = 0xFF; // Reflection
        return d;
    }

    [Fact]
    public void ReadOblivionWaterData_DecodesColorsAndSurfaceScalars()
    {
        var props = MiscEnvironmentHandler.ReadOblivionWaterData(BuildOblivionData(), isBigEndian: false);

        Assert.Equal(50f, Assert.IsType<float>(props["SunPower"]), 4);
        Assert.Equal(0.5f, Assert.IsType<float>(props["ReflectivityAmount"]), 4);
        Assert.Equal(0.025f, Assert.IsType<float>(props["FresnelAmount"]), 4);

        // Packed R | G<<8 | B<<16 — the form WaterAppearance.ExtractColor expects.
        Assert.Equal(0x00_30_20_10u, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.Equal(0x00_60_50_40u, Assert.IsType<uint>(props["DeepColor"]));
        Assert.Equal(0x00_90_80_70u, Assert.IsType<uint>(props["ReflectionColor"]));
    }

    [Fact]
    public void ReadOblivionWaterData_FeedsWaterAppearanceColors()
    {
        // End-to-end: the Oblivion DATA dict drives WaterAppearance exactly like the FNV DNAM dict.
        var appearance = BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance
            .FromVisualProperties(MiscEnvironmentHandler.ReadOblivionWaterData(BuildOblivionData(), false), null);

        Assert.NotNull(appearance);
        Assert.Equal((R: (byte)0x10, G: (byte)0x20, B: (byte)0x30), appearance!.Shallow);
        Assert.Equal((R: (byte)0x40, G: (byte)0x50, B: (byte)0x60), appearance.Deep);
        Assert.Equal((R: (byte)0x70, G: (byte)0x80, B: (byte)0x90), appearance.Reflection);
    }
}
