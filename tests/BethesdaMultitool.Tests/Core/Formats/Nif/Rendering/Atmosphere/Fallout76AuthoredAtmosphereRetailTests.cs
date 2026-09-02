using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Installed-retail guard for the authored FO76 atmosphere promoted to the default sky path.
///     The policy test proves selection; this fixture proves the shipped asset actually decodes into
///     the typed, vertex-weighted geometry consumed by <c>SkyGeometryRenderer12</c>.
/// </summary>
[Trait("Category", BucketBTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Fallout76AuthoredAtmosphereRetailTests
{
    [Fact]
    public void RetailAtmosphereNif_RecoversTypedVertexWeightedSkyGeometry()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDirectory = Environment.GetEnvironmentVariable("FALLOUT76_DATA_DIR") ??
                            RealAssetPaths.SteamGameDirectory("Fallout76", "Data");
        Assert.SkipUnless(dataDirectory is not null,
            RealAssetPaths.SkipMessage("Fallout 76 Data folder"));

        var archivePath = Path.Combine(dataDirectory!, "SeventySix - Meshes.ba2");
        Assert.SkipUnless(File.Exists(archivePath),
            RealAssetPaths.SkipMessage("SeventySix - Meshes.ba2"));

        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(@"meshes\sky\Atmosphere.nif"));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data,
            nif,
            skipSkinning: true,
            treatRootsAsIdentity: true));

        var skyLayers = model.Submeshes
            .Where(static submesh => submesh.SkyType == SkyObjectType.Sky)
            .ToArray();
        Assert.NotEmpty(skyLayers);
        Assert.All(skyLayers, static layer =>
        {
            Assert.NotEmpty(layer.Positions);
            Assert.NotEmpty(layer.Triangles);
            var colors = Assert.IsType<byte[]>(layer.VertexColors);
            Assert.Equal((layer.Positions.Length / 3) * 4, colors.Length);
        });
    }
}
