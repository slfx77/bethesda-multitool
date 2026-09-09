using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
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
public sealed class NpcEquipmentSkinTextureRetailTests
{
    private const string GreavesPath = @"meshes\Armor\Iron\F\Greaves.NIF";
    private const string BootsPath = @"meshes\Armor\Iron\F\Boots.NIF";
    private const string CuirassPath = @"meshes\Armor\Iron\F\Cuirass.NIF";
    private const string ImperialLegTexture = @"textures\characters\imperial\female\LegFemale.dds";
    private const string SkinLegTexture = @"body_skin\00085969_lowerbody.dds";
    private const string OrcLegTexture = @"textures\Characters\Orc\Female\LegFemale.dds";
    private const string GreavesArmorTexture = @"textures\armor\iron\f\Greaves.dds";
    private const string BootsArmorTexture = @"textures\armor\iron\m\Boots.dds";
    private const string ImperialUpperTexture = @"textures\characters\imperial\female\UpperBodyFemale.dds";
    private const string CuirassArmorTexture = @"textures\armor\iron\f\Cuirass.dds";
    private const string GeneratedUpperTexture = @"body_egt\00085969_upperbody.dds";

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public async Task RetailMazoga_ExposedGreavesUseOrcLegAtlas_InCpuAndNativeComposition()
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
        var index = await LoadAppearanceIndexAsync(esmPath, cancellationToken);
        var npcRecord = Assert.Contains(0x00085969u, index.Npcs);
        Assert.Equal(0x000191C0u, npcRecord.RaceFormId);
        var greavesList = Assert.Contains(0x00033EC5u, index.LeveledItemRecords);
        Assert.Equal(0x0001C6D0u, greavesList.Entries[0].FormId);
        var greavesRecord = Assert.Contains(0x0001C6D0u, index.Armors);
        Assert.Equal(@"Armor\Iron\F\Greaves.NIF", greavesRecord.FemaleBipedModelPath);
        var cuirassList = Assert.Contains(0x00033EC0u, index.LeveledItemRecords);
        Assert.Equal(0x0001C6D1u, cuirassList.Entries[0].FormId);
        var cuirassRecord = Assert.Contains(0x0001C6D1u, index.Armors);
        Assert.Equal(@"Armor\Iron\F\Cuirass.NIF", cuirassRecord.FemaleBipedModelPath);

        var appearance = new NpcAppearanceFactory(index).Build(0x00085969, npcRecord, "Oblivion.esm");
        Assert.Equal(BethesdaGame.Oblivion, appearance.Game);
        Assert.True(appearance.IsFemale);
        Assert.Equal(OrcLegTexture, appearance.LowerBodyTexturePath);
        var greaves = Assert.Single(appearance.EquippedItems!, item => PathEquals(item.MeshPath, GreavesPath));
        var boots = Assert.Single(appearance.EquippedItems!, item => PathEquals(item.MeshPath, BootsPath));
        var cuirass = Assert.Single(appearance.EquippedItems!, item => PathEquals(item.MeshPath, CuirassPath));
        Assert.Equal(0x08u, greaves.BipedFlags);
        Assert.Equal(0x20u, boots.BipedFlags);
        Assert.Equal(0x04u, cuirass.BipedFlags);

        using var meshArchives = MeshArchiveSet.Open(meshesPath, null);
        AssertAuthoredRetailShapes(meshArchives);

        using var textureResolver = new NifTextureResolver(texturesPath);
        var caches = new NpcCompositionCaches();
        var options = new NpcCompositionOptions { IncludeWeapon = false };
        var skeleton = NpcCompositionPlanner.BuildSkeletonComposition(appearance, meshArchives, caches, options);
        Assert.NotNull(skeleton);
        var plan = new NpcCompositionPlan
        {
            Appearance = appearance,
            Options = options,
            Skeleton = skeleton,
            Head = new NpcHeadCompositionPlan(),
            BodyEquipment = [greaves, boots, cuirass],
            // A generated upper-body key must never replace a TES4 lower-body atlas.
            EffectiveBodyTexturePath = NpcTextureHelpers.BuildNpcBodyEgtTextureKey(0x00085969, "upperbody", null),
            EffectiveHandTexturePath = appearance.HandTexturePath
        };
        Assert.Equal(GeneratedUpperTexture, plan.EffectiveBodyTexturePath);
        // This fixture deliberately supplies no generated upper pixels: that route must stay generic.
        Assert.Null(textureResolver.GetTexture(GeneratedUpperTexture));

        cancellationToken.ThrowIfCancellationRequested();
        var cpuModel = new NifRenderableModel();
        NpcEquipmentAttacher.LoadEquipmentFromPlan(plan, meshArchives, textureResolver, cpuModel);
        AssertComposedTextures(cpuModel.Submeshes);

        cancellationToken.ThrowIfCancellationRequested();
        var exportScene = NpcCompositionExportAdapter.BuildNpc(plan, meshArchives, textureResolver, caches);
        Assert.NotNull(exportScene);
        AssertComposedTextures(exportScene.MeshParts.Select(static part => part.Submesh));
        var nativeScene = BethesdaViewerSceneGlbAdapter.FromGlbScene(
            exportScene,
            "Mazoga retail equipment texture regression",
            BethesdaViewerScenePurpose.NpcAppearance,
            game: BethesdaGame.Oblivion,
            textureSourcePaths: [texturesPath]);
        AssertComposedTextures(nativeScene.MeshParts.Select(static part => part.Submesh));
        Assert.All(nativeScene.MeshParts.Where(static part => IsTargetEquipment(part.Submesh)),
            static part => Assert.NotNull(part.Skin));

        // These are the exact CPU payloads consumed by the native viewport, not a GLB
        // re-export or shader inference. Actual GPU bindings/pixels remain a capture gate.
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = BethesdaViewerSceneDecoder12.Decode(nativeScene);
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        var targetIndices = Enumerable.Range(0, nativeScene.MeshParts.Count)
            .Where(index => IsTargetEquipment(nativeScene.MeshParts[index].Submesh))
            .ToArray();
        Assert.Equal(9, targetIndices.Length);
        foreach (var partIndex in targetIndices)
        {
            var original = nativeScene.MeshParts[partIndex].Submesh;
            var native = decoded.MeshParts[partIndex];
            Assert.True(native.Submesh.HasBump);
            Assert.Equal(original.DiffuseTexturePath, native.Submesh.DiffuseTexturePath);
            Assert.Equal(original.NormalMapTexturePath, native.Submesh.NormalMapTexturePath);
            Assert.Equal(IsExposedGreavesSkin(original), native.NativeSemantics.IsFaceGen);
            Assert.DoesNotContain(posed.UnsupportedMeshParts, part => part.MeshPartIndex == partIndex);
            var posedPart = posed.Mesh.Submeshes[partIndex];
            Assert.True(posedPart.HasBump);
            Assert.Equal(original.DiffuseTexturePath, posedPart.DiffuseTexturePath);
            Assert.Equal(original.NormalMapTexturePath, posedPart.NormalMapTexturePath);
            Assert.Equal(original.Positions.Length / 3, posedPart.Vertices.Length);
            Assert.NotEmpty(posedPart.Indices);
            Assert.All(posedPart.Vertices, static vertex =>
            {
                AssertFinite(vertex.Position);
                AssertFinite(vertex.Normal);
                AssertFinite(vertex.Tangent);
                AssertFinite(vertex.Bitangent);
                Assert.True(vertex.Tangent.LengthSquared() > 1e-8f);
                Assert.True(vertex.Bitangent.LengthSquared() > 1e-8f);
            });
            var normal = textureResolver.GetTexture(original.NormalMapTexturePath!);
            Assert.NotNull(normal);
            Assert.True(normal.Width > 0 && normal.Height > 0);
            Assert.Equal((long)normal.Width * normal.Height * 4, normal.Pixels.LongLength);
        }
    }

    private static async Task<NpcAppearanceIndex> LoadAppearanceIndexAsync(
        string esmPath,
        CancellationToken cancellationToken)
    {
        // The sequential collection protects this shared cache's eviction-owned memory mapping.
        // Do not dispose its result or make another whole-master copy for this fixture.
        var result = await RealAssetEsmCache.LoadAsync(esmPath, cancellationToken);
        var records = result.RawResult.EsmRecords
                      ?? throw new InvalidOperationException("Retail ESM record descriptors are missing.");
        var accessor = result.Accessor
                       ?? throw new InvalidOperationException("Retail ESM memory mapping is missing.");
        return NpcAppearanceIndexBuilder.Build(
            new MmfMemoryAccessor(accessor),
            result.RawResult.FileSize,
            records.MainRecords,
            records.BigEndianRecords > 0,
            records.Game,
            cancellationToken: cancellationToken);
    }

    private static void AssertAuthoredRetailShapes(MeshArchiveSet meshArchives)
    {
        var greaves = NpcExportSceneBuilder.LoadExtractedNif(GreavesPath, meshArchives);
        Assert.NotNull(greaves);
        Assert.Equal(3, greaves.MeshParts.Count);
        var skin = Assert.Single(greaves.MeshParts, static part => part.Submesh.SourceBlockIndex == 1);
        Assert.Equal("LowerBody:0", skin.Name);
        Assert.NotNull(skin.Skin);
        Assert.Equal(484 * 3, skin.Submesh.Positions.Length);
        Assert.Equal(797 * 3, skin.Submesh.Triangles.Length);
        Assert.Equal(ImperialLegTexture, skin.Submesh.DiffuseTexturePath);
        foreach (var blockIndex in new[] { 18, 27 })
        {
            var armor = Assert.Single(greaves.MeshParts, part => part.Submesh.SourceBlockIndex == blockIndex);
            Assert.Equal(GreavesArmorTexture, armor.Submesh.DiffuseTexturePath);
        }

        var boots = NpcExportSceneBuilder.LoadExtractedNif(BootsPath, meshArchives);
        Assert.NotNull(boots);
        var boot = Assert.Single(boots.MeshParts);
        Assert.Equal("Foot", boot.Name);
        Assert.Equal(1840 * 3, boot.Submesh.Positions.Length);
        Assert.Equal(1432 * 3, boot.Submesh.Triangles.Length);
        Assert.Equal(BootsArmorTexture, boot.Submesh.DiffuseTexturePath);

        var cuirass = NpcExportSceneBuilder.LoadExtractedNif(CuirassPath, meshArchives);
        Assert.NotNull(cuirass);
        Assert.Equal(5, cuirass.MeshParts.Count);
        foreach (var (block, vertices, diffuse) in new[]
                 {
                     (1, 444, ImperialUpperTexture), (23, 214, ImperialUpperTexture),
                     (32, 282, CuirassArmorTexture), (41, 1331, CuirassArmorTexture),
                     (49, 932, CuirassArmorTexture)
                 })
        {
            var part = Assert.Single(cuirass.MeshParts, part => part.Submesh.SourceBlockIndex == block);
            Assert.Equal(vertices * 3, part.Submesh.Positions.Length);
            Assert.NotEmpty(part.Submesh.Triangles);
            Assert.NotNull(part.Skin);
            Assert.Equal(diffuse, part.Submesh.DiffuseTexturePath);
        }

        foreach (var part in greaves.MeshParts.Concat(boots.MeshParts).Concat(cuirass.MeshParts))
        {
            // No explicit normal exists in these authored NiTexturingProperty chains.
            Assert.Null(part.Submesh.NormalMapTexturePath);
            Assert.False(part.Submesh.IsFaceGen);
            AssertFiniteTangentFrame(part.Submesh);
        }
    }

    private static void AssertComposedTextures(IEnumerable<RenderableSubmesh> submeshes)
    {
        var parts = submeshes.Where(IsTargetEquipment).ToArray();
        Assert.Equal(9, parts.Length);
        var greaves = parts.Where(static part => PathEquals(part.SourceNifPath, GreavesPath)).ToArray();
        Assert.Equal(3, greaves.Length);
        var skin = Assert.Single(greaves, static part => part.SourceBlockIndex == 1);
        Assert.Equal(SkinLegTexture, skin.DiffuseTexturePath);
        Assert.True(skin.HasAuthoredOblivionBodySkinInputs);
        Assert.Equal(ImperialLegTexture, skin.AuthoredOblivionBodySkinDiffusePath);
        Assert.Equal(@"textures\characters\imperial\female\LegFemale_n.dds", skin.NormalMapTexturePath,
            true);
        foreach (var blockIndex in new[] { 18, 27 })
        {
            var armor = Assert.Single(greaves, part => part.SourceBlockIndex == blockIndex);
            Assert.Equal(GreavesArmorTexture, armor.DiffuseTexturePath);
            Assert.Equal(@"textures\armor\iron\f\Greaves_n.dds", armor.NormalMapTexturePath, true);
        }

        // The armor's shape is named Foot, but its texture is not skin.
        var boot = Assert.Single(parts, static part => PathEquals(part.SourceNifPath, BootsPath));
        Assert.Equal(BootsArmorTexture, boot.DiffuseTexturePath);
        Assert.Equal(@"textures\armor\iron\m\Boots_n.dds", boot.NormalMapTexturePath, true);

        var cuirass = parts.Where(static part => PathEquals(part.SourceNifPath, CuirassPath)).ToArray();
        Assert.Equal(5, cuirass.Length);
        foreach (var block in new[] { 1, 23 })
        {
            var exposedSkin = Assert.Single(cuirass, part => part.SourceBlockIndex == block);
            Assert.Equal(GeneratedUpperTexture, exposedSkin.DiffuseTexturePath);
            Assert.True(exposedSkin.HasAuthoredOblivionBodySkinInputs);
            Assert.Equal(ImperialUpperTexture, exposedSkin.AuthoredOblivionBodySkinDiffusePath);
            Assert.Equal(@"textures\characters\imperial\female\UpperBodyFemale_n.dds",
                exposedSkin.NormalMapTexturePath, true);
        }

        foreach (var block in new[] { 32, 41, 49 })
        {
            var armor = Assert.Single(cuirass, part => part.SourceBlockIndex == block);
            Assert.Equal(CuirassArmorTexture, armor.DiffuseTexturePath);
            Assert.Equal(@"textures\armor\iron\f\Cuirass_n.dds", armor.NormalMapTexturePath, true);
        }

        Assert.All(parts, static part =>
        {
            Assert.Equal(IsExposedGreavesSkin(part), part.IsFaceGen);
            Assert.False(part.UsesClassicHairMaterial);
            AssertFiniteTangentFrame(part);
        });
    }

    private static bool IsExposedGreavesSkin(RenderableSubmesh part)
    {
        return PathEquals(part.SourceNifPath, GreavesPath) && part.SourceBlockIndex == 1;
    }

    private static void AssertFiniteTangentFrame(RenderableSubmesh submesh)
    {
        var tangents = Assert.IsType<float[]>(submesh.Tangents);
        var bitangents = Assert.IsType<float[]>(submesh.Bitangents);
        Assert.Equal(submesh.Positions.Length, tangents.Length);
        Assert.Equal(submesh.Positions.Length, bitangents.Length);
        Assert.All(tangents, static value => Assert.True(float.IsFinite(value)));
        Assert.All(bitangents, static value => Assert.True(float.IsFinite(value)));
        for (var offset = 0; offset < tangents.Length; offset += 3)
        {
            var tangent = new Vector3(tangents[offset], tangents[offset + 1], tangents[offset + 2]);
            var bitangent = new Vector3(bitangents[offset], bitangents[offset + 1], bitangents[offset + 2]);
            Assert.True(tangent.LengthSquared() > 1e-8f);
            Assert.True(bitangent.LengthSquared() > 1e-8f);
        }
    }

    private static void AssertFinite(Vector3 value)
    {
        Assert.True(float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z));
    }

    private static bool PathEquals(string? actual, string expected)
    {
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTargetEquipment(RenderableSubmesh submesh)
    {
        return PathEquals(submesh.SourceNifPath, GreavesPath) || PathEquals(submesh.SourceNifPath, BootsPath) ||
               PathEquals(submesh.SourceNifPath, CuirassPath);
    }
}