using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Pins how <see cref="NifTexturePathUtility.Normalize" /> roots a path that arrives with no
///     <c>textures\</c> or <c>materials\</c> prefix.
///     <para>
///         This was a whole-worldspace defect: Fallout 76's landscape LTEX <c>BNAM</c> ships a
///         ROOTLESS <c>.bgsm</c> (the record handler stores it verbatim), and rooting it at
///         <c>textures\</c> sent every Appalachia terrain material to a path in no archive. The
///         worldspace rendered on the untextured fallback with nothing logged.
///     </para>
///     <para>
///         It reached FO76 through the Starfield feature commit (589a831d), which added the shared
///         LTEX→material branch. Starfield survives the same mis-rooting because a <c>.mat</c> is
///         resolved by NAME from the compiled material database, where the root is irrelevant; a
///         <c>.bgsm</c> is a FILE fetched from an archive, where it is fatal. That asymmetry is why
///         <c>.mat</c> is deliberately excluded below.
///     </para>
/// </summary>
public sealed class MaterialPathRootingTests
{
    [Theory]
    [InlineData(@"landscape\ground\dirt01.bgsm")]
    [InlineData(@"Landscape\Ground\Dirt01.BGSM")]
    [InlineData(@"effects\water\foam.bgem")]
    public void A_rootless_material_is_rooted_at_materials(string authored)
    {
        // The FO76 LTEX BNAM shape. Rooting these at textures\ is what broke Appalachia.
        var normalized = NifTexturePathUtility.Normalize(authored);

        Assert.StartsWith(@"materials\", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain(@"textures\", normalized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"landscape\ground\dirt01.dds")]
    [InlineData(@"sky\clouds.dds")]
    public void A_rootless_texture_is_still_rooted_at_textures(string authored)
    {
        // The overwhelmingly common case must be untouched by the material carve-out.
        var normalized = NifTexturePathUtility.Normalize(authored);

        Assert.StartsWith(@"textures\", normalized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"materials\landscape\dirt.bgsm", @"materials\landscape\dirt.bgsm")]
    [InlineData(@"textures\landscape\dirt.dds", @"textures\landscape\dirt.dds")]
    public void An_already_rooted_path_is_left_alone(string authored, string expected)
    {
        Assert.Equal(expected, NifTexturePathUtility.Normalize(authored));
    }

    [Fact]
    public void An_absolute_developer_build_path_still_peels_to_data_relative()
    {
        // FO4/FO76 meshes bake absolute build paths into the shader Name. These already end up
        // materials\-rooted after peeling, which is why the MESH path worked while terrain did not —
        // the regression was invisible from the mesh side.
        var normalized = NifTexturePathUtility.Normalize(
            @"C:\Projects\Fallout4\Build\PC\Data\Materials\Architecture\X.BGSM");

        Assert.Equal(@"materials\architecture\x.bgsm", normalized);
    }

    [Theory]
    [InlineData(@"c:\projects\76\build\pc\materials\landscape\ground\riverbedrocks.bgsm",
        @"materials\landscape\ground\riverbedrocks.bgsm")]
    [InlineData(@"C:\Projects\76\Build\PC\Textures\Landscape\Rock01.dds",
        @"textures\landscape\rock01.dds")]
    [InlineData(@"d:\work\76\build\pc\meshes\landscape\tree.nif",
        @"meshes\landscape\tree.nif")]
    public void A_fallout76_build_path_with_no_data_step_still_peels(string authored, string expected)
    {
        // FO76 bakes absolute build paths that omit the "Data" segment entirely, unlike FO4's
        // "...\Build\PC\Data\Materials\...". The \data\ peel cannot see those, so they used to arrive
        // at the archive lookup with the whole build root still attached and silently resolved to
        // nothing. Found by the resolve-failure logging within one run of enabling it.
        Assert.Equal(expected, NifTexturePathUtility.Normalize(authored));
    }

    [Fact]
    public void A_path_already_rooted_is_not_re_cut_at_a_later_root_segment()
    {
        // Guard against the peel being too eager: a legitimately-rooted path that happens to contain
        // another root word deeper in must be left exactly as it is.
        Assert.Equal(@"textures\landscape\textures\overlay.dds",
            NifTexturePathUtility.Normalize(@"textures\landscape\textures\overlay.dds"));
    }

    [Fact]
    public void A_starfield_material_is_deliberately_not_re_rooted()
    {
        // Starfield's .mat resolves by name from the compiled material database, so its root is
        // irrelevant — and it renders correctly today. Re-rooting it would change a working path for
        // no benefit, so this pins the exclusion rather than leaving it to chance.
        var normalized = NifTexturePathUtility.Normalize(@"landscape\rock01.mat");

        Assert.StartsWith(@"textures\", normalized, StringComparison.Ordinal);
    }
}
