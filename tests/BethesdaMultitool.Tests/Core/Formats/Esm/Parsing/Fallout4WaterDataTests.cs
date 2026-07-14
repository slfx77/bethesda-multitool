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
    private static byte[] BuildFallout4Dnam()
    {
        var d = new byte[201];
        BitConverter.GetBytes(1087f).CopyTo(d, 0);     // Depth Amount
        d[4] = 0x3A; d[5] = 0x35; d[6] = 0x21;         // Shallow Color
        d[8] = 0x3A; d[9] = 0x39; d[10] = 0x29;        // Deep Color
        BitConverter.GetBytes(64f).CopyTo(d, 12);      // Color Shallow Range
        BitConverter.GetBytes(512f).CopyTo(d, 16);     // Color Deep Range
        BitConverter.GetBytes(0.15f).CopyTo(d, 20);    // Shallow Alpha
        BitConverter.GetBytes(0.9f).CopyTo(d, 24);     // Deep Alpha
        BitConverter.GetBytes(32f).CopyTo(d, 28);      // Alpha Shallow Range
        BitConverter.GetBytes(256f).CopyTo(d, 32);     // Alpha Deep Range
        BitConverter.GetBytes(0.8f).CopyTo(d, 52);     // Normal Magnitude
        BitConverter.GetBytes(0.3732f).CopyTo(d, 64);  // Reflectivity Amount
        BitConverter.GetBytes(0.0145f).CopyTo(d, 68);  // Fresnel Amount
        d[96] = 0x51; d[97] = 0x62; d[98] = 0x73;      // Reflection Color
        BitConverter.GetBytes(951f).CopyTo(d, 100);    // Sun Specular Power
        BitConverter.GetBytes(8.803f).CopyTo(d, 104);  // Sun Specular Magnitude
        // Noise layers are grouped BY FIELD (all WindDirs, then WindSpeeds, Amplitudes, UVScales).
        BitConverter.GetBytes(10f).CopyTo(d, 128);     // L1 Wind Dir
        BitConverter.GetBytes(20f).CopyTo(d, 132);     // L2 Wind Dir
        BitConverter.GetBytes(30f).CopyTo(d, 136);     // L3 Wind Dir
        BitConverter.GetBytes(0.05f).CopyTo(d, 140);   // L1 Wind Speed
        BitConverter.GetBytes(0.06f).CopyTo(d, 144);   // L2 Wind Speed
        BitConverter.GetBytes(0.07f).CopyTo(d, 148);   // L3 Wind Speed
        BitConverter.GetBytes(0.4f).CopyTo(d, 152);    // L1 Amplitude
        BitConverter.GetBytes(0.5f).CopyTo(d, 156);    // L2 Amplitude
        BitConverter.GetBytes(0.6f).CopyTo(d, 160);    // L3 Amplitude
        BitConverter.GetBytes(100f).CopyTo(d, 164);    // L1 UV Scale
        BitConverter.GetBytes(200f).CopyTo(d, 168);    // L2 UV Scale
        BitConverter.GetBytes(300f).CopyTo(d, 172);    // L3 UV Scale
        BitConverter.GetBytes(1f).CopyTo(d, 188);      // Silt Amount
        d[192] = 0x60; d[193] = 0x4F; d[194] = 0x1A;   // Light (silt) Color
        d[196] = 0x2F; d[197] = 0x2B; d[198] = 0x1A;   // Dark (silt) Color
        d[200] = 1;                                    // Screen Space Reflections bool
        return d;
    }

    [Fact]
    public void ReadFallout4WaterData_DecodesColorsRangesAndSpecular()
    {
        var props = MiscEnvironmentHandler.ReadFallout4WaterData(BuildFallout4Dnam(), isBigEndian: false);

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

        // Field-grouped layers land under the canonical per-layer keys.
        Assert.Equal(20f, Assert.IsType<float>(props["NoiseLayer2WindDir"]), 3);
        Assert.Equal(0.07f, Assert.IsType<float>(props["NoiseLayer3WindSpeed"]), 4);
        Assert.Equal(0.4f, Assert.IsType<float>(props["NoiseLayer1AmpScale"]), 4);
        Assert.Equal(300f, Assert.IsType<float>(props["NoiseLayer3UVScale"]), 3);
    }

    [Fact]
    public void ReadFallout4WaterData_FeedsWaterAppearanceAndFo4SurfaceParams()
    {
        var appearance = BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance
            .FromVisualProperties(MiscEnvironmentHandler.ReadFallout4WaterData(BuildFallout4Dnam(), false), null);

        Assert.NotNull(appearance);
        Assert.Equal((R: (byte)0x3A, G: (byte)0x35, B: (byte)0x21), appearance!.Shallow);
        Assert.Equal((R: (byte)0x3A, G: (byte)0x39, B: (byte)0x29), appearance.Deep);
        Assert.Equal((R: (byte)0x51, G: (byte)0x62, B: (byte)0x73), appearance.Reflection);
        Assert.Equal((R: (byte)0x2F, G: (byte)0x2B, B: (byte)0x1A), appearance.DarkSilt);

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
    }

    [Fact]
    public void FnvAndOblivionSurfaceParams_KeepFo4FieldDefaults()
    {
        // The FO4-only fields default inert for every other game's decode path — the FNV/Oblivion
        // shader permutations never read them, and Default must stay byte-identical.
        var def = BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterSurfaceParams.Default;
        Assert.Equal(0f, def.SunSpecularMagnitude);
        Assert.Equal(0f, def.SiltAmount);
        Assert.Equal(1f, def.ShallowAlpha);
        Assert.Equal(1f, def.DeepAlpha);
        Assert.Equal(0f, def.ColorShallowRange);
        Assert.Equal(0f, def.ColorDeepRange);
    }

    [Fact]
    public void ReadFallout76WaterData_DecodesSharedCreationPrefixWithoutFo4Tail()
    {
        var d = BuildFallout4Dnam()[..148];

        var props = MiscEnvironmentHandler.ReadFallout76WaterData(d, isBigEndian: false);
        var appearance = BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance
            .FromVisualProperties(props, @"textures\water\WaterRainRipples.dds");

        Assert.Equal(0x00_21_35_3Au, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.Equal(0x00_29_39_3Au, Assert.IsType<uint>(props["DeepColor"]));
        Assert.Equal(0x00_73_62_51u, Assert.IsType<uint>(props["ReflectionColor"]));
        Assert.Equal(951f, Assert.IsType<float>(props["SunPower"]));
        Assert.Equal(8.803f, Assert.IsType<float>(props["SunSpecularMagnitude"]));
        Assert.DoesNotContain("SiltAmount", props.Keys);
        Assert.DoesNotContain("NoiseLayer1WindDir", props.Keys);

        Assert.NotNull(appearance);
        Assert.Equal(1087f, appearance.Surface.DepthAmount);
        Assert.Equal(8.803f, appearance.Surface.SunSpecularMagnitude);
        Assert.Equal(@"textures\water\WaterRainRipples.dds", appearance.NoiseTexture);
    }
}
