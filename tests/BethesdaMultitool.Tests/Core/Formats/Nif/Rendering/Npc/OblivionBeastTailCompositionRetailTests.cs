using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     Installed-retail gate for actual Argonian/Khajiit tail emission, joint binding and native
///     pose materialization. Metadata alone passes even when an absent joint drops the whole tail.
///     This uses the Actors scene adapters and their pure-CPU D3D12 preparation, without GLB export
///     or a GPU device; visual appearance remains a separate native-capture gate.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class OblivionBeastTailCompositionRetailTests
{
    private const string BeastSkeletonPath = @"meshes\characters\_Male\skeletonbeast.nif";

    // Both installed tail NIFs bind nine joints, in this exact NiSkinInstance order.
    // TailRoot belongs to the skeleton hierarchy but is not in either tail's skin bone list.
    private static readonly string[] AuthoredTailJointNames =
    [
        "Bip01 Pelvis",
        "Bip01 Tail01", "Bip01 Tail02", "Bip01 Tail03", "Bip01 Tail04",
        "Bip01 Tail05", "Bip01 Tail06", "Bip01 Tail07", "Bip01 Tail08"
    ];

    [Theory]
    [InlineData(0x000224ECu, true, @"meshes\characters\argonian\tail.nif",
        @"textures\characters\argonian\female\tail.dds", @"body_skin\000224EC_tail.dds")]
    [InlineData(0x00023E35u, false, @"meshes\characters\khajiit\khajiittail.nif",
        @"textures\characters\khajiit\female\tail.dds", @"body_skin\00023E35_tail.dds")]
    public async Task RetailActor_EmitsAuthoredTailWithCompleteFiniteNativeSkin(
        uint actorFormId,
        bool isFemale,
        string tailPath,
        string tailTexturePath,
        string generatedTexturePath)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esmPath = RealAssetPaths.Masters.Oblivion();
        var meshesPath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        var texturesPath = RealAssetPaths.SteamGameFile(
            "Oblivion", @"Data\Oblivion - Textures - Compressed.bsa");
        Assert.SkipWhen(esmPath is null, RealAssetPaths.SkipMessage("Oblivion.esm"));
        Assert.SkipWhen(meshesPath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));
        Assert.SkipWhen(texturesPath is null,
            RealAssetPaths.SkipMessage("Oblivion - Textures - Compressed.bsa"));

        var cancellationToken = TestContext.Current.CancellationToken;
        var resolver = await LoadAppearanceResolverAsync(esmPath, cancellationToken);
        var appearance = Assert.IsType<NpcAppearance>(
            resolver.ResolveHeadOnly(actorFormId, "Oblivion.esm"));
        Assert.Equal(actorFormId, appearance.NpcFormId);
        Assert.Equal(BethesdaGame.Oblivion, appearance.Game);
        Assert.Equal(isFemale, appearance.IsFemale);
        Assert.Equal(tailPath, appearance.TailNifPath, true);
        Assert.Equal(tailTexturePath, appearance.TailTexturePath, true);
        Assert.Equal(BeastSkeletonPath, appearance.SkeletonNifPath, true);

        using var meshArchives = MeshArchiveSet.Open(meshesPath, null);
        using var textureResolver = new NifTextureResolver(texturesPath);
        var tailTexture = Assert.IsType<DecodedTexture>(textureResolver.GetTexture(tailTexturePath));
        Assert.True(tailTexture.Width > 0 && tailTexture.Height > 0);
        Assert.NotEmpty(tailTexture.Pixels);

        cancellationToken.ThrowIfCancellationRequested();
        var caches = new NpcCompositionCaches();
        var plan = NpcCompositionPlanner.CreatePlan(
            appearance, meshArchives, textureResolver, caches, new NpcCompositionOptions());
        Assert.Equal(0u, plan.CoveredSlots & NpcCompositionPlanner.Tes4TailSlot);
        var tailPlan = Assert.Single(plan.BodyParts, part =>
            string.Equals(part.MeshPath, tailPath, StringComparison.OrdinalIgnoreCase));
        // The CPU plan keeps its original EGT atlas; the admitted scene material below creates
        // a separate body_skin texture for the default SKIN2000 detail modulation.
        var expectedCpuTexturePath = $@"body_egt\{actorFormId:X8}_tail.dds";
        Assert.Equal(expectedCpuTexturePath, tailPlan.TextureOverride);
        var tintedTail = Assert.IsType<DecodedTexture>(textureResolver.GetTexture(expectedCpuTexturePath));
        Assert.Equal(tailTexture.Width, tintedTail.Width);
        Assert.Equal(tailTexture.Height, tintedTail.Height);
        Assert.False(tailTexture.Pixels.AsSpan().SequenceEqual(tintedTail.Pixels));
        for (var alpha = 3; alpha < tailTexture.Pixels.Length; alpha += 4)
        {
            Assert.Equal(tailTexture.Pixels[alpha], tintedTail.Pixels[alpha]);
        }

        var generatedTextureKeys = NpcTextureHelpers.BuildNpcGeneratedTextureKeys(appearance);
        Assert.Contains(expectedCpuTexturePath, generatedTextureKeys);
        Assert.Contains(generatedTexturePath, generatedTextureKeys);
        AssertSkeletonPose(plan);

        cancellationToken.ThrowIfCancellationRequested();
        var assembled = Assert.IsType<GlbScene>(NpcCompositionExportAdapter.BuildNpc(
            plan, meshArchives, textureResolver, caches));
        var scene = BethesdaViewerSceneGlbAdapter.FromGlbScene(
            assembled,
            $"Oblivion.esm:0x{actorFormId:X8}",
            BethesdaViewerScenePurpose.NpcAppearance,
            game: BethesdaGame.Oblivion,
            textureSourcePaths: [texturesPath]);
        NpcBoundaryVertexStitcher.PopulateViewerSceneBoundaryGroups(scene);

        var tail = Assert.Single(scene.MeshParts, part => string.Equals(
            part.Submesh.SourceNifPath, tailPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(generatedTexturePath, tail.Submesh.DiffuseTexturePath);
        // Both exact installed tail sources author ambient .588 and satisfy the proved static scene.
        // Their admitted SKIN2000 route retains the default detail modulation and race-family normal.
        Assert.True(tail.Submesh.HasAuthoredOblivionBodySkinInputs);
        Assert.Equal((0.588f, 0.588f, 0.588f), tail.Submesh.AuthoredOblivionBodySkinAmbientColor);
        Assert.True(tail.Submesh.IsFaceGen);
        var expectedNormalPath = tailTexturePath[..^4] + "_n.dds";
        Assert.Equal(expectedNormalPath, tail.Submesh.NormalMapTexturePath, true);
        var nativeNormal = Assert.IsType<DecodedTexture>(textureResolver.GetTexture(expectedNormalPath));
        Assert.True(nativeNormal.Width > 0 && nativeNormal.Height > 0);
        Assert.NotEmpty(nativeNormal.Pixels);
        AssertPositiveGeometry(tail.Submesh);
        AssertTailSkin(scene, tail);

        // Use the same decoder and current-pose materializer as the native viewport, including
        // its DQS/linear choice and boundary stitching. A rejected part retains its index but has
        // empty geometry, so explicitly inspect this tail in the resulting upload payload.
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        var tailIndex = Assert.Single(Enumerable.Range(0, decoded.MeshParts.Count), index =>
            string.Equals(decoded.MeshParts[index].NativeSemantics.SourceNifPath,
                tailPath, StringComparison.OrdinalIgnoreCase));
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        Assert.DoesNotContain(posed.UnsupportedMeshParts, part => part.MeshPartIndex == tailIndex);
        var posedTail = posed.Mesh.Submeshes[tailIndex];
        Assert.Equal(generatedTexturePath, posedTail.DiffuseTexturePath);
        Assert.Equal(tail.Submesh.Positions.Length / 3, posedTail.Vertices.Length);
        Assert.Equal(tail.Submesh.Triangles.Length, posedTail.Indices.Length);
        Assert.True(float.IsFinite(posedTail.LocalBoundsRadius) && posedTail.LocalBoundsRadius > 0f);
        AssertFinite(posedTail.LocalBoundsCenter);
        Assert.All(posedTail.Vertices, static vertex =>
        {
            AssertFinite(vertex.Position);
            AssertFinite(vertex.Normal);
            AssertFinite(vertex.Tangent);
            AssertFinite(vertex.Bitangent);
        });
        Assert.Contains(Enumerable.Range(0, posedTail.Indices.Length / 3), triangleIndex =>
        {
            var offset = triangleIndex * 3;
            var first = posedTail.Vertices[posedTail.Indices[offset]].Position;
            var second = posedTail.Vertices[posedTail.Indices[offset + 1]].Position;
            var third = posedTail.Vertices[posedTail.Indices[offset + 2]].Position;
            var doubledAreaSquared = Vector3.Cross(second - first, third - first).LengthSquared();
            return float.IsFinite(doubledAreaSquared) && doubledAreaSquared > 1e-8f;
        });
    }

    private static async Task<NpcAppearanceResolver> LoadAppearanceResolverAsync(
        string esmPath,
        CancellationToken cancellationToken)
    {
        // The collection serializes users of this eviction-owning cache. Do not dispose its result
        // or read the entire master again for the second actor.
        var result = await RealAssetEsmCache.LoadAsync(esmPath, cancellationToken);
        var records = result.RawResult.EsmRecords
                      ?? throw new InvalidOperationException("Retail ESM record descriptors are missing.");
        var accessor = result.Accessor
                       ?? throw new InvalidOperationException("Retail ESM memory mapping is missing.");
        return NpcAppearanceResolver.Build(
            new MmfMemoryAccessor(accessor),
            result.RawResult.FileSize,
            records.MainRecords,
            records.BigEndianRecords > 0,
            records.Game,
            cancellationToken: cancellationToken);
    }

    private static void AssertSkeletonPose(NpcCompositionPlan plan)
    {
        var skeleton = Assert.IsType<NpcSkeletonComposition>(plan.Skeleton);
        Assert.Equal(BeastSkeletonPath, skeleton.SkeletonNifPath, true);
        Assert.NotNull(skeleton.BodySkinningBones);
        Assert.NotNull(skeleton.AnimationOverrides);
        AssertFinite(Assert.Contains("Bip01 TailRoot", skeleton.BodySkinningBones));
        for (var index = 1; index <= 8; index++)
        {
            var name = $"Bip01 Tail{index:00}";
            AssertFinite(Assert.Contains(name, skeleton.BodySkinningBones));
            Assert.Contains(name, skeleton.AnimationOverrides);
        }
    }

    private static void AssertTailSkin(BethesdaViewerScene scene, BethesdaViewerMeshPart tail)
    {
        var skin = Assert.IsType<BethesdaViewerSkinBinding>(tail.Skin);
        Assert.Equal(AuthoredTailJointNames.Length, skin.JointNodeIndices.Length);
        Assert.Equal(skin.JointNodeIndices.Length, skin.InverseBindMatrices.Length);
        Assert.Equal(tail.Submesh.Positions.Length / 3, skin.PerVertexInfluences.Length);
        var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < skin.JointNodeIndices.Length; index++)
        {
            var jointIndex = skin.JointNodeIndices[index];
            Assert.InRange(jointIndex, 0, scene.Nodes.Count - 1);
            var joint = scene.Nodes[jointIndex];
            Assert.Equal(BethesdaViewerNodeRole.Skeleton, joint.Role);
            var jointName = joint.LookupName ?? joint.Name;
            Assert.Equal(AuthoredTailJointNames[index], jointName);
            Assert.True(boundNames.Add(jointName));
            AssertFinite(joint.LocalTransform);
            AssertFinite(joint.WorldTransform);
            var inverseBind = skin.InverseBindMatrices[index];
            AssertFinite(inverseBind);
            Assert.True(Matrix4x4.Invert(inverseBind, out var bind));
            AssertFinite(bind);
            AssertFinite(inverseBind * joint.WorldTransform);
        }

        foreach (var name in AuthoredTailJointNames)
        {
            Assert.Contains(name, boundNames);
        }

        Assert.All(skin.PerVertexInfluences, influences =>
        {
            Assert.NotEmpty(influences);
            var totalWeight = 0f;
            foreach (var influence in influences)
            {
                Assert.InRange(influence.BoneIdx, 0, skin.JointNodeIndices.Length - 1);
                Assert.True(float.IsFinite(influence.Weight));
                Assert.InRange(influence.Weight, 0f, 1f);
                totalWeight += influence.Weight;
            }

            Assert.InRange(totalWeight, 0.99f, 1.01f);
        });
    }

    private static void AssertPositiveGeometry(RenderableSubmesh submesh)
    {
        Assert.NotEmpty(submesh.Positions);
        Assert.Equal(0, submesh.Positions.Length % 3);
        Assert.All(submesh.Positions, static value => Assert.True(float.IsFinite(value)));
        Assert.NotEmpty(submesh.Triangles);
        Assert.Equal(0, submesh.Triangles.Length % 3);
        Assert.All(submesh.Triangles, index =>
            Assert.InRange(index, 0u, (uint)(submesh.Positions.Length / 3 - 1)));
        var bindPositions = Assert.IsType<float[]>(submesh.BindPosePositions);
        Assert.Equal(submesh.Positions.Length, bindPositions.Length);
        Assert.All(bindPositions, static value => Assert.True(float.IsFinite(value)));
    }

    private static void AssertFinite(Vector3 value)
    {
        Assert.True(float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z));
    }

    private static void AssertFinite(Matrix4x4 value)
    {
        Assert.All(new[]
        {
            value.M11, value.M12, value.M13, value.M14,
            value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34,
            value.M41, value.M42, value.M43, value.M44
        }, static element => Assert.True(float.IsFinite(element)));
    }
}