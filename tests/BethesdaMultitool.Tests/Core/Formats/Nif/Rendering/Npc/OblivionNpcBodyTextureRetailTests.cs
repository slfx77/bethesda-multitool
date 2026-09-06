using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class OblivionNpcBodyTextureRetailTests
{
    private const string UpperKey = @"body_egt\00085969_upperbody.dds";
    private const string LowerKey = @"body_egt\00085969_lowerbody.dds";
    private const string HandKey = @"body_egt\00085969_hands.dds";
    private const string FootKey = @"body_egt\00085969_feet.dds";
    private const string HandMesh = @"meshes\characters\_male\femalehand.nif";
    private const string UpperSkinKey = @"body_skin\00085969_upperbody.dds";
    private const string LowerSkinKey = @"body_skin\00085969_lowerbody.dds";
    private const string HandSkinKey = @"body_skin\00085969_hands.dds";

    [Fact]
    [Trait("Category", TestCategories.BucketB)]
    public async Task RetailMazoga_TintedAtlasesReachBareHandsEquipmentAndNativePreparation()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.Masters.Oblivion();
        var meshes = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        var textures = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Textures - Compressed.bsa");
        Assert.SkipWhen(esm is null, RealAssetPaths.SkipMessage("Oblivion.esm"));
        Assert.SkipWhen(meshes is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));
        Assert.SkipWhen(textures is null, RealAssetPaths.SkipMessage("Oblivion - Textures - Compressed.bsa"));
        var token = TestContext.Current.CancellationToken;
        var loaded = await RealAssetEsmCache.LoadAsync(esm!, token);
        var records = loaded.RawResult.EsmRecords ?? throw new InvalidOperationException("Retail record descriptors missing");
        var accessor = loaded.Accessor ?? throw new InvalidOperationException("Retail mapping missing");
        var index = NpcAppearanceIndexBuilder.Build(new MmfMemoryAccessor(accessor), loaded.RawResult.FileSize,
            records.MainRecords, records.BigEndianRecords > 0, records.Game, cancellationToken: token);
        var npc = new NpcAppearanceFactory(index).Build(0x85969, Assert.Contains(0x85969u, index.Npcs), "Oblivion.esm");
        Assert.Equal(BethesdaGame.Oblivion, npc.Game);
        Assert.True(npc.IsFemale);
        Assert.Equal(@"textures\Characters\Orc\Female\HandFemale.dds", npc.HandTexturePath);
        Assert.NotNull(npc.FaceGenTextureCoeffs);
        var unchangedCoefficients = npc.FaceGenTextureCoeffs.ToArray();
        using var archives = MeshArchiveSet.Open(meshes!, null);
        using var resolver = new NifTextureResolver(textures!);
        var caches = new NpcCompositionCaches();
        var options = new NpcCompositionOptions { IncludeWeapon = false, IncludeHair = false, ApplyEgm = false };
        var plan = NpcCompositionPlanner.CreatePlan(npc, archives, resolver, caches, options);
        Assert.Equal(UpperKey, plan.EffectiveBodyTexturePath);
        Assert.Equal(LowerKey, plan.EffectiveLowerBodyTexturePath);
        Assert.Equal(HandKey, plan.EffectiveHandTexturePath);
        Assert.Equal(FootKey, plan.EffectiveFootTexturePath);
        Assert.Null(plan.EffectiveTailTexturePath);
        Assert.Equal(unchangedCoefficients, npc.FaceGenTextureCoeffs);
        var hand = Assert.Single(plan.BodyParts);
        Assert.Equal(HandMesh, hand.MeshPath, ignoreCase: true);
        Assert.Equal(HandKey, hand.TextureOverride);
        Assert.Contains(@"meshes\characters\_male\upperbodyhumanfemale.egt", caches.EgtFiles.Keys);
        Assert.Contains(@"meshes\characters\_male\body.egt", caches.EgtFiles.Keys);
        Assert.DoesNotContain(@"meshes\characters\_male\upperbodyhumanmale.egt", caches.EgtFiles.Keys);
        foreach (var pair in new[]
                 {
                     (UpperKey, npc.BodyTexturePath), (LowerKey, npc.LowerBodyTexturePath),
                     (HandKey, npc.HandTexturePath), (FootKey, npc.FootTexturePath)
                 })
        {
            Assert.NotNull(pair.Item2);
            var source = resolver.GetTexture(pair.Item2);
            var tinted = resolver.GetTexture(pair.Item1);
            Assert.NotNull(source);
            Assert.NotNull(tinted);
            Assert.Equal(source.Width, tinted.Width);
            Assert.Equal(source.Height, tinted.Height);
            Assert.False(source.Pixels.AsSpan().SequenceEqual(tinted.Pixels), pair.Item1);
            for (var alpha = 3; alpha < source.Pixels.Length; alpha += 4)
            {
                Assert.Equal(source.Pixels[alpha], tinted.Pixels[alpha]);
            }
            Assert.Contains(pair.Item1, NpcTextureHelpers.BuildNpcGeneratedTextureKeys(npc));
        }
        token.ThrowIfCancellationRequested();
        using var headCache = NpcRenderModelCache.CreateHeadMeshCache();
        var cpu = NpcBodyBuilder.BuildFromPlan(plan, archives, resolver, caches, new NpcRenderModelCache(headCache));
        Assert.NotNull(cpu);
        AssertBoundParts(cpu.Submeshes);
        var exported = NpcCompositionExportAdapter.BuildNpc(plan, archives, resolver, caches);
        Assert.NotNull(exported);
        AssertBoundParts(exported.MeshParts.Select(static p => p.Submesh));
        AssertDetailAtlases(resolver);
        var scene = BethesdaViewerSceneGlbAdapter.FromGlbScene(exported, "Mazoga body tint routing",
            BethesdaViewerScenePurpose.NpcAppearance, game: BethesdaGame.Oblivion, textureSourcePaths: [textures!]);
        AssertBoundParts(scene.MeshParts.Select(static p => p.Submesh));
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        foreach (var partIndex in Enumerable.Range(0, scene.MeshParts.Count)
                     .Where(i => scene.MeshParts[i].Submesh.DiffuseTexturePath is UpperSkinKey or LowerSkinKey or HandSkinKey))
        {
            var source = scene.MeshParts[partIndex].Submesh;
            Assert.Equal(source.DiffuseTexturePath, decoded.MeshParts[partIndex].Submesh.DiffuseTexturePath);
            Assert.Equal(source.DiffuseTexturePath, posed.Mesh.Submeshes[partIndex].DiffuseTexturePath);
            Assert.True(decoded.MeshParts[partIndex].NativeSemantics.IsFaceGen);
            Assert.DoesNotContain(posed.UnsupportedMeshParts, p => p.MeshPartIndex == partIndex);
            Assert.NotEmpty(posed.Mesh.Submeshes[partIndex].Indices);
        }
        // These controls use the real shared planner, not a second implementation of its condition.
        var noTint = NpcCompositionPlanner.CreatePlan(npc, archives, resolver, caches,
            new NpcCompositionOptions { IncludeWeapon = false, IncludeHair = false, ApplyEgm = false, ApplyEgt = false });
        Assert.Equal(npc.BodyTexturePath, noTint.EffectiveBodyTexturePath);
        Assert.Equal(npc.HandTexturePath, noTint.EffectiveHandTexturePath);
        Assert.Equal(npc.LowerBodyTexturePath, noTint.EffectiveLowerBodyTexturePath);
        Assert.Equal(npc.FootTexturePath, noTint.EffectiveFootTexturePath);
        var headOnly = NpcCompositionPlanner.CreatePlan(npc, archives, resolver, caches,
            new NpcCompositionOptions { IncludeWeapon = false, IncludeHair = false, ApplyEgm = false, HeadOnly = true });
        Assert.Empty(headOnly.BodyParts);
        Assert.Equal(npc.HandTexturePath, headOnly.EffectiveHandTexturePath);
        var khajiit = new NpcAppearanceFactory(index).Build(
            0x23E35, Assert.Contains(0x23E35u, index.Npcs), "Oblivion.esm");
        var khajiitPlan = NpcCompositionPlanner.CreatePlan(khajiit, archives, resolver, caches, options);
        var khajiitScene = NpcCompositionExportAdapter.BuildNpc(khajiitPlan, archives, resolver, caches);
        Assert.NotNull(khajiitScene);
        var shoe = Assert.Single(khajiitScene.MeshParts.Select(static p => p.Submesh), static p =>
            string.Equals(p.SourceNifPath, @"meshes\Clothes\MiddleClass\01\M\Shoes.NIF", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Foot", shoe.ShapeName);
        Assert.Equal("foot", shoe.LegacyMaterialName);
        Assert.False(shoe.HasAuthoredOblivionBodySkinInputs);
        Assert.False(shoe.IsFaceGen);
        Assert.Equal(@"textures\clothes\middleclass\Shoe01.dds", shoe.DiffuseTexturePath, ignoreCase: true);
        Assert.Equal(@"textures\clothes\middleclass\Shoe01_n.dds", shoe.NormalMapTexturePath, ignoreCase: true);
        Assert.NotNull(resolver.GetTexture(shoe.NormalMapTexturePath!));
        Assert.Equal((1f, 1f, 1f), shoe.SpecularColor);
        Assert.Equal(10f, shoe.MaterialGlossiness);

        var khajiitCpu = NpcBodyBuilder.BuildFromPlan(khajiitPlan, archives, resolver, caches,
            new NpcRenderModelCache(headCache));
        Assert.NotNull(khajiitCpu);
        AssertTailBinding(khajiitCpu.Submeshes, resolver);
        AssertTailBinding(khajiitScene.MeshParts.Select(static p => p.Submesh), resolver);
        var khajiitViewer = BethesdaViewerSceneGlbAdapter.FromGlbScene(khajiitScene, "Khajiit tail source routing",
            BethesdaViewerScenePurpose.NpcAppearance, game: BethesdaGame.Oblivion, textureSourcePaths: [textures!]);
        AssertTailBinding(khajiitViewer.MeshParts.Select(static p => p.Submesh), resolver);
        var tailIndex = Assert.Single(Enumerable.Range(0, khajiitViewer.MeshParts.Count), i =>
            string.Equals(khajiitViewer.MeshParts[i].Submesh.ShapeName, "Tail", StringComparison.Ordinal));
        var khajiitDecoded = BethesdaViewerSceneDecoder12.Decode(khajiitViewer);
        Assert.True(khajiitDecoded.MeshParts[tailIndex].NativeSemantics.IsFaceGen);
        Assert.Equal(@"body_skin\00023E35_tail.dds",
            khajiitDecoded.MeshParts[tailIndex].Submesh.DiffuseTexturePath, ignoreCase: true);
    }

    private static void AssertTailBinding(IEnumerable<RenderableSubmesh> meshes, NifTextureResolver resolver)
    {
        var tail = Assert.Single(meshes, static p => string.Equals(p.SourceNifPath,
            @"meshes\characters\khajiit\khajiittail.nif", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Tail", tail.ShapeName);
        Assert.True(tail.HasAuthoredOblivionBodySkinInputs);
        Assert.Equal((0.588f, 0.588f, 0.588f), tail.AuthoredOblivionBodySkinAmbientColor);
        Assert.Equal(@"textures\characters\khajiit\female\tail.dds",
            tail.AuthoredOblivionBodySkinDiffusePath, ignoreCase: true);
        Assert.True(tail.IsFaceGen);
        Assert.Equal(@"body_skin\00023E35_tail.dds", tail.DiffuseTexturePath, ignoreCase: true);
        Assert.Equal(@"textures\characters\khajiit\female\tail_n.dds",
            tail.NormalMapTexturePath, ignoreCase: true);
        var normal = resolver.GetTexture(tail.NormalMapTexturePath!);
        Assert.NotNull(normal);
        Assert.Equal(64, normal.Width);
        Assert.Equal(256, normal.Height);
        Assert.Equal((1f, 1f, 1f), tail.MaterialDiffuse);
        Assert.Equal(145, tail.VertexCount);
    }

    private static void AssertBoundParts(IEnumerable<RenderableSubmesh> meshes)
    {
        var parts = meshes.ToArray();
        var hand = Assert.Single(parts, static p => string.Equals(p.SourceNifPath, HandMesh, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HandSkinKey, hand.DiffuseTexturePath);
        Assert.Equal("skin", hand.LegacyMaterialName, ignoreCase: true);
        Assert.Equal(@"textures\characters\imperial\female\HandFemale_n.dds", hand.NormalMapTexturePath, ignoreCase: true);
        Assert.Contains(parts, static p => p.DiffuseTexturePath == UpperSkinKey);
        Assert.Contains(parts, static p => p.DiffuseTexturePath == LowerSkinKey);
        var skin = parts.Where(static p => p.DiffuseTexturePath is UpperSkinKey or LowerSkinKey or HandSkinKey).ToArray();
        Assert.Equal(4, skin.Length);
        Assert.All(skin, static p =>
        {
            Assert.Equal("skin", p.LegacyMaterialName, ignoreCase: true);
            Assert.True(p.HasAuthoredOblivionBodySkinInputs);
            Assert.NotNull(p.AuthoredOblivionBodySkinDiffusePath);
            Assert.True(p.IsFaceGen);
            Assert.NotNull(p.NormalMapTexturePath);
            Assert.Equal(p.Positions.Length, p.Tangents!.Length);
            Assert.Equal(p.Positions.Length, p.Bitangents!.Length);
        });
        Assert.Contains(parts, static p => p.DiffuseTexturePath == @"textures\armor\iron\f\Cuirass.dds");
        Assert.Contains(parts, static p => p.DiffuseTexturePath == @"textures\armor\iron\f\Greaves.dds");
        Assert.Contains(parts, static p => p.DiffuseTexturePath == @"textures\armor\iron\m\Boots.dds");
        Assert.All(parts.Where(static p => p.DiffuseTexturePath?.StartsWith(@"textures\armor\iron\", StringComparison.OrdinalIgnoreCase) == true),
            static p => Assert.False(p.IsFaceGen));
    }

    private static void AssertDetailAtlases(NifTextureResolver resolver)
    {
        foreach (var (atlas, skin) in new[] { (UpperKey, UpperSkinKey), (LowerKey, LowerSkinKey), (HandKey, HandSkinKey) })
        {
            var original = Assert.IsType<BethesdaMultitool.Core.Formats.Dds.DecodedTexture>(resolver.GetTexture(atlas));
            var detail = Assert.IsType<BethesdaMultitool.Core.Formats.Dds.DecodedTexture>(resolver.GetTexture(skin));
            Assert.Equal(original.Width, detail.Width);
            Assert.Equal(original.Height, detail.Height);
            Assert.NotSame(original.Pixels, detail.Pixels);
            for (var index = 0; index < original.Pixels.Length; index++)
            {
                var channel = original.Pixels[index];
                var expected = index % 4 == 3 || channel < 128 ? channel : Math.Min(channel + 1, 255);
                Assert.Equal((byte)expected, detail.Pixels[index]);
            }
        }
    }
}
