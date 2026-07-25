using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models.Records.World;

public sealed class WaterAppearanceTests
{
    [Fact]
    public void FromVisualProperties_DecodesThreeColorsAsRgbAndKeepsNoiseTexture()
    {
        // Canonical-LE packing 0xAABBGGRR — distinct primes per channel catch any byte swap.
        var props = new Dictionary<string, object?>
        {
            ["ShallowColor"] = 0xAA_33_55_77u,
            ["DeepColor"] = 0xFF_11_22_44u,
            ["ReflectionColor"] = 0x00_99_88_66u
        };

        var appearance = WaterAppearance.FromVisualProperties(props, "water\\noise.dds");

        Assert.NotNull(appearance);
        Assert.Equal((R: (byte)0x77, G: (byte)0x55, B: (byte)0x33), appearance!.Shallow);
        Assert.Equal((R: (byte)0x44, G: (byte)0x22, B: (byte)0x11), appearance.Deep);
        Assert.Equal((R: (byte)0x66, G: (byte)0x88, B: (byte)0x99), appearance.Reflection);
        Assert.Equal("water\\noise.dds", appearance.NoiseTexture);
    }

    [Fact]
    public void FromVisualProperties_ReflectionFallsBackToShallowWhenMissing()
    {
        var appearance = WaterAppearance.FromVisualProperties(
            new Dictionary<string, object?> { ["ShallowColor"] = 0x00_80_60_40u }, null);

        Assert.NotNull(appearance);
        // Deep mirrors Shallow (missing endpoint), Reflection falls back to Shallow.
        Assert.Equal(appearance!.Shallow, appearance.Deep);
        Assert.Equal(appearance.Shallow, appearance.Reflection);
        Assert.Equal((R: (byte)0x40, G: (byte)0x60, B: (byte)0x80), appearance.Shallow);
    }

    [Fact]
    public void FromVisualProperties_ReturnsNullWhenBothPrimaryColorsAbsentOrZero()
    {
        Assert.Null(WaterAppearance.FromVisualProperties(new Dictionary<string, object?>(), null));
        Assert.Null(WaterAppearance.FromVisualProperties(
            new Dictionary<string, object?> { ["ShallowColor"] = 0u, ["DeepColor"] = 0u }, null));
        Assert.Null(WaterAppearance.FromVisualProperties(null, "x.dds"));
    }

    [Fact]
    public void FromVisualProperties_DecodesTheThreeRealNoiseLayersAndShadingScalars()
    {
        // Field names match the DNAM/WATR schema (SubrecordCellAndMiscSchemas). The decompile of
        // WaterShader::SetupGeometryConstants confirms three noise layers, so all three must survive.
        var props = new Dictionary<string, object?>
        {
            ["ShallowColor"] = 0x00_30_20_10u,
            ["NormalsUVScale"] = 2.5f,
            ["FresnelAmount"] = 0.4f,
            ["ReflectivityAmount"] = 0.8f,
            ["Shininess"] = 120f,
            ["SunPower"] = 50f,
            ["DepthFalloffStart"] = 16f,
            ["DepthFalloffEnd"] = 1024f,
            ["NoiseLayer1UVScale"] = 1.1f,
            ["NoiseLayer1WindDir"] = 30f,
            ["NoiseLayer1WindSpeed"] = 2f,
            ["NoiseLayer1AmpScale"] = 0.9f,
            ["NoiseLayer2UVScale"] = 1.9f,
            ["NoiseLayer3WindDir"] = 270f
        };

        var appearance = WaterAppearance.FromVisualProperties(props, null);

        Assert.NotNull(appearance);
        var s = appearance!.Surface;
        Assert.Equal(2.5f, s.NormalsUvScale, 4);
        Assert.Equal(0.4f, s.FresnelAmount, 4);
        Assert.Equal(0.8f, s.ReflectivityAmount, 4);
        Assert.Equal(120f, s.Shininess, 4);
        Assert.Equal(50f, s.SunPower, 4);
        Assert.Equal(16f, s.DepthFalloffStart, 4);
        Assert.Equal(1024f, s.DepthFalloffEnd, 4);

        Assert.Equal(new WaterNoiseLayer(1.1f, 30f, 2f, 0.9f), s.Layer1);
        // Present-but-partial layer 2: explicit UVScale, the rest fall back to the per-field default.
        Assert.Equal(1.9f, s.Layer2.UvScale, 4);
        Assert.Equal(WaterSurfaceParams.Default.Layer2.AmpScale, s.Layer2.AmpScale, 4);
        // Layer 3: only WindDir given; the other three fall back to defaults.
        Assert.Equal(270f, s.Layer3.WindDirDegrees, 4);
        Assert.Equal(WaterSurfaceParams.Default.Layer3.UvScale, s.Layer3.UvScale, 4);
    }

    [Fact]
    public void FromVisualProperties_UsesDefaultSurfaceWhenScalarsAbsent()
    {
        // Only a color is present (proto/partial DNAM) → every shading scalar falls back to Default.
        var appearance = WaterAppearance.FromVisualProperties(
            new Dictionary<string, object?> { ["ShallowColor"] = 0x00_30_20_10u }, null);

        Assert.NotNull(appearance);
        Assert.Equal(WaterSurfaceParams.Default, appearance!.Surface);
    }

    [Fact]
    public void FromWaterRecord_NullRecordReturnsNull()
    {
        Assert.Null(WaterAppearance.FromWaterRecord(null));
    }

    [Fact]
    public void FromWaterRecord_PullsColorsFromDnamAndNnamFromNoiseTexture()
    {
        var water = new WaterRecord
        {
            FormId = 0x55,
            NoiseTexture = "water\\water.dds",
            VisualProperties = new Dictionary<string, object?>
            {
                ["ShallowColor"] = 0x00_30_20_10u,
                ["DeepColor"] = 0x00_60_50_40u
            }
        };

        var appearance = WaterAppearance.FromWaterRecord(water);

        Assert.NotNull(appearance);
        Assert.Equal((R: (byte)0x10, G: (byte)0x20, B: (byte)0x30), appearance!.Shallow);
        Assert.Equal((R: (byte)0x40, G: (byte)0x50, B: (byte)0x60), appearance.Deep);
        Assert.Equal("water\\water.dds", appearance.NoiseTexture);
    }

    [Fact]
    public void FromWaterRecord_PreservesRepeatedNormalTexturesInSourceOrder()
    {
        var water = new WaterRecord
        {
            FormId = 0x56,
            NoiseTexture = "water\\normal01.dds",
            NormalTextures =
            [
                "water\\normal01.dds",
                "water\\normal02.dds",
                "water\\normal03.dds"
            ],
            VisualProperties = new Dictionary<string, object?>
            {
                ["ShallowColor"] = 0x00_30_20_10u
            }
        };

        var appearance = WaterAppearance.FromWaterRecord(water);

        Assert.NotNull(appearance);
        Assert.Equal("water\\normal01.dds", appearance!.NoiseTexture);
        Assert.Equal(water.NormalTextures, appearance.NormalTextures);
    }

    [Fact]
    public void FromWaterRecord_OblivionSurfaceTextureStaysSeparateFromGlobalNormalAnimation()
    {
        var water = new WaterRecord
        {
            FormId = 0x57,
            SurfaceTexture = "water\\water00.dds",
            VisualProperties = new Dictionary<string, object?>
            {
                ["ShallowColor"] = 0x00_30_20_10u
            }
        };

        var appearance = WaterAppearance.FromWaterRecord(water);

        Assert.NotNull(appearance);
        Assert.Null(appearance!.NoiseTexture);
        Assert.Empty(appearance.NormalTextures!);
        Assert.Equal("water\\water00.dds", appearance.SurfaceTexture);
    }

    [Fact]
    public void FromWaterRecord_TnamFilenameDoesNotReclassifyBloodAsLava()
    {
        // Retail Oblivion Blood uses Landscape\Oblivion\TerrainHDOblivionLava01.dds as TNAM.
        // TNAM used to be projected into NoiseTexture, whose name-based fallback then incorrectly
        // classified this WATR as lava. The record identity remains authoritative.
        var water = new WaterRecord
        {
            FormId = 0x58,
            EditorId = "Blood",
            SurfaceTexture = @"Landscape\Oblivion\TerrainHDOblivionLava01.dds",
            VisualProperties = new Dictionary<string, object?>
            {
                ["ShallowColor"] = 0x00_30_20_10u
            }
        };

        var appearance = WaterAppearance.FromWaterRecord(water);

        Assert.NotNull(appearance);
        Assert.False(appearance!.IsLava);
        Assert.Equal(water.SurfaceTexture, appearance.SurfaceTexture);
    }

    private static WaterRecord WaterWith(string? editorId, byte flags)
    {
        return new WaterRecord
        {
            FormId = 0x1,
            EditorId = editorId,
            WaterFlags = [flags],
            // FromWaterRecord needs at least one color or it returns null.
            VisualProperties = new Dictionary<string, object?> { ["ShallowColor"] = 0x00_30_20_10u }
        };
    }

    [Fact]
    public void FromWaterRecord_LavaByName_IsLavaAndCausesDamage()
    {
        // Every shipped Oblivion lava WATR is FNAM "Causes Damage" (bit 0) AND editor-id'd "…Lava…".
        var appearance = WaterAppearance.FromWaterRecord(WaterWith("OblivionCitadelLavaPlane", 0x01));

        Assert.NotNull(appearance);
        Assert.True(appearance!.IsLava);
        Assert.True(appearance.CausesDamage);
    }

    [Fact]
    public void FromWaterRecord_DamagingOil_CausesDamageButNotLava()
    {
        // OblivionOil01 also sets Causes Damage but is not lava — name disambiguates so oil doesn't glow.
        var appearance = WaterAppearance.FromWaterRecord(WaterWith("OblivionOil01", 0x01));

        Assert.NotNull(appearance);
        Assert.True(appearance!.CausesDamage);
        Assert.False(appearance.IsLava);
    }

    [Fact]
    public void FromWaterRecord_OrdinaryWater_NeitherLavaNorDamage()
    {
        // Reflective-only flag (bit 1) → no damage; no "lava" token → not lava.
        var appearance = WaterAppearance.FromWaterRecord(WaterWith("DefaultWater", 0x02));

        Assert.NotNull(appearance);
        Assert.False(appearance!.CausesDamage);
        Assert.False(appearance.IsLava);
    }

    [Fact]
    public void FromWaterRecord_LavaTokenIsCaseInsensitive()
    {
        Assert.True(WaterAppearance.FromWaterRecord(WaterWith("camoranLAVA02", 0x01))!.IsLava);
    }

    [Fact]
    public void FromWaterRecord_ColorlessLava_StillFlaggedWithMoltenFallback()
    {
        // The shipping Oblivion lava planes carry no DATA colors (engine colors them from TNAM). They must
        // still come back flagged IsLava with a reddish molten palette, not null → default water.
        var water = new WaterRecord { FormId = 0x1, EditorId = "OblivionCitadelLavaPlane", WaterFlags = [0x01] };

        var a = WaterAppearance.FromWaterRecord(water);

        Assert.NotNull(a);
        Assert.True(a!.IsLava);
        Assert.True(a.CausesDamage);
        Assert.True(a.Shallow.R > a.Shallow.B); // molten (reddish), not a default blue water tint
    }

    [Fact]
    public void FromWaterRecord_ColorlessOrdinaryWater_ReturnsNull()
    {
        // No colors and not lava → null, so the caller keeps its own default tint (unchanged behavior).
        var water = new WaterRecord { FormId = 0x1, EditorId = "DungeonWater01", WaterFlags = [0x02] };

        Assert.Null(WaterAppearance.FromWaterRecord(water));
    }
}