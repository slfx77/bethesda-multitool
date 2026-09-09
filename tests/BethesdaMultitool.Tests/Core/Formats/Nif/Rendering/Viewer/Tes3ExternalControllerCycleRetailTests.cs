using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Tes3ExternalControllerCycleRetailTests
{
    private static readonly string[] ExpectedNames = ["Rock_1", "Rock_2", "Rock_3", "Rock_4", "Rock_5"];
    private static readonly int[] ModelDataBlocks = [250, 257, 264, 271, 278];
    private static readonly int[] KfDataBlocks = [88, 87, 86, 85, 84];
    private static readonly float[] SampleTimes = [8.5f, 40.56667f, 57.56667f];

    private static readonly string[] ExpectedDataHashes =
    [
        "F02D2F417288D7D88F70307AF34CAF271D04142ADC0130B84D73D3CB5F554BFB",
        "3283AE5A9B23FB87FDD561CE864BD69704A6027042F7B538F3FC094B727D772B",
        "B52EED4E00F067E62F28B5D0A847270FDE3AABBD660EF10E5445F60F33D2264C",
        "33FDD29E71A4416F530437490D6BF0DF89C5306C3AD54443A6480A48276383F4",
        "54B7BBEA5A01EE47931724616EF95FDB5A2217685E456090AFD4D09C55514F19"
    ];

    [Fact]
    public void InstalledExternalRockCycleBindsExactNamesAndMatchesThePairedNativeModel()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archive = RealAssetPaths.SteamGameFile("Morrowind", @"Data Files\Morrowind.bsa");
        Assert.SkipWhen(archive is null, RealAssetPaths.SkipMessage("Morrowind.bsa"));
        const string modelPath = @"meshes\r\atronach_storm.nif";
        const string kfPath = @"meshes\r\xatronach_storm.kf";
        using var service = NifBrowserService.CreateFromBsa(archive);
        var model = Assert.IsType<byte[]>(service.ReadNifData(modelPath));
        var kf = Assert.IsType<byte[]>(service.ReadNifData(kfPath));
        Assert.Equal(640391, model.Length);
        Assert.Equal(370911, kf.Length);
        Assert.Equal("C78C249A38EFCB13A386609FA2538E6A4CB3F3EE466B9ECE8D741FE0EF6A700B", Hash(model));
        Assert.Equal("A29A6FE551F1C4C77E59F3646E91134026B82AB2596D335F92F453302F75F012", Hash(kf));
        var modelInfo = Assert.IsType<NifInfo>(NifParser.Parse(model));
        var kfInfo = Assert.IsType<NifInfo>(NifParser.Parse(kf));
        Assert.Equal(125, kfInfo.Blocks.Count);
        for (var index = 0; index < ExpectedNames.Length; index++)
        {
            var modelBlock = modelInfo.Blocks[ModelDataBlocks[index]];
            var kfBlock = kfInfo.Blocks[KfDataBlocks[index]];
            Assert.Equal(1416, kfBlock.Size);
            Assert.Equal(ExpectedDataHashes[index], Hash(kf.AsSpan(kfBlock.DataOffset, kfBlock.Size)));
            Assert.Equal(ExpectedDataHashes[index], Hash(model.AsSpan(modelBlock.DataOffset, modelBlock.Size)));
        }

        var source = Assert.Single(NifTes3SequenceStreamReader.ReadAll(kf, kfInfo));
        Assert.Equal(ExpectedNames, source.Tracks.Select(static track => track.NodeName));
        Assert.All(source.Tracks, static track =>
        {
            Assert.Equal(NifKeyInterpolation.Quadratic, track.TranslationInterpolation);
            Assert.Equal(35, track.TranslationKeys.Length);
            Assert.All(track.TranslationKeys, static key => Assert.True(key.HasQuadraticTangents));
            Assert.Empty(track.RotationKeys);
            Assert.Empty(track.ScaleKeys);
            Assert.False(track.HasEulerRotation);
        });

        var build = service.BuildViewerSceneWithDiagnostics(model, "atronach_storm.nif", modelPath);
        var scene = Assert.IsType<BethesdaViewerScene>(build.Scene);
        Assert.NotEmpty(scene.MeshParts);
        var embedded = Assert.Single(scene.AnimationClips, static clip => clip.Name == "Embedded Controller Cycle");
        var binding = BethesdaViewerKfAnimationBinder.ParseAndBind(kf, scene, "xatronach_storm.kf");
        var external = Assert.Single(binding.AcceptedClips);
        Assert.Equal("External TES3 Controller Cycle", external.Name);
        Assert.Equal(5, Assert.Single(binding.Reports).BoundTrackCount);
        Assert.Equal(2, scene.AnimationClips.Count); // Binding alone remains transactional.
        scene.AnimationClips.Add(external);
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        var native = Assert.Single(decoded.AnimationClips,
            static clip => clip.Name == "External TES3 Controller Cycle");
        Assert.Equal(98.13334f, BethesdaViewerAnimationClockPolicy.Resolve(native).PresentationDurationSeconds, 4);
        var rest = scene.Nodes.Select(static node => node.LocalTransform).ToArray();
        var parents = scene.Nodes.Select(static node => node.ParentIndex).ToArray();
        var embeddedEvaluator = new BethesdaViewerAnimationPoseEvaluator(rest, parents, embedded);
        var externalEvaluator = new BethesdaViewerAnimationPoseEvaluator(rest, parents, native);
        var expected = new Matrix4x4[scene.Nodes.Count];
        var actual = new Matrix4x4[scene.Nodes.Count];
        foreach (var time in SampleTimes)
        {
            embeddedEvaluator.EvaluateNodeWorlds(time, expected);
            externalEvaluator.EvaluateNodeWorlds(time, actual);
            foreach (var track in native.NodeTracks)
            {
                Assert.InRange(
                    Vector3.Distance(expected[track.NodeIndex].Translation, actual[track.NodeIndex].Translation), 0f,
                    .001f);
            }
        }

        externalEvaluator.EvaluateNodeWorlds(40.56667f, expected);
        externalEvaluator.EvaluateNodeWorlds(57.56667f, actual);
        foreach (var track in native.NodeTracks)
        {
            Assert.InRange(Vector3.Distance(expected[track.NodeIndex].Translation, actual[track.NodeIndex].Translation),
                0f, .001f);
        }

        externalEvaluator.EvaluateNodeWorlds(8.5f, actual);
        Assert.Contains(native.NodeTracks, track =>
            Vector3.Distance(expected[track.NodeIndex].Translation, actual[track.NodeIndex].Translation) > 80f);
        var snapshotKey = native.NodeTracks[0].TranslationKeys[0];
        external.NodeTracks[0].TranslationKeys[0] = snapshotKey with { Backward = new Vector3(999f) };
        Assert.Equal(snapshotKey, native.NodeTracks[0].TranslationKeys[0]);
    }

    private static string Hash(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexString(SHA256.HashData(data));
    }
}