using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     Pins the installed Atomic Wrangler advertisement's record, MODS and native material identity.
///     This CPU fixture proves bounded route eligibility, not GPU submission or retail visual parity.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvAdvertisementLightingRetailTests
{
    private const uint ReferenceFormId = 0x00177F9B;
    private const uint BaseFormId = 0x00177F96;
    private const uint CellFormId = 0x000DDF00;
    private const uint TextureSetFormId = 0x0016A885;
    private const string ShapeName = "BB04:13";
    private const string ModelPath = @"meshes\clutter\billboards\BillboardTallNV.NIF";
    private const string DiffusePath = @"textures\clutter\billboards\AtomicWrangler_Billboard.dds";
    private const string NormalPath = @"textures\clutter\billboards\AtomicWrangler_Billboard_n.dds";
    private static readonly float[] WastelandClearFog = [-10f, 200000f, -10f, 200000f, 0.6f, 0.5f];

    [Fact]
    public async Task AtomicWrangler_PinsPlacedModsFaceAndGreaterCutoutActiveAdtEligibility()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.Masters.FalloutNv();
        var meshesBsa = RealAssetPaths.SteamGameFile("Fallout New Vegas", @"Data\Fallout - Meshes.bsa");
        Assert.SkipWhen(esm is null, RealAssetPaths.SkipMessage("Installed FalloutNV.esm"));
        Assert.SkipWhen(meshesBsa is null, RealAssetPaths.SkipMessage("Installed FNV meshes BSA"));

        var result = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var baseRecord = Assert.Single(result.Records.Statics, static record => record.FormId == BaseFormId);
        Assert.Equal("BillboardAtomicWranglerTall", baseRecord.EditorId);
        var rawOverride = Assert.Single(result.Records.AlternateTexturesByFormId[BaseFormId]);
        Assert.Equal(ShapeName, rawOverride.ShapeName);
        Assert.Equal(TextureSetFormId, rawOverride.TextureSetFormId);
        Assert.Equal(1, rawOverride.Index);
        var textureSet = Assert.Single(result.Records.TextureSets,
            static record => record.FormId == TextureSetFormId);
        Assert.Equal("NVBillboardAtomicWrangler", textureSet.EditorId);
        Assert.Equal(DiffusePath["textures\\".Length..], textureSet.DiffuseTexture,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(NormalPath["textures\\".Length..], textureSet.NormalTexture,
            StringComparer.OrdinalIgnoreCase);

        var world = WorldMapOverlayBuilder.BuildFromRecords(result.Records, esm);
        Assert.Equal(BethesdaGame.FalloutNewVegas, world.Game);
        Assert.True(world.PlacedRefs.TryGetEntry(ReferenceFormId, out var placed));
        Assert.Equal(CellFormId, placed.Cell.FormId);
        Assert.Equal(3, placed.Cell.GridX);
        Assert.Equal(11, placed.Cell.GridY);
        Assert.Equal(BaseFormId, placed.Ref.BaseFormId);
        Assert.Equal(ModelPath["meshes\\".Length..], placed.Ref.ModelPath, StringComparer.OrdinalIgnoreCase);
        Assert.False(placed.Ref.IsInitiallyDisabled);

        var resolved = world.AlternateTexturesByFormId[BaseFormId];
        Assert.True(resolved.Overrides.TryGetValue(ShapeName, out var textureOverride));
        Assert.Equal(TextureSetFormId, textureOverride.TextureSetFormId);
        Assert.Equal(1, textureOverride.Index);
        Assert.Equal(DiffusePath, textureOverride.Diffuse, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(NormalPath, textureOverride.Normal, StringComparer.OrdinalIgnoreCase);
        var reference = RenderableReference.TryBuild(placed.Ref, alternateTextures: resolved, game: world.Game);
        Assert.True(reference.HasValue);
        Assert.Equal(ReferenceFormId, reference.Value.FormId);
        Assert.Equal(BaseFormId, reference.Value.BaseFormId);
        Assert.Same(resolved, reference.Value.AlternateTextures);

        using var archives = MeshArchiveSet.Open(meshesBsa, null, false);
        Assert.True(archives.TryExtractFile(ModelPath, out var data, out _), $"Retail NIF missing: {ModelPath}");
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data, nif, skipSkinning: true, treatRootsAsIdentity: true,
            collectBillboards: true, dropBoneAttachedShapes: true));
        Assert.Equal([10, 16, 22], model.Submeshes.Select(static submesh => submesh.SourceBlockIndex).Order());
        var face = Assert.Single(model.Submeshes, static submesh => submesh.SourceBlockIndex == 16);
        AssertRetailFaceMaterial(face);

        // Follow the world decoder's shape-name override lookup into the shared CPU submesh mapper.
        // This does not stand in for the separately required draw-time REFR/block route receipt.
        var decoded = ReferenceSubmeshDecoder12.Decode(face, new ReferenceSubmeshDecodeOptions12(
            textureOverride.Diffuse, textureOverride.Normal, Nif: nif));
        Assert.Equal(16, decoded.SourceBlockIndex);
        Assert.Equal(DiffusePath, decoded.DiffuseTexturePath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(NormalPath, decoded.NormalMapTexturePath, StringComparer.OrdinalIgnoreCase);
        Assert.True(decoded.HasBump);
        Assert.True(decoded.AlphaTest);
        Assert.False(decoded.AlphaBlend);
        Assert.Equal(4, decoded.AlphaTestFunction);
        Assert.Equal(90 / 255f, decoded.AlphaTestThreshold);
        Assert.Equal(1f, decoded.MaterialAlpha);
        Assert.Null(decoded.MaterialAlphaController);
        Assert.Equal(FnvClassicBasicShaderMode.Sls1013VertexColor, decoded.ClassicBasicShaderMode);

        var eligibility = new FnvActiveAdtBaseEligibility(
            world.Game, true, 0, false, false, decoded.AlphaBlend, decoded.AlphaTest,
            decoded.MaterialAlpha, decoded.MaterialAlphaController is not null,
            decoded.ClassicBasicShaderMode, decoded.AlphaTestFunction);
        Assert.True(FnvActiveAdtBasePolicy.IsEligible(eligibility));
        var flags = FnvActiveAdtBasePolicy.ApplyRuntimeFlags(eligibility, 0);
        Assert.NotEqual(0u, flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtFlag);
        Assert.NotEqual(0u, flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtVertexColorFlag);
        Assert.False(FnvActiveAdtBasePolicy.IsEligible(eligibility with { FogEnabled = true }));
        Assert.False(FnvActiveAdtBasePolicy.IsEligible(eligibility with { HasProjectedSunShadow = true }));
    }

    [Fact]
    public async Task WastelandClear_PreservesAuthoredSignedFnamAndAdmitsTheObservedFogRoute()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.Masters.FalloutNv();
        Assert.SkipWhen(esm is null, RealAssetPaths.SkipMessage("Installed FalloutNV.esm"));
        var result = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var weather = Assert.Single(result.Records.Weather, static record => record.FormId == 0x000FFC88);
        Assert.Equal("NVWastelandClear", weather.EditorId);
        Assert.Equal(WastelandClearFog, weather.FogDistances);
        Assert.True(FnvActiveAdtFog.HasUnmodifiedWeatherSource(BethesdaGame.FalloutNewVegas, weather.FogDistances));

        // Canonical handler/resolver join for the retained hour-9 native capture, plus its night pair.
        var day = AtmosphereState.Resolve(9f, weather, game: BethesdaGame.FalloutNewVegas);
        var night = AtmosphereState.Resolve(0f, weather, game: BethesdaGame.FalloutNewVegas);
        Assert.Equal(-10f, day.FogNear);
        Assert.Equal(-10f, night.FogNear);
        Assert.Equal(200000f, day.FogFar);
        Assert.Equal(200000f, night.FogFar);
        Assert.Equal(0.6f, day.FogPower);
        Assert.Equal(0.5f, night.FogPower);
        Assert.True(day.HasUnmodifiedFnvAdtFogSource);
        Assert.True(night.HasUnmodifiedFnvAdtFogSource);
        Assert.True(FnvActiveAdtFog.IsSupported(day.HasUnmodifiedFnvAdtFogSource,
            day.FogNear, day.FogFar, day.FogPower, day.FogColor, day.FogFarColor, day.FogMaxOpacity));
        Assert.True(FnvActiveAdtFog.IsSupported(night.HasUnmodifiedFnvAdtFogSource,
            night.FogNear, night.FogFar, night.FogPower, night.FogColor, night.FogFarColor, night.FogMaxOpacity));
        Assert.True(FnvActiveAdtFog.TryPack(day.FogNear, day.FogFar, day.FogPower, out var packed));
        Assert.Equal(new Vector3(200000f, 200010f, 0.6f), packed);
        Assert.InRange(FnvActiveAdtFog.EvaluateAmount(Vector4.Zero, packed), 0.00262f, 0.00264f);

        var eligibility = new FnvActiveAdtBaseEligibility(BethesdaGame.FalloutNewVegas, true, 0,
            false, true, false, true, 1f, false, FnvClassicBasicShaderMode.Sls1013VertexColor,
            4, true);
        Assert.True(FnvActiveAdtBasePolicy.IsEligible(eligibility));
        Assert.False(FnvActiveAdtBasePolicy.IsEligible(eligibility with { HasSupportedFog = false }));
    }

    private static void AssertRetailFaceMaterial(RenderableSubmesh face)
    {
        Assert.Equal(ShapeName, face.ShapeName);
        Assert.Equal(10, face.VertexCount);
        Assert.Equal(24, face.Triangles.Length);
        Assert.False(face.IsBillboard);
        Assert.False(face.IsLeafBillboard);
        Assert.False(face.IsEmissive);
        Assert.True(face.HasAlphaTest);
        Assert.Equal(4, face.AlphaTestFunction);
        Assert.Equal(90, face.AlphaTestThreshold);
        Assert.False(face.HasAlphaBlend);
        Assert.Equal(1f, face.MaterialAlpha);
        Assert.Null(face.MaterialAlphaController);
        Assert.True(face.UseVertexColors);
        Assert.NotNull(face.VertexColors);
        Assert.True(face.VertexColors.Length >= face.VertexCount * 4);
        Assert.Equal("BSShaderPPLightingProperty", face.ShaderMetadata?.PropertyType);
        Assert.Equal(NifLighting30EmissionPolicy.StandardShaderType, face.ShaderMetadata?.ShaderType);
        Assert.Equal(0x82000000u, face.ShaderMetadata?.ShaderFlags);
        Assert.Equal(0x00000001u, face.ShaderMetadata?.ShaderFlags2);
    }
}