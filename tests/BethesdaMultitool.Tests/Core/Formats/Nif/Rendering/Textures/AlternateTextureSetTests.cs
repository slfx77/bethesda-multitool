using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Pins the render-facing alternate-texture set: its <see cref="AlternateTextureSet.VariantKey" />
///     is what makes the mesh cache treat one NIF's re-skins as distinct entries, so it must be stable
///     for identical overrides and different for different ones. Also verifies the "nothing to override"
///     collapse to null (the fast unchanged-cache-key path) and case-insensitive shape lookup.
/// </summary>
public sealed class AlternateTextureSetTests
{
    private static KeyValuePair<string, ShapeTextureOverride> Entry(string shape, string? diffuse, string? normal = null)
        => new(shape, new ShapeTextureOverride(diffuse, normal));

    [Fact]
    public void Create_SameOverrides_ProducesSameVariantKey()
    {
        var a = AlternateTextureSet.Create([Entry("BB04:13", "textures\\ads\\ultraluxe.dds")]);
        var b = AlternateTextureSet.Create([Entry("BB04:13", "textures\\ads\\ultraluxe.dds")]);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.VariantKey, b!.VariantKey);
    }

    [Fact]
    public void Create_VariantKey_IsIndependentOfEntryOrder()
    {
        var a = AlternateTextureSet.Create(
        [
            Entry("Shape:A", "textures\\a.dds"),
            Entry("Shape:B", "textures\\b.dds")
        ]);
        var b = AlternateTextureSet.Create(
        [
            Entry("Shape:B", "textures\\b.dds"),
            Entry("Shape:A", "textures\\a.dds")
        ]);

        Assert.Equal(a!.VariantKey, b!.VariantKey);
    }

    [Fact]
    public void Create_DifferentTexture_ProducesDifferentVariantKey()
    {
        var ultraLuxe = AlternateTextureSet.Create([Entry("BB04:13", "textures\\ads\\ultraluxe.dds")]);
        var fancyLads = AlternateTextureSet.Create([Entry("BB04:13", "textures\\ads\\fancylads.dds")]);

        Assert.NotEqual(ultraLuxe!.VariantKey, fancyLads!.VariantKey);
    }

    [Fact]
    public void Create_CaseOnlyPathDifference_SharesVariantKey()
    {
        // Path case shouldn't spawn redundant cache variants (BSA lookups are case-insensitive).
        var lower = AlternateTextureSet.Create([Entry("bb04:13", "textures\\ads\\ultraluxe.dds")]);
        var upper = AlternateTextureSet.Create([Entry("BB04:13", "TEXTURES\\ADS\\ULTRALUXE.DDS")]);

        Assert.Equal(lower!.VariantKey, upper!.VariantKey);
    }

    [Fact]
    public void Create_NoEffectiveOverrides_ReturnsNull()
    {
        Assert.Null(AlternateTextureSet.Create([]));
        Assert.Null(AlternateTextureSet.Create([Entry("BB04:13", null, null)]));
    }

    [Fact]
    public void Overrides_LookupIsCaseInsensitive()
    {
        var set = AlternateTextureSet.Create([Entry("BB04:13", "textures\\ads\\ultraluxe.dds")]);

        Assert.True(set!.Overrides.TryGetValue("bb04:13", out var ov));
        Assert.Equal("textures\\ads\\ultraluxe.dds", ov.Diffuse);
    }

    [Fact]
    public void Create_ColorRemapAlone_CreatesDistinctSharedVariants()
    {
        // FO4 MODC colorways: a bare color-remap index must create a variant (the shipping-crate
        // STATs carry MODC with no MSWP and no shape overrides), identical values must share one
        // cached mesh, and different rows (Blue 0.6875 vs Yellow 0.3125) must not collide.
        var blue = AlternateTextureSet.Create([], null, 0.6875f);
        var blueAgain = AlternateTextureSet.Create([], null, 0.6875f);
        var yellow = AlternateTextureSet.Create([], null, 0.3125f);

        Assert.NotNull(blue);
        Assert.Equal(0.6875f, blue!.GradientMapVOverride);
        Assert.Equal(blue.VariantKey, blueAgain!.VariantKey);
        Assert.NotEqual(blue.VariantKey, yellow!.VariantKey);
    }

    [Fact]
    public void Create_ExternalEmittanceAlone_CreatesColorKeyedVariants()
    {
        var warm = AlternateTextureSet.Create([], externalEmittanceColor: new Vector3(1f, 0.5f, 0.25f));
        var warmAgain = AlternateTextureSet.Create([], externalEmittanceColor: new Vector3(1f, 0.5f, 0.25f));
        var cool = AlternateTextureSet.Create([], externalEmittanceColor: new Vector3(0.25f, 0.5f, 1f));

        Assert.NotNull(warm);
        Assert.Equal(new Vector3(1f, 0.5f, 0.25f), warm!.ExternalEmittanceColor);
        Assert.Equal(warm.VariantKey, warmAgain!.VariantKey);
        Assert.NotEqual(warm.VariantKey, cool!.VariantKey);
    }
}
