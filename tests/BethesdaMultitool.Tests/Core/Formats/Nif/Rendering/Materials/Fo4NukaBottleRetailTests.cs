using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Installed-retail boundary for the Nuka bottle refraction discriminator. The synthetic policy
///     tests own the branch matrix; this pins the two real BSTriShapes and their external materials.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Fo4NukaBottleRetailTests
{
    private const string DefaultDataDirectory =
        @"E:\SteamLibrary\SteamApps\common\Fallout 4\Data";

    [Fact]
    [Trait("Category", BucketBTestGuard.Category)]
    public void FullBottle_RetainsGlassAndRefractionFlaggedLightingShape()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDirectory = Environment.GetEnvironmentVariable("FALLOUT4_DATA_DIR") ?? DefaultDataDirectory;
        var meshesPath = Path.Combine(dataDirectory, "Fallout4 - Meshes.ba2");
        var materialsPath = Path.Combine(dataDirectory, "Fallout4 - Materials.ba2");
        Assert.SkipUnless(
            File.Exists(meshesPath) && File.Exists(materialsPath),
            "Fallout 4 meshes/materials BA2s not installed (set FALLOUT4_DATA_DIR to run this probe).");

        using var meshes = ArchiveReader.Open(meshesPath);
        using var resolver = new NifTextureResolver(materialsPath);
        var data = Assert.IsType<byte[]>(meshes.ReadFile(@"meshes\props\NukaColaBottleFull.nif"));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data,
            nif,
            resolver,
            skipSkinning: true,
            treatRootsAsIdentity: true));

        // Historical discriminator: the generic refraction-bit skip returned only the glass shape.
        Assert.Equal(2, model.Submeshes.Count);

        var glass = Assert.Single(model.Submeshes, static submesh =>
            string.Equals(submesh.ShapeName, "NukaCola_Glass:3", StringComparison.Ordinal));
        Assert.Equal("BSEffectShaderProperty", glass.ShaderMetadata?.PropertyType);
        Assert.Equal(
            @"materials\Props\NukaColaGlass.BGEM",
            glass.ShaderMetadata?.MaterialPath,
            ignoreCase: true);

        var bottle = Assert.Single(model.Submeshes, static submesh =>
            string.Equals(submesh.ShapeName, "NukaCola:0", StringComparison.Ordinal));
        Assert.Equal("BSLightingShaderProperty", bottle.ShaderMetadata?.PropertyType);
        Assert.Equal(
            @"materials\Props\NukaColaFull.BGSM",
            bottle.ShaderMetadata?.MaterialPath,
            ignoreCase: true);
        Assert.True((bottle.ShaderMetadata!.ShaderFlags!.Value & (1u << 16)) != 0);
        Assert.Equal(@"props/nukacolafull_d.dds", bottle.DiffuseTexturePath, ignoreCase: true);

        // This row still has a real residual: the glass material intentionally has no diffuse and
        // instead authors a cubemap/effect route that this bounded fix does not implement.
        var glassMaterial = Assert.IsType<BgsmMaterial>(
            resolver.TryGetMaterial(@"materials\Props\NukaColaGlass.BGEM"));
        Assert.Null(glassMaterial.Diffuse);
        Assert.Equal(@"Shared/Cubemaps/Bronze_e.dds", glassMaterial.EnvironmentMap, ignoreCase: true);
    }
}
