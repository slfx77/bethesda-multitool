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