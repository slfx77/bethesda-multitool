using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Vegetation;

[Trait("Category", TestCategories.BucketB)]
public sealed class NifTallGrassVertexAlphaTests
{
    [Fact]
    public void TallGrassPolicy_PreservesWindWeightButSuppliesOpaqueCoverageAlpha()
    {
        var metadata = new NifShaderTextureMetadata
        {
            PropertyType = "TallGrassShaderProperty"
        };
        Assert.False(NifVertexColorPolicy.UsesAlphaForOpacity(metadata));

        byte[] authoredColors = [32, 64, 96, 43];
        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            VertexColors = authoredColors,
            UseVertexColors = true,
            UseVertexAlphaForOpacity = false
        };

        var effective = NifVertexColorPolicy.Read(submesh, 0);
        var gpuVertex = Assert.Single(GpuMeshUploader.BuildVertices(submesh));
        var referenceWindVertex = Assert.Single(GpuMeshUploader.BuildVertices(
            submesh,
            true));

        Assert.Equal((byte)43, authoredColors[3]);
        Assert.Equal(((byte)32, (byte)64, (byte)96, byte.MaxValue), effective);
        Assert.Equal(1f, gpuVertex.VertexColor.W);
        Assert.Equal(43f / 255f, referenceWindVertex.VertexColor.W);
    }

    [Fact]
    public void SkyrimLightingPolicy_ContinuesToHonorVertexAlphaFlag()
    {
        Assert.False(NifVertexColorPolicy.UsesAlphaForOpacity(new NifShaderTextureMetadata
        {
            PropertyType = "BSLightingShaderProperty",
            ShaderFlags = 0u
        }));
        Assert.True(NifVertexColorPolicy.UsesAlphaForOpacity(new NifShaderTextureMetadata
        {
            PropertyType = "BSLightingShaderProperty",
            ShaderFlags = 0x8u
        }));
        Assert.True(NifVertexColorPolicy.UsesAlphaForOpacity(new NifShaderTextureMetadata
        {
            PropertyType = "BSShaderPPLightingProperty",
            ShaderFlags = 0u
        }));
    }

    [Fact]
    public void RetailFNVGreenGrass_ExtractionUsesTextureOnlyForCutoutCoverage()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var assetRoot = Path.Combine(
            Path.GetTempPath(),
            "fnv-grass-assets-retail-20260716",
            "meshes",
            "landscape",
            "grass");
        var paths = new[]
        {
            Path.Combine(assetRoot, "nvgreengrass01.nif"),
            Path.Combine(assetRoot, "nvgreengrass02.nif"),
            Path.Combine(assetRoot, "nvgreengrass03.nif")
        };
        Assert.SkipUnless(paths.All(File.Exists),
            "Extracted FNV NVGreenGrass retail NIFs are not present (dev-machine-only asset gate).");

        foreach (var path in paths)
        {
            var data = File.ReadAllBytes(path);
            var nif = NifParser.Parse(data);
            Assert.NotNull(nif);
            var model = NifGeometryExtractor.Extract(
                data,
                nif,
                skipSkinning: true,
                treatRootsAsIdentity: true,
                collectBillboards: true,
                dropBoneAttachedShapes: true);
            Assert.NotNull(model);

            var submesh = Assert.Single(model.Submeshes);
            Assert.Equal("TallGrassShaderProperty", submesh.ShaderMetadata?.PropertyType);
            Assert.False(submesh.UseVertexAlphaForOpacity);
            Assert.NotNull(submesh.VertexColors);
            Assert.Contains(submesh.VertexColors!.Where((_, index) => (index & 3) == 3), alpha => alpha < 255);
            Assert.All(
                Enumerable.Range(0, submesh.VertexCount),
                vertexIndex => Assert.Equal(byte.MaxValue, NifVertexColorPolicy.Read(submesh, vertexIndex).A));
            Assert.All(
                GpuMeshUploader.BuildVertices(submesh),
                vertex => Assert.Equal(1f, vertex.VertexColor.W));
            var referenceVertices = GpuMeshUploader.BuildVertices(
                submesh,
                true);
            Assert.Contains(referenceVertices, vertex => vertex.VertexColor.W < 1f);
            Assert.Equal(
                submesh.VertexColors!.Where((_, index) => (index & 3) == 3).Select(alpha => alpha / 255f),
                referenceVertices.Select(vertex => vertex.VertexColor.W));
        }
    }
}