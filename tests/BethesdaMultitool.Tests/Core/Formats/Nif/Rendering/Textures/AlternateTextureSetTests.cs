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
}
