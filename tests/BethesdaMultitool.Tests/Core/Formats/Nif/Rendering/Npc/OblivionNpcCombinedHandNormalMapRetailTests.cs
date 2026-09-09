using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class OblivionNpcCombinedHandNormalMapRetailTests
{
    private const string HandMesh = @"meshes\characters\_male\femalehand.nif";
    private const string ImperialDiffuse = @"textures\characters\imperial\female\HandFemale.dds";
    private const string ImperialNormal = @"textures\characters\imperial\female\HandFemale_n.dds";
    private const string SkinDiffuse = @"body_skin\00085969_hands.dds";
    private const string UpperSkinDiffuse = @"body_skin\00085969_upperbody.dds";
    private const string OrcDiffuse = @"textures\Characters\Orc\Female\HandFemale.dds";
    private const string GreavesMesh = @"meshes\Armor\Iron\F\Greaves.NIF";
    private const string BootsMesh = @"meshes\Armor\Iron\F\Boots.NIF";
    private const string CuirassMesh = @"meshes\Armor\Iron\F\Cuirass.NIF";

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public async Task RetailMazoga_CombinedHandRetainsImperialNormalAndOrcAtlas_ThroughBothAssemblersAndNativePose()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esmPath = RealAssetPaths.Masters.Oblivion();
        var meshesPath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        var texturesPath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Textures - Compressed.bsa");
        Assert.SkipWhen(esmPath is null, RealAssetPaths.SkipMessage("Oblivion.esm"));
        Assert.SkipWhen(meshesPath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));
        Assert.SkipWhen(texturesPath is null, RealAssetPaths.SkipMessage("Oblivion - Textures - Compressed.bsa"));

        var cancellationToken = TestContext.Current.CancellationToken;
        var appearanceIndex = await LoadAppearanceIndexAsync(esmPath, cancellationToken);
        var npc = Assert.Contains(0x00085969u, appearanceIndex.Npcs);
        Assert.Equal(0x000191C0u, npc.RaceFormId);
        var appearance = new NpcAppearanceFactory(appearanceIndex).Build(0x00085969, npc, "Oblivion.esm");
        Assert.Equal(BethesdaGame.Oblivion, appearance.Game);
        Assert.True(appearance.IsFemale);
        Assert.Equal(HandMesh, appearance.HandNifPath, true);
        Assert.Equal(OrcDiffuse, appearance.HandTexturePath);
        Assert.Null(appearance.LeftHandNifPath);
        Assert.Null(appearance.RightHandNifPath);
        Assert.Null(appearance.LeftHandEgtPath);
        Assert.Null(appearance.RightHandEgtPath);

        using var meshArchives = MeshArchiveSet.Open(meshesPath, null);
        using var resolver = new NifTextureResolver(texturesPath);
        var raw = NpcMeshHelpers.LoadNifRawFromBsa(HandMesh, meshArchives);
        Assert.NotNull(raw);
        Assert.Equal("NiTriShape", raw.Value.Info.Blocks[1].TypeName);
        Assert.Equal("NiMaterialProperty", raw.Value.Info.Blocks[5].TypeName);
        var material = NifRenderPropertyReader.ReadMaterialProperty(raw.Value.Data, raw.Value.Info, [5]);
        Assert.True(material.HasMaterial);
        Assert.Equal((1f, 1f, 1f), material.Ambient);
        Assert.Equal((1f, 1f, 1f), material.Diffuse);
        Assert.Equal((1f, 1f, 1f), (material.SpecularR, material.SpecularG, material.SpecularB));
        Assert.Equal((0f, 0f, 0f), (material.EmissiveR, material.EmissiveG, material.EmissiveB));
        Assert.Equal(1f, material.Alpha);
        Assert.Equal(10f, material.Glossiness);
        Assert.DoesNotContain(raw.Value.Info.Blocks, static block => block.TypeName is
            "NiSpecularProperty" or "NiVertexColorProperty" or "NiAlphaProperty");

        var authoredCpuModel = NifGeometryExtractor.Extract(raw.Value.Data, raw.Value.Info, bindPoseOnly: true);
        Assert.NotNull(authoredCpuModel);
        var authoredCpuHand = Assert.Single(authoredCpuModel.Submeshes);
        var authoredExport = NpcExportSceneBuilder.LoadExtractedNif(HandMesh, meshArchives);
        Assert.NotNull(authoredExport);
        var authoredExportPart = Assert.Single(authoredExport.MeshParts);
        var authoredExportHand = authoredExportPart.Submesh;
        Assert.NotNull(authoredExportPart.Skin);
        foreach (var hand in new[] { authoredCpuHand, authoredExportHand })
        {
            AssertHandGeometry(hand);
            AssertAuthoredWhiteMaterial(hand);
            Assert.Equal(ImperialDiffuse, hand.DiffuseTexturePath);
            Assert.Null(hand.NormalMapTexturePath);
            Assert.Null(hand.VertexColors);
            Assert.Null(hand.ShaderMetadata);
            Assert.False(hand.IsFaceGen);
        }

        var normal = resolver.GetTexture(ImperialNormal);
        Assert.NotNull(normal);
        Assert.NotEmpty(normal.MipLevels);
        Assert.True(normal.Width > 0 && normal.Height > 0);
        Assert.Equal((long)normal.Width * normal.Height * 4, normal.Pixels.LongLength);

        var caches = new NpcCompositionCaches();
        // Tint generation and head morphing are orthogonal to the original-family hand binding.
        // Keep actual retail equipment/idle composition while making this fixture's diffuse inputs explicit.
        var options = new NpcCompositionOptions
        {
            IncludeWeapon = false, ApplyEgm = false, ApplyEgt = false, IncludeHair = false
        };
        cancellationToken.ThrowIfCancellationRequested();
        var plan = NpcCompositionPlanner.CreatePlan(appearance, meshArchives, resolver, caches, options);
        var handPlan = Assert.Single(plan.BodyParts);
        Assert.Equal(HandMesh, handPlan.MeshPath, true);
        Assert.Equal(OrcDiffuse, handPlan.TextureOverride);
        Assert.Equal(0u, plan.CoveredSlots & 0x10u);
        Assert.NotNull(plan.Skeleton);

        using var headCache = NpcRenderModelCache.CreateHeadMeshCache();
        cancellationToken.ThrowIfCancellationRequested();
        var cpu = NpcBodyBuilder.BuildFromPlan(plan, meshArchives, resolver, caches,
            new NpcRenderModelCache(headCache));
        Assert.NotNull(cpu);
        var cpuHand = Assert.Single(cpu.Submeshes, static part => PathEquals(part.SourceNifPath, HandMesh));
        AssertComposedHand(cpuHand, authoredCpuHand);
        AssertEquipmentMaterials(cpu.Submeshes);

        cancellationToken.ThrowIfCancellationRequested();
        var export = NpcCompositionExportAdapter.BuildNpc(plan, meshArchives, resolver, caches);
        Assert.NotNull(export);
        var exportHand =
            Assert.Single(export.MeshParts, static part => PathEquals(part.Submesh.SourceNifPath, HandMesh));
        AssertComposedHand(exportHand.Submesh, authoredExportHand);
        Assert.NotNull(exportHand.Skin);
        AssertEquipmentMaterials(export.MeshParts.Select(static part => part.Submesh));
        var scene = BethesdaViewerSceneGlbAdapter.FromGlbScene(export, "Mazoga retail combined hand regression",
            BethesdaViewerScenePurpose.NpcAppearance, game: BethesdaGame.Oblivion, textureSourcePaths: [texturesPath]);
        var handIndex = Assert.Single(Enumerable.Range(0, scene.MeshParts.Count),
            index => PathEquals(scene.MeshParts[index].Submesh.SourceNifPath, HandMesh));
        var nativeHand = scene.MeshParts[handIndex];
        AssertComposedHand(nativeHand.Submesh, authoredExportHand);
        var skin = nativeHand.Skin;
        Assert.NotNull(skin);
        Assert.Equal(authoredExportPart.Skin.BoneNames.Length, skin.JointNodeIndices.Length);
        Assert.Equal(skin.JointNodeIndices.Length, skin.InverseBindMatrices.Length);
        for (var joint = 0; joint < skin.JointNodeIndices.Length; joint++)
        {
            var nodeIndex = skin.JointNodeIndices[joint];
            Assert.InRange(nodeIndex, 0, scene.Nodes.Count - 1);
            Assert.Equal(authoredExportPart.Skin.BoneNames[joint], scene.Nodes[nodeIndex].LookupName);
            AssertFinite(skin.InverseBindMatrices[joint]);
            AssertFinite(scene.Nodes[nodeIndex].LocalTransform);
            AssertFinite(scene.Nodes[nodeIndex].WorldTransform);
        }

        Assert.Equal(1708, skin.PerVertexInfluences.Length);
        Assert.All(skin.PerVertexInfluences, influences =>
        {
            Assert.NotEmpty(influences);
            Assert.True(influences.Sum(static influence => influence.Weight) > 0f);
            Assert.All(influences, influence =>
            {
                Assert.InRange(influence.BoneIdx, 0, skin.JointNodeIndices.Length - 1);
                Assert.True(float.IsFinite(influence.Weight) && influence.Weight >= 0f);
            });
        });

        cancellationToken.ThrowIfCancellationRequested();
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        Assert.DoesNotContain(posed.UnsupportedMeshParts, part => part.MeshPartIndex == handIndex);
        foreach (var hand in new[] { decoded.MeshParts[handIndex].Submesh, posed.Mesh.Submeshes[handIndex] })
        {
            Assert.Equal(1, hand.SourceBlockIndex);
            Assert.Equal(1708, hand.Vertices.Length);
            Assert.Equal(2806 * 3, hand.Indices.Length);
            Assert.Equal(SkinDiffuse, hand.DiffuseTexturePath);
            Assert.Equal(ImperialNormal, hand.NormalMapTexturePath, true);
            Assert.True(hand.HasBump);
            Assert.Equal(10f, hand.Glossiness);
            Assert.Equal(1f, hand.MaterialAlpha);
            Assert.Equal(Vector3.One, hand.SpecularColor);
            Assert.Equal(Vector3.One, hand.MaterialDiffuse);
            Assert.Equal(new Vector3(authoredExportHand.SpecularColor.R, authoredExportHand.SpecularColor.G,
                authoredExportHand.SpecularColor.B), hand.SpecularColor);
            Assert.All(hand.Vertices, static vertex =>
            {
                AssertFinite(vertex.Position);
                AssertFinite(vertex.Normal);
                AssertFinite(vertex.Tangent);
                AssertFinite(vertex.Bitangent);
                Assert.True(vertex.Tangent.LengthSquared() > 1e-8f);
                Assert.True(vertex.Bitangent.LengthSquared() > 1e-8f);
                Assert.Equal(Vector4.One, vertex.VertexColor);
            });
        }

        Assert.True(decoded.MeshParts[handIndex].NativeSemantics.IsFaceGen);
        Assert.Null(decoded.MeshParts[handIndex].NativeSemantics.TintColor);
    }

    private static async Task<NpcAppearanceIndex> LoadAppearanceIndexAsync(string esmPath,
        CancellationToken cancellationToken)
    {
        // The shared sequential collection protects the cache-owned mapping from concurrent eviction.
        var result = await RealAssetEsmCache.LoadAsync(esmPath, cancellationToken);
        var records = result.RawResult.EsmRecords
                      ?? throw new InvalidOperationException("Retail ESM record descriptors are missing.");
        var accessor = result.Accessor
                       ?? throw new InvalidOperationException("Retail ESM memory mapping is missing.");
        return NpcAppearanceIndexBuilder.Build(new MmfMemoryAccessor(accessor), result.RawResult.FileSize,
            records.MainRecords, records.BigEndianRecords > 0, records.Game, cancellationToken: cancellationToken);
    }

    private static void AssertComposedHand(RenderableSubmesh hand, RenderableSubmesh unmodified)
    {
        AssertHandGeometry(hand);
        AssertAuthoredWhiteMaterial(hand);
        Assert.Equal(HandMesh, hand.SourceNifPath, true);
        Assert.Equal(SkinDiffuse, hand.DiffuseTexturePath);
        Assert.Equal(ImperialNormal, hand.NormalMapTexturePath, true);
        // Literal authored-white assertions above pin preservation on every route. Agreement is
        // an additional nonmutation check, not proof that retail selects a specular shader pass.
        Assert.Equal(unmodified.SpecularColor, hand.SpecularColor);
        Assert.Equal(unmodified.MaterialDiffuse, hand.MaterialDiffuse);
        Assert.Equal(unmodified.MaterialGlossiness, hand.MaterialGlossiness);
        Assert.Equal(unmodified.MaterialAlpha, hand.MaterialAlpha);
        Assert.Equal(unmodified.TintColor, hand.TintColor);
        Assert.True(hand.IsFaceGen);
        Assert.True(hand.HasAuthoredOblivionBodySkinInputs);
        Assert.Equal(ImperialDiffuse, hand.AuthoredOblivionBodySkinDiffusePath);
        Assert.False(hand.UsesClassicHairMaterial);
    }

    private static void AssertAuthoredWhiteMaterial(RenderableSubmesh hand)
    {
        Assert.Equal((1f, 1f, 1f), hand.SpecularColor);
        Assert.Equal((1f, 1f, 1f), hand.MaterialDiffuse);
        Assert.Equal(10f, hand.MaterialGlossiness);
        Assert.Equal(1f, hand.MaterialAlpha);
    }

    private static void AssertHandGeometry(RenderableSubmesh hand)
    {
        Assert.Equal("Hand", hand.ShapeName);
        Assert.Equal(1, hand.SourceBlockIndex);
        Assert.Equal(1708 * 3, hand.Positions.Length);
        Assert.Equal(2806 * 3, hand.Triangles.Length);
        Assert.All(hand.Positions, static value => Assert.True(float.IsFinite(value)));
        Assert.All(hand.Triangles, static index => Assert.InRange(index, 0, 1707));
        var tangents = Assert.IsType<float[]>(hand.Tangents);
        var bitangents = Assert.IsType<float[]>(hand.Bitangents);
        Assert.Equal(hand.Positions.Length, tangents.Length);
        Assert.Equal(hand.Positions.Length, bitangents.Length);
        for (var offset = 0; offset < tangents.Length; offset += 3)
        {
            var tangent = new Vector3(tangents[offset], tangents[offset + 1], tangents[offset + 2]);
            var bitangent = new Vector3(bitangents[offset], bitangents[offset + 1], bitangents[offset + 2]);
            AssertFinite(tangent);
            AssertFinite(bitangent);
            Assert.True(tangent.LengthSquared() > 1e-8f);
            Assert.True(bitangent.LengthSquared() > 1e-8f);
        }
    }

    private static void AssertEquipmentMaterials(IEnumerable<RenderableSubmesh> submeshes)
    {
        var equipment = submeshes.Where(static part => PathEquals(part.SourceNifPath, GreavesMesh) ||
                                                       PathEquals(part.SourceNifPath, BootsMesh) ||
                                                       PathEquals(part.SourceNifPath, CuirassMesh)).ToArray();
        Assert.Equal(9, equipment.Length);
        foreach (var (mesh, block, diffuse, normal) in new[]
                 {
                     (GreavesMesh, 1, @"body_skin\00085969_lowerbody.dds",
                         @"textures\characters\imperial\female\LegFemale_n.dds"),
                     (GreavesMesh, 18, @"textures\armor\iron\f\Greaves.dds", @"textures\armor\iron\f\Greaves_n.dds"),
                     (GreavesMesh, 27, @"textures\armor\iron\f\Greaves.dds", @"textures\armor\iron\f\Greaves_n.dds"),
                     (BootsMesh, 1, @"textures\armor\iron\m\Boots.dds", @"textures\armor\iron\m\Boots_n.dds"),
                     (CuirassMesh, 1, UpperSkinDiffuse, @"textures\characters\imperial\female\UpperBodyFemale_n.dds"),
                     (CuirassMesh, 23, UpperSkinDiffuse, @"textures\characters\imperial\female\UpperBodyFemale_n.dds"),
                     (CuirassMesh, 32, @"textures\armor\iron\f\Cuirass.dds", @"textures\armor\iron\f\Cuirass_n.dds"),
                     (CuirassMesh, 41, @"textures\armor\iron\f\Cuirass.dds", @"textures\armor\iron\f\Cuirass_n.dds"),
                     (CuirassMesh, 49, @"textures\armor\iron\f\Cuirass.dds", @"textures\armor\iron\f\Cuirass_n.dds")
                 })
        {
            var part = Assert.Single(equipment,
                part => PathEquals(part.SourceNifPath, mesh) && part.SourceBlockIndex == block);
            Assert.Equal(diffuse, part.DiffuseTexturePath, true);
            Assert.Equal(normal, part.NormalMapTexturePath, true);
            var exposedSkin = (PathEquals(mesh, GreavesMesh) && block == 1) ||
                              (PathEquals(mesh, CuirassMesh) && block is 1 or 23);
            Assert.Equal(exposedSkin, part.IsFaceGen);
            Assert.False(part.UsesClassicHairMaterial);
        }
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
        }, static component => Assert.True(float.IsFinite(component)));
    }

    private static bool PathEquals(string? actual, string expected)
    {
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}