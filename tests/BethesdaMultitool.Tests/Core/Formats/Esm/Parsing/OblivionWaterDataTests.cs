using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
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
    private static byte[] BuildOblivionData(bool bigEndian)
    {
        var d = new byte[102];
        WriteFloat(d, 0, 15f, bigEndian); // Wind Velocity
        WriteFloat(d, 4, 90f, bigEndian); // Wind Direction
        WriteFloat(d, 8, 1.25f, bigEndian); // Wave Amplitude
        WriteFloat(d, 12, 2.5f, bigEndian); // Wave Frequency
        WriteFloat(d, 16, 50f, bigEndian); // SunPower
        WriteFloat(d, 20, 0.5f, bigEndian); // Reflectivity
        WriteFloat(d, 24, 0.025f, bigEndian); // Fresnel
        WriteFloat(d, 28, 0.03f, bigEndian); // Scroll X
        WriteFloat(d, 32, -0.04f, bigEndian); // Scroll Y
        WriteFloat(d, 36, 27852.8f, bigEndian); // Fog Near
        WriteFloat(d, 40, 163840f, bigEndian); // Fog Far
        // wbByteColors are a byte sequence R,G,B,A.
        d[44] = 0x10;
        d[45] = 0x20;
        d[46] = 0x30;
        d[47] = 0xFF; // Shallow
        d[48] = 0x40;
        d[49] = 0x50;
        d[50] = 0x60;
        d[51] = 0xFF; // Deep
        d[52] = 0x70;
        d[53] = 0x80;
        d[54] = 0x90;
        d[55] = 0xFF; // Reflection
        d[56] = 73; // Texture Blend is a percentage byte, not a float.
        for (var i = 0; i < 10; i++)
        {
            WriteFloat(d, 60 + i * 4, i + 1, bigEndian); // Rain then displacement controls.
        }

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
    public void ReadOblivionWaterData_DecodesLosslessDataInBothEndianModes(bool bigEndian)
    {
        var props = MiscEnvironmentHandler.ReadOblivionWaterData(BuildOblivionData(bigEndian), bigEndian);

        Assert.Equal(15f, Assert.IsType<float>(props["WindVelocity"]), 4);
        Assert.Equal(90f, Assert.IsType<float>(props["WindDirection"]), 4);
        Assert.Equal(1.25f, Assert.IsType<float>(props["WaveAmplitude"]), 4);
        Assert.Equal(2.5f, Assert.IsType<float>(props["WaveFrequency"]), 4);
        Assert.Equal(50f, Assert.IsType<float>(props["SunPower"]), 4);
        Assert.Equal(0.5f, Assert.IsType<float>(props["ReflectivityAmount"]), 4);
        Assert.Equal(0.025f, Assert.IsType<float>(props["FresnelAmount"]), 4);
        Assert.Equal(0.03f, Assert.IsType<float>(props["ScrollXSpeed"]), 4);
        Assert.Equal(-0.04f, Assert.IsType<float>(props["ScrollYSpeed"]), 4);
        Assert.Equal(27852.8f, Assert.IsType<float>(props["FogNear"]), 1);
        Assert.Equal(163840f, Assert.IsType<float>(props["FogFar"]), 1);
        Assert.Equal(0.73f, Assert.IsType<float>(props["TextureBlend"]), 4);
        Assert.Equal(1f, Assert.IsType<float>(props["RainForce"]), 4);
        Assert.Equal(5f, Assert.IsType<float>(props["RainStartingSize"]), 4);
        Assert.Equal(6f, Assert.IsType<float>(props["DisplacementForce"]), 4);
        Assert.Equal(10f, Assert.IsType<float>(props["DisplacementStartingSize"]), 4);

        // The old port aliased unrelated TES4 controls onto FNV's layer/depth fields. Keeping those
        // aliases would silently feed the wrong shader constants even after the raw DATA survived.
        Assert.False(props.ContainsKey("NoiseLayer1WindSpeed"));
        Assert.False(props.ContainsKey("NoiseLayer1WindDir"));
        Assert.False(props.ContainsKey("DepthFalloffStart"));
        Assert.False(props.ContainsKey("DepthFalloffEnd"));

        // Packed R | G<<8 | B<<16 — the form WaterAppearance.ExtractColor expects.
        Assert.Equal(0x00_30_20_10u, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.Equal(0x00_60_50_40u, Assert.IsType<uint>(props["DeepColor"]));
        Assert.Equal(0x00_90_80_70u, Assert.IsType<uint>(props["ReflectionColor"]));
    }

    // TES4 WATR DATA is a SIZE-VERSIONED union (xEdit wbDefinitionsTES4: 102/86/62/42/2) whose
    // short forms are OLDER LAYOUTS, not byte prefixes — byte-verified against retail Oblivion.esm
    // (2026-08-18 adversarial pass): Blood/CamoranLava02 (42) put the colors at @28/32/36 with
    // damage @40; SwampWater/MS31Water (86) store THREE-float rain@60../displacement@72.. sims with
    // damage @84; OblivionOil01 (62) ends colors+blend with damage @60. The pre-fix ≥100 gate
    // dropped all six to FNV fallback tints; a naive "truncated prefix" reading misattributes the
    // 86-form's sim floats and misses the 42-form entirely.

    [Fact]
    public void ReadOblivionWaterData_42ByteVintage_ColorsFollowTheSevenFloats()
    {
        var d = new byte[42];
        WriteFloat(d, 16, 50f, false); // SunPower
        WriteFloat(d, 20, 0.5f, false); // Reflectivity
        WriteFloat(d, 24, 0.025f, false); // Fresnel
        d[28] = 60;
        d[29] = 0;
        d[30] = 0;
        d[31] = 0xFF; // Shallow (Blood's authored red)
        d[32] = 150;
        d[33] = 0;
        d[34] = 0;
        d[35] = 0xFF; // Deep
        d[36] = 255;
        d[37] = 102;
        d[38] = 102;
        d[39] = 0xFF; // Reflection
        var props = MiscEnvironmentHandler.ReadOblivionWaterData(d, false);

        Assert.Equal(0x00_00_00_3Cu, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.Equal(0x00_00_00_96u, Assert.IsType<uint>(props["DeepColor"]));
        Assert.Equal(0x00_66_66_FFu, Assert.IsType<uint>(props["ReflectionColor"]));
        Assert.Equal(0.5f, Assert.IsType<float>(props["ReflectivityAmount"]), 4);
        // This vintage carries no scroll/fog/blend/sim fields — they must stay absent, not garbage.
        Assert.False(props.ContainsKey("ScrollXSpeed"));
        Assert.False(props.ContainsKey("FogNear"));
        Assert.False(props.ContainsKey("TextureBlend"));
        Assert.False(props.ContainsKey("RainForce"));
    }

    [Fact]
    public void ReadOblivionWaterData_62ByteVintage_EndsAtTheTextureBlend()
    {
        var props = MiscEnvironmentHandler.ReadOblivionWaterData(
            BuildOblivionData(false).AsSpan(0, 62), false);

        Assert.Equal(0x00_30_20_10u, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.Equal(0x00_60_50_40u, Assert.IsType<uint>(props["DeepColor"]));
        Assert.Equal(0x00_90_80_70u, Assert.IsType<uint>(props["ReflectionColor"]));
        Assert.Equal(0.73f, Assert.IsType<float>(props["TextureBlend"]), 4);
        Assert.False(props.ContainsKey("RainForce"));
        Assert.False(props.ContainsKey("DisplacementForce"));
    }

    [Fact]
    public void ReadOblivionWaterData_86ByteVintage_ReadsThreeFloatSims()
    {
        var d = new byte[86];
        BuildOblivionData(false).AsSpan(0, 60).CopyTo(d);
        WriteFloat(d, 60, 0.1f, false); // Rain Force
        WriteFloat(d, 64, 0.6f, false); // Rain Velocity
        WriteFloat(d, 68, 0.985f, false); // Rain Falloff
        WriteFloat(d, 72, 0.4f, false); // Displacement Force — @72 is RainDampener in the 102-form
        WriteFloat(d, 76, 0.6f, false); // Displacement Velocity
        WriteFloat(d, 80, 0.985f, false); // Displacement Falloff
        var props = MiscEnvironmentHandler.ReadOblivionWaterData(d, false);

        Assert.Equal(0x00_30_20_10u, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.Equal(0.1f, Assert.IsType<float>(props["RainForce"]), 4);
        Assert.Equal(0.985f, Assert.IsType<float>(props["RainFalloff"]), 4);
        Assert.Equal(0.4f, Assert.IsType<float>(props["DisplacementForce"]), 4);
        Assert.Equal(0.985f, Assert.IsType<float>(props["DisplacementFalloff"]), 4);
        // The five-float-era fields do not exist in this vintage.
        Assert.False(props.ContainsKey("RainDampener"));
        Assert.False(props.ContainsKey("RainStartingSize"));
        Assert.False(props.ContainsKey("DisplacementDampener"));
        Assert.False(props.ContainsKey("DisplacementStartingSize"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOblivionWaterData_FeedsWaterAppearanceColorsAndControls(bool bigEndian)
    {
        var appearance = WaterAppearance
            .FromVisualProperties(MiscEnvironmentHandler.ReadOblivionWaterData(
                BuildOblivionData(bigEndian), bigEndian), null);

        Assert.NotNull(appearance);
        Assert.Equal((R: (byte)0x10, G: (byte)0x20, B: (byte)0x30), appearance!.Shallow);
        Assert.Equal((R: (byte)0x40, G: (byte)0x50, B: (byte)0x60), appearance.Deep);
        Assert.Equal((R: (byte)0x70, G: (byte)0x80, B: (byte)0x90), appearance.Reflection);
        Assert.Equal(1.25f, appearance.Surface.WaveAmplitude, 4);
        Assert.Equal(2.5f, appearance.Surface.WaveFrequency, 4);
        Assert.Equal(0.03f, appearance.Surface.ScrollXSpeed, 4);
        Assert.Equal(-0.04f, appearance.Surface.ScrollYSpeed, 4);
        Assert.Equal(27852.8f, appearance.Surface.FogNear, 1);
        Assert.Equal(163840f, appearance.Surface.FogFar, 1);
        Assert.Equal(0.73f, appearance.Surface.TextureBlend, 4);
    }
}