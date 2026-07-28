using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Retail-asset boundary for the complete FNV TallGrass material/geometry route. Synthetic
///     fixtures pin the property offsets, while this gate proves the shipped NIFs still resolve
///     their shared cutout texture from the archive that actually contains it.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvTallGrassRetailAssetTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";

    private const string Textures2BsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Textures2.bsa";

    private const string DiffusePath = @"textures\landscape\grass\NVGreenGrass.dds";

    private static readonly GrassFixture[] Fixtures =
    [
        new(@"meshes\landscape\grass\NVGreenGrass01.NIF", 40, 24, 100),
        new(@"meshes\landscape\grass\NVGreenGrass02.NIF", 18, 6, 90),
        new(@"meshes\landscape\grass\NVGreenGrass03.NIF", 54, 54, 128)
    ];

    [Fact]
    public void NvGreenGrass_RetailAssetsResolveCompleteTexturedDoubleSidedCutouts()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var meshesBsa = FindRetailArchive(MeshesBsaRelative, "meshes");
        var textures2Bsa = FindRetailArchive(Textures2BsaRelative, "Textures2");

        using var meshes = ArchiveReader.Open(meshesBsa);
        // NVGreenGrass.dds ships in Fallout - Textures2.bsa, not Fallout - Textures.bsa. Using
        // only that source makes a successful lookup direct evidence for the production path that
        // previously rendered these cards untextured.
        using var textures = new NifTextureResolver(textures2Bsa);

        foreach (var fixture in Fixtures)
        {
            var data = meshes.ReadFile(fixture.ModelPath);
            Assert.NotNull(data);
            var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
            var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
                data,
                nif,
                textures,
                skipSkinning: true,
                treatRootsAsIdentity: true,
                collectBillboards: true,
                dropBoneAttachedShapes: true));

            var submesh = Assert.Single(model.Submeshes);
            Assert.Equal(fixture.VertexCount, submesh.VertexCount);
            Assert.Equal(fixture.TriangleCount, submesh.TriangleCount);
            Assert.Equal("TallGrassShaderProperty", submesh.ShaderMetadata?.PropertyType);
            Assert.Equal(DiffusePath, submesh.DiffuseTexturePath, StringComparer.OrdinalIgnoreCase);
            Assert.True(submesh.IsDoubleSided);

            var diffuse = textures.GetTexture(submesh.DiffuseTexturePath!);
            Assert.NotNull(diffuse);
            Assert.Equal(512, diffuse.Width);
            Assert.Equal(512, diffuse.Height);
            Assert.Equal(10, diffuse.MipCount);
            Assert.True(diffuse.HasSignificantAlpha());

            var alpha = NifAlphaClassifier.Classify(submesh, diffuse);
            Assert.Equal(NifAlphaRenderMode.Cutout, alpha.RenderMode);
            Assert.False(alpha.HasAlphaBlend);
            Assert.True(alpha.HasAlphaTest);
            Assert.Equal(fixture.AlphaTestThreshold, alpha.AlphaTestThreshold);
            Assert.Equal((byte)4, alpha.AlphaTestFunction);
        }
    }

    private static string FindRetailArchive(string relativePath, string label)
    {
        var path = SampleFileFixture.FindSamplePath(relativePath);
        Assert.SkipWhen(path is null, $"FNV PC-final {label} BSA not available");
        return path!;
    }

    private sealed record GrassFixture(
        string ModelPath,
        int VertexCount,
        int TriangleCount,
        byte AlphaTestThreshold);
}