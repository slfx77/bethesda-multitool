using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the FO4 WATR DNAM decode (xEdit wbDefinitionsFO4, 201 bytes; layout byte-verified against
///     retail <c>Fallout4.esm</c> — see <c>fo4_water_pixel_shader_decompiled.txt</c>): fog-block colors +
///     depth ranges + alphas, physical Reflectivity/Fresnel + Reflection color, Sun Specular
///     Power/Magnitude, the field-grouped noise layers (all WindDirs, then all WindSpeeds, …), and the
///     silt block. The canonical keys feed the shared <c>WaterAppearance</c> decode; the FO4-only keys
///     drive <c>WaterShaderVariant.Fo4Water</c>.
/// </summary>
public sealed class Fallout4WaterDataTests
{
    private static byte[] BuildFallout4Dnam(bool bigEndian = false)
    {
        var d = new byte[201];
        WriteSingle(d, 0, 1087f, bigEndian); // Depth Amount
        d[4] = 0x3A;
        d[5] = 0x35;
        d[6] = 0x21; // Shallow Color
        d[8] = 0x3A;
        d[9] = 0x39;
        d[10] = 0x29; // Deep Color
        WriteSingle(d, 12, 64f, bigEndian); // Color Shallow Range
        WriteSingle(d, 16, 512f, bigEndian); // Color Deep Range
        WriteSingle(d, 20, 0.15f, bigEndian); // Shallow Alpha
        WriteSingle(d, 24, 0.9f, bigEndian); // Deep Alpha
        WriteSingle(d, 28, 32f, bigEndian); // Alpha Shallow Range
        WriteSingle(d, 32, 256f, bigEndian); // Alpha Deep Range
        d[36] = 0x12;
        d[37] = 0x34;
        d[38] = 0x56; // Underwater Color
        WriteSingle(d, 40, 0.7f, bigEndian); // Underwater Fog Amount
        WriteSingle(d, 44, 12f, bigEndian); // Underwater Near Fog
        WriteSingle(d, 48, 345f, bigEndian); // Underwater Far Fog
        WriteSingle(d, 52, 0.8f, bigEndian); // Normal Magnitude
        WriteSingle(d, 56, 21f, bigEndian); // Shallow Normal Falloff
        WriteSingle(d, 60, 87f, bigEndian); // Deep Normal Falloff
        WriteSingle(d, 64, 0.3732f, bigEndian); // Reflectivity Amount
        WriteSingle(d, 68, 0.0145f, bigEndian); // Fresnel Amount
        WriteSingle(d, 72, 432f, bigEndian); // Surface Effect Falloff
        WriteSingle(d, 76, 1.1f, bigEndian); // Displacement Force
        WriteSingle(d, 80, 2.2f, bigEndian); // Displacement Velocity
        WriteSingle(d, 84, 3.3f, bigEndian); // Displacement Falloff
        WriteSingle(d, 88, 4.4f, bigEndian); // Displacement Dampener
        WriteSingle(d, 92, 5.5f, bigEndian); // Displacement Starting Size
        d[96] = 0x51;
        d[97] = 0x62;
        d[98] = 0x73; // Reflection Color
        WriteSingle(d, 100, 951f, bigEndian); // Sun Specular Power
        WriteSingle(d, 104, 8.803f, bigEndian); // Sun Specular Magnitude
        WriteSingle(d, 108, 71f, bigEndian); // Sun Sparkle Power
        WriteSingle(d, 112, 6.25f, bigEndian); // Sun Sparkle Magnitude
        WriteSingle(d, 116, 700f, bigEndian); // Interior Specular Radius
        WriteSingle(d, 120, 2.75f, bigEndian); // Interior Specular Brightness
        WriteSingle(d, 124, 44f, bigEndian); // Interior Specular Power
        // Noise layers are grouped BY FIELD (all WindDirs, then WindSpeeds, Amplitudes, UVScales).
        WriteSingle(d, 128, 10f, bigEndian); // L1 Wind Dir
        WriteSingle(d, 132, 20f, bigEndian); // L2 Wind Dir
        WriteSingle(d, 136, 30f, bigEndian); // L3 Wind Dir
        WriteSingle(d, 140, 0.05f, bigEndian); // L1 Wind Speed
        WriteSingle(d, 144, 0.06f, bigEndian); // L2 Wind Speed
        WriteSingle(d, 148, 0.07f, bigEndian); // L3 Wind Speed
        WriteSingle(d, 152, 0.4f, bigEndian); // L1 Amplitude
        WriteSingle(d, 156, 0.5f, bigEndian); // L2 Amplitude
        WriteSingle(d, 160, 0.6f, bigEndian); // L3 Amplitude
        WriteSingle(d, 164, 100f, bigEndian); // L1 UV Scale
        WriteSingle(d, 168, 200f, bigEndian); // L2 UV Scale
        WriteSingle(d, 172, 300f, bigEndian); // L3 UV Scale
        WriteSingle(d, 176, 0.11f, bigEndian); // L1 Falloff
        WriteSingle(d, 180, 0.22f, bigEndian); // L2 Falloff
        WriteSingle(d, 184, 0.33f, bigEndian); // L3 Falloff
        WriteSingle(d, 188, 1f, bigEndian); // Silt Amount
        d[192] = 0x60;
        d[193] = 0x4F;
        d[194] = 0x1A; // Light (silt) Color
        d[196] = 0x2F;
        d[197] = 0x2B;
        d[198] = 0x1A; // Dark (silt) Color
        d[200] = 1; // Screen Space Reflections bool
        return d;
    }

    private static void WriteSingle(byte[] destination, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteSingleBigEndian(destination.AsSpan(offset, 4), value);
        else
            BinaryPrimitives.WriteSingleLittleEndian(destination.AsSpan(offset, 4), value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadFallout4WaterData_DecodesColorsRangesAndSpecular(bool bigEndian)
    {
        var props = MiscEnvironmentHandler.ReadFallout4WaterData(BuildFallout4Dnam(bigEndian), bigEndian);

        Assert.Equal(0x00_21_35_3Au, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.Equal(0x00_29_39_3Au, Assert.IsType<uint>(props["DeepColor"]));
        Assert.Equal(0x00_73_62_51u, Assert.IsType<uint>(props["ReflectionColor"]));
        Assert.Equal(0x00_1A_2B_2Fu, Assert.IsType<uint>(props["DarkSiltColor"]));
        Assert.Equal(0x00_1A_4F_60u, Assert.IsType<uint>(props["LightSiltColor"]));

        Assert.Equal(64f, Assert.IsType<float>(props["ColorShallowRange"]), 3);
        Assert.Equal(512f, Assert.IsType<float>(props["ColorDeepRange"]), 3);
        Assert.Equal(0.15f, Assert.IsType<float>(props["ShallowAlpha"]), 4);
        Assert.Equal(0.9f, Assert.IsType<float>(props["DeepAlpha"]), 4);
        Assert.Equal(32f, Assert.IsType<float>(props["AlphaShallowRange"]), 3);
        Assert.Equal(256f, Assert.IsType<float>(props["AlphaDeepRange"]), 3);

        Assert.Equal(1087f, Assert.IsType<float>(props["DepthAmount"]), 3);
        Assert.Equal(0.3732f, Assert.IsType<float>(props["ReflectivityAmount"]), 4);
        Assert.Equal(0.0145f, Assert.IsType<float>(props["FresnelAmount"]), 4);
        Assert.Equal(951f, Assert.IsType<float>(props["SunPower"]), 3);
        Assert.Equal(8.803f, Assert.IsType<float>(props["SunSpecularMagnitude"]), 3);
        Assert.Equal(1f, Assert.IsType<float>(props["SiltAmount"]), 4);
        Assert.Equal(0x00_56_34_12u, Assert.IsType<uint>(props["UnderwaterColor"]));
        Assert.Equal(0.7f, Assert.IsType<float>(props["UnderwaterFogAmount"]), 4);
        Assert.Equal(21f, Assert.IsType<float>(props["ShallowNormalFalloff"]), 3);
        Assert.Equal(432f, Assert.IsType<float>(props["SurfaceEffectFalloff"]), 3);
        Assert.Equal(5.5f, Assert.IsType<float>(props["DisplacementStartingSize"]), 4);
        Assert.Equal(71f, Assert.IsType<float>(props["SunSparklePower"]), 3);
        Assert.Equal(2.75f, Assert.IsType<float>(props["InteriorSpecularBrightness"]), 4);
        Assert.True(Assert.IsType<bool>(props["ScreenSpaceReflections"]));

        // Field-grouped layers land under the canonical per-layer keys.
        Assert.Equal(20f, Assert.IsType<float>(props["NoiseLayer2WindDir"]), 3);
        Assert.Equal(0.07f, Assert.IsType<float>(props["NoiseLayer3WindSpeed"]), 4);
        Assert.Equal(0.4f, Assert.IsType<float>(props["NoiseLayer1AmpScale"]), 4);
        Assert.Equal(300f, Assert.IsType<float>(props["NoiseLayer3UVScale"]), 3);
        Assert.Equal(0.22f, Assert.IsType<float>(props["NoiseLayer2Falloff"]), 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadFallout4WaterData_FeedsWaterAppearanceAndFo4SurfaceParams(bool bigEndian)
    {
        var appearance = WaterAppearance
            .FromVisualProperties(MiscEnvironmentHandler.ReadFallout4WaterData(
                BuildFallout4Dnam(bigEndian), bigEndian), null);

        Assert.NotNull(appearance);
        Assert.Equal((R: (byte)0x3A, G: (byte)0x35, B: (byte)0x21), appearance!.Shallow);
        Assert.Equal((R: (byte)0x3A, G: (byte)0x39, B: (byte)0x29), appearance.Deep);
        Assert.Equal((R: (byte)0x51, G: (byte)0x62, B: (byte)0x73), appearance.Reflection);
        Assert.Equal((R: (byte)0x2F, G: (byte)0x2B, B: (byte)0x1A), appearance.DarkSilt);
        Assert.Equal((R: (byte)0x60, G: (byte)0x4F, B: (byte)0x1A), appearance.LightSilt);
        Assert.Equal((R: (byte)0x12, G: (byte)0x34, B: (byte)0x56), appearance.Underwater);

        var s = appearance.Surface;
        Assert.Equal(951f, s.SunPower, 3);
        Assert.Equal(8.803f, s.SunSpecularMagnitude, 3);
        Assert.Equal(0.15f, s.ShallowAlpha, 4);
        Assert.Equal(0.9f, s.DeepAlpha, 4);
        Assert.Equal(64f, s.ColorShallowRange, 3);
        Assert.Equal(512f, s.ColorDeepRange, 3);
        Assert.Equal(32f, s.AlphaShallowRange, 3);
        Assert.Equal(256f, s.AlphaDeepRange, 3);
        Assert.Equal(1f, s.SiltAmount, 4);
        Assert.Equal(1087f, s.DepthAmount, 3);
        // FO4 layers ride the same WaterNoiseLayer slots (wind dir/speed + amplitude drive the scroll).
        Assert.Equal(10f, s.Layer1.WindDirDegrees, 3);
        Assert.Equal(0.06f, s.Layer2.WindSpeed, 4);
        Assert.Equal(0.6f, s.Layer3.AmpScale, 4);
        Assert.Equal(0.11f, s.Layer1.Falloff, 4);
        Assert.Equal(0.8f, s.NormalMagnitude, 4);
        Assert.Equal(0.7f, s.UnderwaterFogAmount, 4);
        Assert.Equal(71f, s.SunSparklePower, 3);
        Assert.Equal(700f, s.InteriorSpecularRadius, 3);
        Assert.True(s.ScreenSpaceReflections);
        Assert.True(s.HasAuthoredNoiseLayers);
    }

    [Fact]
    public void FnvAndOblivionSurfaceParams_KeepFo4FieldDefaults()
    {
        // The FO4-only fields default inert for every other game's decode path — the FNV/Oblivion
        // shader permutations never read them, and Default must stay byte-identical.
        var def = WaterSurfaceParams.Default;
        Assert.Equal(0f, def.SunSpecularMagnitude);
        Assert.Equal(0f, def.SiltAmount);
        Assert.Equal(1f, def.ShallowAlpha);
        Assert.Equal(1f, def.DeepAlpha);
        Assert.Equal(0f, def.ColorShallowRange);
        Assert.Equal(0f, def.ColorDeepRange);
    }

}
