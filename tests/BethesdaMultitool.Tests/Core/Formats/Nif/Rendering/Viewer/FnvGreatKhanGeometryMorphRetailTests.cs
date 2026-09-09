using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class FnvGreatKhanGeometryMorphRetailTests
{
    private static readonly int[] ExpectedTargetKeyCounts = [3, 27, 27, 33, 21, 21, 24, 27, 30, 33, 36, 38, 42, 46, 43];

    [Fact]
    public void InstalledGreatKhanFlag_BindsAllAuthoredCurvesAndDeformsTheNativeCloth()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile("Fallout New Vegas", @"Data\Fallout - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("FNV Fallout - Meshes.bsa"));
        const string modelPath = @"meshes\clutter\nvgreatkhanflag\nvgreatkhanflag.nif";
        using var service = NifBrowserService.CreateFromBsa(archivePath);
        var data = Assert.IsType<byte[]>(service.ReadNifData(modelPath));
        Assert.Equal("1DD0D3FEBEBC40BFB6E7B0E3FB3F231F64F64C181AA18D17F88FB181786D509A",
            Convert.ToHexString(SHA256.HashData(data)));
        var build = service.BuildViewerSceneWithDiagnostics(data, "nvgreatkhanflag.nif", modelPath);
        var scene = Assert.IsType<BethesdaViewerScene>(build.Scene);
        var clip = Assert.Single(scene.AnimationClips);
        Assert.Empty(clip.NodeTracks);
        var source = Assert.Single(clip.GeometryMorphTracks!);
        Assert.Equal(21, source.Morph.SourceBlockIndex);
        Assert.Equal(15, source.Morph.Targets.Length);
        Assert.Equal(162, source.Morph.VertexCount);
        Assert.Null(scene.MeshParts[source.MeshPartIndex].Skin);
        Assert.Equal(ExpectedTargetKeyCounts,
            source.Morph.Targets.Select(static target => target.Curve.Keys.Length));

        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        var track = Assert.Single(Assert.Single(decoded.AnimationClips).GeometryMorphTracks!);
        var original = decoded.MeshParts[track.MeshPartIndex].Submesh.Vertices;
        var vertices = original.ToArray();
        var weights = new float[15];
        BethesdaViewerGeometryMorphPolicy.Pose(track.Morph, 0f, vertices, weights);
        var atZero = vertices.ToArray();
        BethesdaViewerGeometryMorphPolicy.Pose(track.Morph, 2.1833333373069763f, vertices, weights);
        Assert.Equal(1f, weights[0]);
        Assert.Equal(0.5370299208443612f, weights[11], 5);
        var maximumMotion = vertices.Zip(atZero, static (a, b) => Vector3.Distance(a.Position, b.Position)).Max();
        Assert.InRange(maximumMotion, 74.05f, 74.08f);
        for (var vertex = 0; vertex < vertices.Length; vertex++)
        {
            Assert.Equal(original[vertex].Normal, vertices[vertex].Normal);
            Assert.Equal(original[vertex].Tangent, vertices[vertex].Tangent);
            Assert.Equal(original[vertex].Bitangent, vertices[vertex].Bitangent);
            Assert.Equal(original[vertex].TexCoord, vertices[vertex].TexCoord);
            Assert.Equal(original[vertex].VertexColorRgba, vertices[vertex].VertexColorRgba);
        }

        BethesdaViewerGeometryMorphPolicy.Pose(track.Morph, 0f, vertices, weights);
        Assert.Equal(atZero, vertices);
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        Assert.Empty(posed.UnsupportedMeshParts);
        Assert.NotNull(posed.Bounds);
    }
}