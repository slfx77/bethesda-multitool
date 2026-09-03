using System.Numerics;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Retail pin for the workstream-1 failure: bearbody.nif stores every skin bone as a flattened
///     child of its file root, while the sibling skeleton.nif carries the actual 33-node hierarchy.
///     Applying idle.kf to the flattened stubs explodes the body; the canonical rig must be installed
///     before name binding and keep the resulting CPU pose finite and creature-sized.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class BethesdaViewerExternalSkeletonRigRetailTests
{
    private const string BodyPath = @"meshes\creatures\bear\bearbody.nif";
    private const string HeadPath = @"meshes\creatures\bear\bearhead.nif";
    private const string SkeletonPath = @"meshes\creatures\bear\skeleton.nif";
    private const string IdlePath = @"meshes\creatures\bear\idle.kf";
    private const string MinotaurBodyPath = @"meshes\creatures\minotaur\minotaur.nif";
    private const string MinotaurHeadPath = @"meshes\creatures\minotaur\head.nif";
    private const string MinotaurSkeletonPath = @"meshes\creatures\minotaur\skeleton.nif";
    private const string MinotaurHandToHandIdlePath = @"meshes\creatures\minotaur\handtohandidle.kf";

    [Fact]
    public void OblivionBear_BrowserPublishesRiggedSceneAndPlayableCatalogEntry()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));

        using var service = NifBrowserService.CreateFromBsa(archivePath!);
        var bodyData = Assert.IsType<byte[]>(service.ReadNifData(BodyPath));
        var build = service.BuildViewerSceneWithDiagnostics(bodyData, "bearbody.nif", BodyPath);
        var scene = Assert.IsType<BethesdaViewerScene>(build.Scene);
        var catalog = Assert.IsType<NifModelFamilyAnimationCatalog>(scene.ModelFamilyAnimations);
        Assert.Equal(NifModelFamilyAnimationResolutionStatus.Resolved, catalog.Status);
        Assert.Equal(SkeletonPath, catalog.Skeleton?.VirtualPath, ignoreCase: true);
        var idle = Assert.Single(catalog.Animations, static asset =>
            string.Equals(asset.VirtualPath, IdlePath, StringComparison.OrdinalIgnoreCase));
        Assert.True(scene.TryGetNodeIndex("Bip01 Neck", out var neckIndex));
        Assert.True(scene.TryGetNodeIndex("Bip01 Head", out var headIndex));
        Assert.Equal(neckIndex, scene.Nodes[headIndex].ParentIndex);

        var bodySkin = Assert.IsType<BethesdaViewerSkinBinding>(
            Assert.Single(scene.MeshParts, static part => part.Skin is not null).Skin);
        Assert.Equal(29, bodySkin.JointNodeIndices.Length);
        var rigidHeadPrefix = Path.GetFileName(HeadPath) + "::";
        var rigidHeadParts = scene.MeshParts
            .Where(part => part.Name.StartsWith(rigidHeadPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(rigidHeadParts);
        Assert.All(rigidHeadParts, part =>
        {
            Assert.Null(part.Skin);
            Assert.True(part.NodeIndex.HasValue);
            Assert.True(
                DescendsFrom(scene, part.NodeIndex.Value, headIndex),
                $"Rigid head part '{part.Name}' does not descend from canonical Bip01 Head.");
        });

        var binding = BethesdaViewerKfAnimationBinder.ParseAndBind(
            service.ReadModelFamilyAnimationData(idle),
            scene,
            idle.RelativePath);
        Assert.True(binding.HasAcceptedClips, binding.Summary);
        Assert.Equal(32, Assert.Single(binding.Reports).BoundTrackCount);
    }

    [Fact]
    public void OblivionBear_UsesCanonicalHierarchyBeforeIdleKfBinding()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));

        using var extractor = new BsaExtractor(archivePath!);
        var archive = BsaParser.Parse(archivePath!);
        var bodyData = Extract(extractor, archive, BodyPath);
        var skeletonData = Extract(extractor, archive, SkeletonPath);
        var idleData = Extract(extractor, archive, IdlePath);

        var bodyNif = Assert.IsType<NifInfo>(NifParser.Parse(bodyData));
        var source = Assert.IsType<GlbScene>(NifExportSceneBuilder.Build(bodyData, bodyNif, BodyPath));
        var sourceSkin = Assert.IsType<GlbSkinBinding>(Assert.Single(source.MeshParts).Skin);
        Assert.Equal(29, sourceSkin.JointNodeIndices.Length);
        var flattenedParentCount = sourceSkin.JointNodeIndices
            .Select(index => source.Nodes[index].ParentIndex)
            .Distinct()
            .Count();
        Assert.Equal(1, flattenedParentCount);

        var applied = BethesdaViewerExternalSkeletonRigAdapter.TryApply(
            source,
            skeletonData,
            out var rebound,
            out var diagnostic);

        Assert.True(applied, diagnostic);
        var viewerScene = BethesdaViewerSceneGlbAdapter.FromGlbScene(
            rebound,
            BodyPath,
            BethesdaViewerScenePurpose.RawNif);
        var binding = BethesdaViewerKfAnimationBinder.ParseAndBind(idleData, viewerScene, "idle.kf");
        var clip = Assert.Single(binding.AcceptedClips);
        Assert.Equal(32, binding.Reports.Single().SourceTrackCount);
        Assert.Equal(32, binding.Reports.Single().BoundTrackCount);
        Assert.True(viewerScene.TryGetNodeIndex("Bip01 Neck", out var neckIndex));
        Assert.True(viewerScene.TryGetNodeIndex("Bip01 Head", out var headIndex));
        Assert.Equal(neckIndex, viewerScene.Nodes[headIndex].ParentIndex);

        var evaluator = new BethesdaViewerAnimationPoseEvaluator(
            viewerScene.Nodes.Select(static node => node.LocalTransform).ToArray(),
            viewerScene.Nodes.Select(static node => node.ParentIndex).ToArray(),
            clip);
        var nodeWorlds = new Matrix4x4[viewerScene.Nodes.Count];
        evaluator.EvaluateNodeWorlds(3f, nodeWorlds);
        var posed = SkinPositions(viewerScene, nodeWorlds);
        Assert.All(posed, static value => Assert.True(float.IsFinite(value)));
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        for (var index = 0; index < posed.Length; index += 3)
        {
            var point = new Vector3(posed[index], posed[index + 1], posed[index + 2]);
            minimum = Vector3.Min(minimum, point);
            maximum = Vector3.Max(maximum, point);
        }

        var extent = maximum - minimum;
        Assert.InRange(extent.Length(), 10f, 500f);
    }

    [Fact]
    public void OblivionMinotaur_AttachesExactRigidHeadAndFullyBindsHandToHandIdle()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));

        using var service = NifBrowserService.CreateFromBsa(archivePath!);
        var bodyData = Assert.IsType<byte[]>(service.ReadNifData(MinotaurBodyPath));
        var build = service.BuildViewerSceneWithDiagnostics(
            bodyData,
            "minotaur.nif",
            MinotaurBodyPath);
        var scene = Assert.IsType<BethesdaViewerScene>(build.Scene);
        var catalog = Assert.IsType<NifModelFamilyAnimationCatalog>(scene.ModelFamilyAnimations);
        Assert.Equal(NifModelFamilyAnimationResolutionStatus.Resolved, catalog.Status);
        Assert.Equal(MinotaurSkeletonPath, catalog.Skeleton?.VirtualPath, ignoreCase: true);
        var idle = Assert.Single(catalog.Animations, static asset =>
            string.Equals(
                asset.VirtualPath,
                MinotaurHandToHandIdlePath,
                StringComparison.OrdinalIgnoreCase));

        Assert.True(scene.TryGetNodeIndex("Bip01 Head", out var headIndex));
        var bodySkin = Assert.IsType<BethesdaViewerSkinBinding>(
            Assert.Single(scene.MeshParts, static part => part.Skin is not null).Skin);
        Assert.Equal(61, bodySkin.JointNodeIndices.Length);

        var rigidHeadPrefix = Path.GetFileName(MinotaurHeadPath) + "::";
        var rigidHeadParts = scene.MeshParts
            .Where(part => part.Name.StartsWith(rigidHeadPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(rigidHeadParts);
        Assert.All(rigidHeadParts, part =>
        {
            Assert.Null(part.Skin);
            Assert.True(part.NodeIndex.HasValue);
            Assert.True(
                DescendsFrom(scene, part.NodeIndex.Value, headIndex),
                $"Rigid Minotaur head part '{part.Name}' does not descend from canonical Bip01 Head.");
        });

        var binding = BethesdaViewerKfAnimationBinder.ParseAndBind(
            service.ReadModelFamilyAnimationData(idle),
            scene,
            idle.RelativePath);
        Assert.True(binding.HasAcceptedClips, binding.Summary);
        var report = Assert.Single(binding.Reports);
        Assert.Equal(65, report.SourceTrackCount);
        Assert.Equal(65, report.BoundTrackCount);
        Assert.Equal(0, report.UnsupportedTransformTrackCount);
    }

    private static bool DescendsFrom(BethesdaViewerScene scene, int nodeIndex, int ancestorIndex)
    {
        var remaining = scene.Nodes.Count;
        while ((uint)nodeIndex < (uint)scene.Nodes.Count && remaining-- > 0)
        {
            if (nodeIndex == ancestorIndex)
            {
                return true;
            }

            if (scene.Nodes[nodeIndex].ParentIndex is not int parentIndex || parentIndex == nodeIndex)
            {
                return false;
            }

            nodeIndex = parentIndex;
        }

        return false;
    }

    private static float[] SkinPositions(BethesdaViewerScene scene, Matrix4x4[] nodeWorlds)
    {
        var part = Assert.Single(scene.MeshParts);
        var skin = Assert.IsType<BethesdaViewerSkinBinding>(part.Skin);
        var skinMatrices = new Matrix4x4[skin.JointNodeIndices.Length];
        for (var index = 0; index < skinMatrices.Length; index++)
        {
            skinMatrices[index] =
                skin.InverseBindMatrices[index] * nodeWorlds[skin.JointNodeIndices[index]];
        }

        return NifSkinningMath.ApplySkinningPositions(
            part.Submesh.Positions,
            skin.PerVertexInfluences,
            skinMatrices);
    }

    private static byte[] Extract(BsaExtractor extractor, BsaArchive archive, string virtualPath)
    {
        var record = archive.AllFiles.Single(file =>
            string.Equals(file.FullPath, virtualPath, StringComparison.OrdinalIgnoreCase));
        return extractor.ExtractFile(record);
    }
}
