using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Pins the FO4/FO76 MSWP material-swap plumbing: swaps must fold into
///     <see cref="AlternateTextureSet.VariantKey" /> (so a swapped placement gets its own mesh-cache
///     variant, and can never collide with a shape-override-only re-skin), and the MSWP record's path
///     normalization must land on EXACTLY the form <c>NifTexturePathUtility.Normalize</c> produces for
///     a NIF's baked material path — the decode-time lookup is a single dictionary hit, so any
///     mismatch silently disables every swap.
/// </summary>
public sealed class MaterialSwapTests
{
    private static KeyValuePair<string, ShapeTextureOverride> Entry(string shape, string? diffuse)
    {
        return new KeyValuePair<string, ShapeTextureOverride>(shape, new ShapeTextureOverride(diffuse, null));
    }

    private static Dictionary<string, string> Swaps(params (string From, string To)[] pairs)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in pairs)
        {
            dict[from] = to;
        }

        return dict;
    }

    [Fact]
    public void Create_SwapsOnly_ReturnsSetWithSwaps()
    {
        var set = AlternateTextureSet.Create(
            [],
            Swaps(("materials\\architecture\\a.bgsm", "materials\\architecture\\b.bgsm")));

        Assert.NotNull(set);
        Assert.Empty(set!.Overrides);
        Assert.NotNull(set.MaterialSwaps);
        Assert.Equal("materials\\architecture\\b.bgsm", set.MaterialSwaps!["materials\\architecture\\a.bgsm"]);
    }

    [Fact]
    public void Create_SwapsOnly_KeyDiffersFromOverridesOnly()
    {
        // A swap-only set must never share a cache variant with a shape-override-only set — the
        // "mswp" hash salt is what guarantees that even for adversarially similar path strings.
        var swapsOnly = AlternateTextureSet.Create(
            [],
            Swaps(("materials\\architecture\\a.bgsm", "materials\\architecture\\b.bgsm")));
        var overridesOnly = AlternateTextureSet.Create(
            [Entry("materials\\architecture\\a.bgsm", "materials\\architecture\\b.bgsm")]);

        Assert.NotEqual(overridesOnly!.VariantKey, swapsOnly!.VariantKey);
    }

    [Fact]
    public void Create_SameSwaps_ShareVariantKey()
    {
        var a = AlternateTextureSet.Create(
            [],
            Swaps(("materials\\x\\a.bgsm", "materials\\x\\b.bgsm"), ("materials\\x\\c.bgsm", "materials\\x\\d.bgsm")));
        var b = AlternateTextureSet.Create(
            [],
            Swaps(("materials\\x\\c.bgsm", "materials\\x\\d.bgsm"), ("materials\\x\\a.bgsm", "materials\\x\\b.bgsm")));

        Assert.Equal(a!.VariantKey, b!.VariantKey);
    }

    [Fact]
    public void Create_DifferentSwaps_ProduceDifferentVariantKeys()
    {
        var a = AlternateTextureSet.Create([], Swaps(("materials\\x\\a.bgsm", "materials\\x\\b.bgsm")));
        var b = AlternateTextureSet.Create([], Swaps(("materials\\x\\a.bgsm", "materials\\x\\c.bgsm")));

        Assert.NotEqual(a!.VariantKey, b!.VariantKey);
    }

    [Fact]
    public void Create_SwapsMergedWithOverrides_KeyDiffersFromEitherAlone()
    {
        var overrides = new[] { Entry("BB04:13", "textures\\ads\\ultraluxe.dds") };
        var swaps = Swaps(("materials\\x\\a.bgsm", "materials\\x\\b.bgsm"));

        var overridesOnly = AlternateTextureSet.Create(overrides);
        var swapsOnly = AlternateTextureSet.Create([], swaps);
        var merged = AlternateTextureSet.Create(overrides, swaps);

        Assert.NotEqual(overridesOnly!.VariantKey, merged!.VariantKey);
        Assert.NotEqual(swapsOnly!.VariantKey, merged.VariantKey);
    }

    [Fact]
    public void Create_EmptySwapsAndNoOverrides_ReturnsNull()
    {
        Assert.Null(AlternateTextureSet.Create([], new Dictionary<string, string>()));
    }

    [Fact]
    public void NormalizeMaterialPath_MatchesTheNifDecodeSideForm()
    {
        // The three real-world spellings of one material: the MSWP BNAM's prefix-less mixed case, an
        // already-prefixed forward-slash form, and the absolute developer build path FO4 NIFs bake
        // into BSLightingShaderProperty Name. All must normalize to one key or lookups silently miss.
        var fromMswpEntry = MaterialSwapRecord.NormalizeMaterialPath("Architecture\\Buildings\\X.BGSM");
        var fromPrefixedEntry = MaterialSwapRecord.NormalizeMaterialPath("materials/architecture/buildings/x.bgsm");
        var fromNifShader = NifTexturePathUtility.Normalize(
            "C:\\Projects\\Fallout4\\Build\\PC\\Data\\materials\\Architecture\\Buildings\\X.BGSM");

        Assert.Equal("materials\\architecture\\buildings\\x.bgsm", fromNifShader);
        Assert.Equal(fromNifShader, fromMswpEntry);
        Assert.Equal(fromNifShader, fromPrefixedEntry);
    }
}