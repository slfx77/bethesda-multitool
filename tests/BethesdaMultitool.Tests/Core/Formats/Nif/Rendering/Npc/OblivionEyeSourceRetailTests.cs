using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class OblivionEyeSourceRetailTests
{
    [Theory]
    [InlineData(@"meshes\characters\imperial\eyelefthuman.nif")]
    [InlineData(@"meshes\characters\imperial\eyerighthuman.nif")]
    public void InstalledEyesRequireExactSourceAndActorPreparation(string path)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));
        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(path));
        Assert.True(BethesdaViewerOblivionEyePolicy.IsReviewedSource(BethesdaGame.Oblivion, data));
        Assert.False(BethesdaViewerOblivionEyePolicy.IsReviewedSource(BethesdaGame.Fallout3, data));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(data, nif, bindPoseOnly: true));
        var eye = Assert.Single(model.Submeshes);
        Assert.False(eye.HasReviewedOblivionEyeSource); // Raw inspection never acquires actor semantics.
        Assert.Null(eye.OblivionEyeBounds);
        Assert.True(eye.LocalBounds is { Radius: > 0f }); // Prepared eyes retain this serialized sphere.
        Assert.Equal(186, eye.VertexCount);
        Assert.False(eye.HasAlphaBlend);
        Assert.False(eye.HasAlphaTest);
        Assert.Null(eye.VertexColors);
        data[^1] ^= 1;
        Assert.False(BethesdaViewerOblivionEyePolicy.IsReviewedSource(BethesdaGame.Oblivion, data));
    }
}