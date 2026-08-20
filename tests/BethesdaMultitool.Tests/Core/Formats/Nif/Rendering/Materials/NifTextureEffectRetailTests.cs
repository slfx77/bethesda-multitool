using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Retail boundary for the scene-graph NiTextureEffect environment route (2026-08-19
///     investigation). POSITIVE: Morrowind's glass armor authors real ENVIRONMENT_MAP +
///     CG_SPHERE_MAP effects (byte-verified block layout on <c>a_glass_boots_gnd.nif</c>), and the
///     decode chain must surface them as sphere-map classic env payloads. NEGATIVE: retail TES4
///     authors ZERO NiTextureEffect blocks — a full sweep of Oblivion - Meshes.bsa (8,032 NIFs)
///     plus SI/DLC (1,580 NIFs) found none; its window reflections are an engine-side RUNTIME
///     attachment (bDynamicWindowReflections), so a named retail window NIF must decode cleanly
///     with NO effect-derived env payload. That negative is the documented reason TES4 sphere
///     maps only light up for modded / runtime-captured content.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifTextureEffectRetailTests
{
    private const string MorrowindBsa =
        @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.bsa";

    private const string OblivionMeshesBsa =
        @"E:\SteamLibrary\SteamApps\common\Oblivion\Data\Oblivion - Meshes.bsa";

    private const string GlassBootsPath = @"meshes\a\a_glass_boots_gnd.nif";
    private const string LeyawiinWindowPath =
        @"meshes\architecture\castle\leyawiin\leyawiinwindow01.nif";

    public NifTextureEffectRetailTests()
    {
        BucketBTestGuard.SkipUnlessEnabled();
    }

    [Fact]
    public void MorrowindGlassBoots_SurfaceSphereMapEnvironmentPayload()
    {
        Assert.SkipUnless(File.Exists(MorrowindBsa), "Morrowind.bsa not present (dev-machine-only asset).");

        var (data, nif) = ExtractNif(MorrowindBsa, GlassBootsPath);
        Assert.Equal(4, nif.Blocks.Count(static block => block.TypeName == "NiTextureEffect"));

        var model = Assert.IsType<NifRenderableModel>(
            NifGeometryExtractor.Extract(data, nif, treatRootsAsIdentity: true));
        Assert.NotEmpty(model.Submeshes);

        var chrome = model.Submeshes
            .Where(static submesh => submesh.ClassicEnvironmentMapTexturePath is not null)
            .ToList();
        Assert.NotEmpty(chrome);
        Assert.All(chrome, static submesh =>
        {
            Assert.True(submesh.ClassicEnvironmentMapIsSphereMap);
            Assert.True(submesh.ClassicEnvironmentMapUsesWindowReflection);
            Assert.True(submesh.ClassicEnvironmentMapScale > 0f);
            // Retail source texture: "enviro glass.dds"-family sphere map (NiSourceTexture of the
            // TextureType-2 effect, block 17 in the byte-verified file).
            Assert.Contains(
                "enviro",
                submesh.ClassicEnvironmentMapTexturePath!,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void OblivionRetailWindowNif_DecodesWithNoAuthoredTextureEffect()
    {
        Assert.SkipUnless(
            File.Exists(OblivionMeshesBsa), "Oblivion meshes BSA not present (dev-machine-only asset).");

        var (data, nif) = ExtractNif(OblivionMeshesBsa, LeyawiinWindowPath);

        // The retail-authoring pin: TES4 window meshes carry NO NiTextureEffect blocks.
        Assert.DoesNotContain(nif.Blocks, static block => block.TypeName == "NiTextureEffect");

        var model = Assert.IsType<NifRenderableModel>(
            NifGeometryExtractor.Extract(data, nif, treatRootsAsIdentity: true));
        Assert.NotEmpty(model.Submeshes);
        Assert.All(model.Submeshes, static submesh =>
        {
            Assert.Null(submesh.ClassicEnvironmentMapTexturePath);
            Assert.False(submesh.ClassicEnvironmentMapIsSphereMap);
        });
    }

    private static (byte[] Data, NifInfo Nif) ExtractNif(string bsaPath, string meshPath)
    {
        using var extractor = new BsaExtractor(bsaPath);
        var archive = BsaParser.Parse(bsaPath);
        var file = archive.AllFiles.First(record =>
            string.Equals(record.FullPath, meshPath, StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        return (data, nif);
    }
}
