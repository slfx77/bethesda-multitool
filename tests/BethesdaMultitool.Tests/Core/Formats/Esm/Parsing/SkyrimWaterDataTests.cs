using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the Skyrim WATR DNAM struct decode (xEdit wbDefinitionsTES5): the three wbByteColors at
///     40/44/48 (Shallow/Deep/Reflection) — shifted +4 from FNV by the extra float at +28 — plus
///     SunPower@16, Reflectivity@20, Fresnel@24, under-water-fog Near@144/Far@148 (→ DepthFalloff),
///     Specular Power@156 (→ Shininess), and the noise layer UV/wind/amplitude fields. Without this,
///     Skyrim water never matched the FNV 196-byte DNAM case and rendered with the default tint.
///     Grounded in tools/GhidraProject/skyrim_water_pixel_shader_decompiled.txt.
/// </summary>
public sealed class SkyrimWaterDataTests
{
    private static byte[] BuildSkyrimDnam(bool bigEndian)
    {
        var d = new byte[228]; // Skyrim LE DNAM length
        WriteFloat(d, 16, 50f, bigEndian); // Sun Specular Power
        WriteFloat(d, 20, 0.5f, bigEndian); // Reflectivity
        WriteFloat(d, 24, 0.02f, bigEndian); // Fresnel
        // Sentinel at +36 (FNV's Shallow offset) must NOT be read as Shallow — proves the +4 shift.
        d[36] = 0xAA;
        d[37] = 0xBB;
        d[38] = 0xCC;
        d[39] = 0xFF;
        d[40] = 0x10;
        d[41] = 0x20;
        d[42] = 0x30;
        d[43] = 0xFF; // Shallow
        d[44] = 0x40;
        d[45] = 0x50;
        d[46] = 0x60;
        d[47] = 0xFF; // Deep
        d[48] = 0x70;
        d[49] = 0x80;
        d[50] = 0x90;
        d[51] = 0xFF; // Reflection
        WriteFloat(d, 144, 0f, bigEndian); // under-water fog Near → DepthFalloffStart
        WriteFloat(d, 148, 1000f, bigEndian); // under-water fog Far → DepthFalloffEnd
        WriteFloat(d, 156, 100f, bigEndian); // Specular Power → Shininess
        WriteFloat(d, 172, 80f, bigEndian); // Noise Layer 1 UV Scale
        return d;
    }

    private static void WriteFloat(byte[] data, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadSkyrimWaterData_DecodesColorsAndScalarsInBothEndianModes(bool bigEndian)
    {
        var props = MiscEnvironmentHandler.ReadSkyrimWaterData(BuildSkyrimDnam(bigEndian), bigEndian);

        Assert.Equal(50f, Assert.IsType<float>(props["SunPower"]), 4);
        Assert.Equal(0.5f, Assert.IsType<float>(props["ReflectivityAmount"]), 4);
        Assert.Equal(0.02f, Assert.IsType<float>(props["FresnelAmount"]), 4);

        // Colors at +40/+44/+48 (NOT the FNV +36/+40/+44), packed R | G<<8 | B<<16.
        Assert.Equal(0x00_30_20_10u, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.Equal(0x00_60_50_40u, Assert.IsType<uint>(props["DeepColor"]));
        Assert.Equal(0x00_90_80_70u, Assert.IsType<uint>(props["ReflectionColor"]));

        Assert.Equal(0f, Assert.IsType<float>(props["DepthFalloffStart"]), 4);
        Assert.Equal(1000f, Assert.IsType<float>(props["DepthFalloffEnd"]), 4);
        Assert.Equal(100f, Assert.IsType<float>(props["Shininess"]), 4);
        Assert.Equal(80f, Assert.IsType<float>(props["NoiseLayer1UVScale"]), 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadSkyrimWaterData_FeedsWaterAppearanceColors(bool bigEndian)
    {
        var appearance = WaterAppearance
            .FromVisualProperties(MiscEnvironmentHandler.ReadSkyrimWaterData(
                BuildSkyrimDnam(bigEndian), bigEndian), null);

        Assert.NotNull(appearance);
        Assert.Equal((R: (byte)0x10, G: (byte)0x20, B: (byte)0x30), appearance!.Shallow);
        Assert.Equal((R: (byte)0x40, G: (byte)0x50, B: (byte)0x60), appearance.Deep);
        Assert.Equal((R: (byte)0x70, G: (byte)0x80, B: (byte)0x90), appearance.Reflection);
    }

    [Fact]
    public void ReadSkyrimWaterData_TruncatedDnam_KeepsColorsDropsDeepFields()
    {
        // A truncated DNAM (optionalFromElement) carries the colors but not the later noise/specular
        // fields. The in-bounds guard must add only what's present (no IndexOutOfRange, no junk keys).
        var d = new byte[52];
        d[40] = 0x11;
        d[41] = 0x22;
        d[42] = 0x33;
        d[43] = 0xFF; // Shallow only-region

        var props = MiscEnvironmentHandler.ReadSkyrimWaterData(d, false);

        Assert.Equal(0x00_33_22_11u, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.False(props.ContainsKey("Shininess"));
        Assert.False(props.ContainsKey("NoiseLayer1UVScale"));
        Assert.False(props.ContainsKey("DepthFalloffEnd"));
    }
}