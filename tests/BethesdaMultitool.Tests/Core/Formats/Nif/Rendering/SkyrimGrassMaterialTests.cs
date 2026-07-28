using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class SkyrimGrassMaterialTests
{
    private const string ArchivePath = @"E:\SteamLibrary\SteamApps\common\Skyrim\Data\Skyrim - Meshes.bsa";

    [Theory]
    [InlineData(false, 0u, false)]
    [InlineData(false, 0x10u, true)]
    [InlineData(true, 0u, true)]
    public void DoubleSidedPolicy_ComposesStencilAndLightingShaderFlag(
        bool stencilDrawBoth,
        uint flags2,
        bool expected)
    {
        var metadata = new NifShaderTextureMetadata
        {
            PropertyType = "BSLightingShaderProperty",
            ShaderFlags2 = flags2
        };

        Assert.Equal(expected, NifDoubleSidedPolicy.Resolve(stencilDrawBoth, metadata));
    }

    [Theory]
    [InlineData(@"meshes\landscape\grass\tundragrassobj04.nif")]
    [InlineData(@"meshes\landscape\grass\fieldgrass01.nif")]
    public void RetailGrassCards_UseShaderDoubleSidedWithoutStencil(string assetPath)
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var archivePath = Environment.GetEnvironmentVariable("SKYRIM_MESHES_BSA") ?? ArchivePath;
        Assert.SkipWhen(!File.Exists(archivePath),
            "Skyrim LE Meshes BSA not installed (set SKYRIM_MESHES_BSA to run this probe)");

        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(assetPath));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(data, nif));

        Assert.NotEmpty(model.Submeshes);
        Assert.All(model.Submeshes, static sub =>
        {
            Assert.Equal("BSLightingShaderProperty", sub.ShaderMetadata?.PropertyType);
            Assert.True((sub.ShaderMetadata?.ShaderFlags2 & 0x10u) != 0);
            Assert.True(sub.IsDoubleSided);
        });
    }

    [Fact]
    public void RetailPineShrub02_PreservesDetailedDoubleSidedGeometry()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var archivePath = Environment.GetEnvironmentVariable("SKYRIM_MESHES_BSA") ?? ArchivePath;
        Assert.SkipWhen(!File.Exists(archivePath),
            "Skyrim LE Meshes BSA not installed (set SKYRIM_MESHES_BSA to run this probe)");

        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(
            @"meshes\landscape\plants\pineshrub02.nif"));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(data, nif));

        var shrub = Assert.Single(model.Submeshes);
        Assert.Equal(1871, model.Submeshes.Sum(static submesh => submesh.VertexCount));
        Assert.Equal(1419, model.Submeshes.Sum(static submesh => submesh.TriangleCount));
        Assert.All(model.Submeshes, static submesh => Assert.True(submesh.IsDoubleSided));
        Assert.True((shrub.ShaderMetadata?.ShaderFlags2 & 0x10u) != 0);
        Assert.Equal((byte)128, shrub.AlphaTestThreshold);
        Assert.EndsWith(@"plants\Shrub01half.dds", shrub.DiffuseTexturePath,
            StringComparison.OrdinalIgnoreCase);
    }
}