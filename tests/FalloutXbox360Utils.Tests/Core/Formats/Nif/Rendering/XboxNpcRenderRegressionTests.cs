using System.Globalization;
using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

[Collection(LoggerSerialTestGroup.Name)]
[Trait("Category", "Slow")]
public sealed class XboxNpcRenderRegressionTests(SampleFileFixture samples)
{
    private const uint UpperBodySlot = 0x04;
    private const uint BooneFormId = 0x00092BD2;
    private const string DocMitchellEditorId = "DocMitchell";
    private const string LucyEditorId = "VMS38RedLucy";
    private const string VeronicaEditorId = "Veronica";
    private const string FiendArmorEditorId = "Fiend1GunCFNV";
    private const string EnclaveArmorEditorId = "Enclave0GunAAFTEMPLATE";
    private const string JayBarnesEditorId = "VMS18JayBarnes";
    private const string GeneralOliverEditorId = "VHDGeneralOliver";
    private const string ArgyllEditorId = "NellisArgyll";
    private const string LucyOutfitDiffusePath = @"textures\armor\lucassimms\OutfitF.dds";

    [Fact]
    public void BuildAndRenderRedLucyBody_Xbox360OutfitRemainsOpaque()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var lucy = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            "Red Lucy",
            LucyEditorId);

        Assert.NotNull(lucy);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Matrix4x4>? skeletonBoneCache = null;
        Dictionary<string, Matrix4x4>? poseDeltaCache = null;

        var model = NpcBodyBuilder.Build(
            lucy!,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            ref skeletonBoneCache,
            ref poseDeltaCache,
            CreateSettings(assets, false));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);

        var outfitSubmeshes = model.Submeshes
            .Where(submesh =>
                string.Equals(
                    submesh.DiffuseTexturePath,
                    LucyOutfitDiffusePath,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(outfitSubmeshes);
        Assert.All(
            outfitSubmeshes,
            submesh =>
            {
                Assert.False(submesh.HasAlphaBlend);
                Assert.False(submesh.HasAlphaTest);
                Assert.Equal(1f, submesh.MaterialAlpha);

                var texture = assets.TextureResolver.GetTexture(submesh.DiffuseTexturePath!);
                Assert.NotNull(texture);

                var alphaState = NifAlphaClassifier.Classify(submesh, texture);
                Assert.Equal(NifAlphaRenderMode.Opaque, alphaState.RenderMode);
                Assert.False(alphaState.HasAlphaBlend);
                Assert.False(alphaState.HasAlphaTest);
            });

        using var _ = new RendererStateScope();
        var outfitModel = CreateModel(outfitSubmeshes);
        var texturedSprite = RenderSprite(outfitModel, assets.TextureResolver);
        var flatSprite = RenderSprite(outfitModel, null);

        Assert.NotNull(texturedSprite);
        Assert.NotNull(flatSprite);

        var texturedCoverage = CountVisiblePixels(texturedSprite!);
        var flatCoverage = CountVisiblePixels(flatSprite!);

        Assert.True(texturedCoverage > 0);
        Assert.True(flatCoverage > 0);
        Assert.InRange(
            texturedCoverage / (float)flatCoverage,
            0.80f,
            1.05f);
    }

    [Fact]
    public void BuildRedLucyBody_Xbox360RendersHolsteredWeaponLayer()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var lucy = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            "Red Lucy",
            LucyEditorId);

        Assert.NotNull(lucy);
        Assert.True(lucy!.WeaponVisual?.IsVisible == true);

        var model = BuildFullBodyModel(assets, lucy);
        AssertVisibleWeaponLayer(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildCraigBooneBody_Xbox360RendersHolsteredWeaponLayer()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var boone = assets.AppearanceResolver.ResolveHeadOnly(BooneFormId, pluginName);

        Assert.NotNull(boone);
        Assert.True(boone!.WeaponVisual?.IsVisible == true);
        Assert.Equal(WeaponAttachmentMode.HolsterPose, boone.WeaponVisual!.AttachmentMode);

        var model = BuildFullBodyModel(assets, boone);
        AssertVisibleWeaponLayer(model, assets.TextureResolver);
    }

    [Theory]
    [InlineData("1ECookCook")]
    [InlineData("VForlornHopeMP03")]
    public void BuildHeavyWeaponNpcBody_Xbox360AnchorsHolsterBackpackToUpperBack(string editorId)
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var npc = ResolveNpcAppearanceByExactEditorId(
            assets.AppearanceResolver,
            pluginName,
            editorId);

        Assert.NotNull(npc);
        Assert.True(npc!.WeaponVisual?.IsVisible == true);
        Assert.Equal(WeaponAttachmentMode.HolsterPose, npc.WeaponVisual!.AttachmentMode);

        var model = BuildFullBodyModel(assets, npc);
        AssertHeavyWeaponBackpackAnchoring(model);
    }

    [Fact]
    public void BuildDocMitchellBody_Xbox360OmitsWeaponInsteadOfUsingHandFallback()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var docMitchell = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            "Doc Mitchell",
            DocMitchellEditorId);

        Assert.NotNull(docMitchell);
        Assert.False(docMitchell!.WeaponVisual?.IsVisible == true);

        var model = BuildFullBodyModel(assets, docMitchell);
        AssertOmittedWeaponLayer(model);
    }

    [Fact]
    public void BuildVeronicaBody_Xbox360RendersEquippedHandMountedWeaponLayer()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var veronica = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            "Veronica",
            VeronicaEditorId);

        Assert.NotNull(veronica);
        Assert.True(veronica!.WeaponVisual?.IsVisible == true);
        Assert.Equal(WeaponAttachmentMode.EquippedHandMounted, veronica.WeaponVisual!.AttachmentMode);
        Assert.NotNull(veronica.WeaponVisual!.AddonMeshes);
        Assert.NotEmpty(veronica.WeaponVisual.AddonMeshes!);
        Assert.Null(veronica.WeaponVisual.EquippedPoseKfPath);
        Assert.True(veronica.WeaponVisual.PreferEquippedForearmMount);

        var model = BuildFullBodyModel(assets, veronica);
        AssertVisibleWeaponLayer(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildArgyllBody_Xbox360AttachesPipBoyRigidArmorToLeftArmInsteadOfOrigin()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var argyll = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            "Argyll",
            ArgyllEditorId);

        Assert.NotNull(argyll);
        Assert.NotNull(argyll!.EquippedItems);
        Assert.Contains(
            argyll.EquippedItems!,
            item => item.MeshPath.EndsWith(@"PipBoyArmNPC.NIF", StringComparison.OrdinalIgnoreCase));

        var model = BuildFullBodyModel(assets, argyll);
        var pipboySubmeshes = model.Submeshes
            .Where(submesh =>
                string.Equals(submesh.DiffuseTexturePath, @"textures\pipboy3000\PipBoyArm01.dds",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(submesh.DiffuseTexturePath, @"textures\pipboy3000\GreenScreen.dds",
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(pipboySubmeshes);

        var (minX, maxX, minZ, maxZ) = ComputeBounds(pipboySubmeshes);
        Assert.True(maxX < -5f, $"Expected Pip-Boy rigid housing on the left arm, got X range {minX:F1}..{maxX:F1}");
        Assert.InRange(minZ, 40f, 90f);
        Assert.InRange(maxZ, 45f, 95f);
    }

    [Fact]
    public void BuildCraigBooneBody_EmbeddedWeaponWithoutNode_OmitsWeaponLayerInsteadOfFallingBack()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var boone = assets.AppearanceResolver.ResolveHeadOnly(BooneFormId, pluginName);

        Assert.NotNull(boone);
        Assert.True(boone!.WeaponVisual?.IsVisible == true);

        var embeddedWeaponNpc = CloneAppearance(
            boone,
            new WeaponVisual
            {
                WeaponFormId = boone.WeaponVisual!.WeaponFormId,
                EditorId = boone.WeaponVisual.EditorId,
                SourceKind = boone.WeaponVisual.SourceKind,
                IsVisible = true,
                WeaponType = boone.WeaponVisual.WeaponType,
                AttachmentMode = boone.WeaponVisual.AttachmentMode,
                MeshPath = boone.WeaponVisual.MeshPath,
                HolsterProfileKey = boone.WeaponVisual.HolsterProfileKey,
                RuntimeActorFormId = boone.WeaponVisual.RuntimeActorFormId,
                AmmoFormId = boone.WeaponVisual.AmmoFormId,
                IsEmbeddedWeapon = true,
                EmbeddedWeaponNode = null
            });

        var model = BuildFullBodyModel(assets, embeddedWeaponNpc);
        AssertOmittedWeaponLayer(model);
    }

    [Fact]
    public void BuildAndRenderBooneHead_Xbox360TransparentHairBlendsOverFace()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var boone = assets.AppearanceResolver.ResolveHeadOnly(BooneFormId, pluginName);

        Assert.NotNull(boone);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var model = NpcHeadBuilder.Build(
            boone,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(assets, true));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);

        var hairSubmeshes = model.Submeshes.Where(IsHairLikeSubmesh).ToList();
        var faceSubmeshes = model.Submeshes.Where(submesh => !IsHairLikeSubmesh(submesh)).ToList();

        Assert.NotEmpty(hairSubmeshes);
        Assert.NotEmpty(faceSubmeshes);

        using var _ = new RendererStateScope();
        var fullSprite = RenderSprite(model, assets.TextureResolver);
        var faceSprite = RenderSprite(CreateModel(faceSubmeshes, model), assets.TextureResolver);
        var hairSprite = RenderSprite(CreateModel(hairSubmeshes, model), assets.TextureResolver);

        Assert.NotNull(fullSprite);
        Assert.NotNull(faceSprite);
        Assert.NotNull(hairSprite);

        var evidence = AnalyzeHairComposition(fullSprite!, faceSprite!, hairSprite!);

        Assert.True(evidence.TranslucentCandidateCount > 0);
        Assert.True(evidence.TranslucentBlendCount > 10);
        Assert.True(evidence.OpaqueHairCandidateCount > 0);
        Assert.True(evidence.OpaqueHairDominanceCount > 10);
    }

    [Fact]
    public void BuildRedLucyHeadOnly_Xbox360AttachmentsRemainAnchoredToFace()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var lucy = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            "Red Lucy",
            LucyEditorId);

        Assert.NotNull(lucy);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var model = NpcHeadBuilder.Build(
            lucy!,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(assets, true));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);
        AssertAttachmentAnchoring(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildRedLucyFullBodyHead_Xbox360AttachmentsRemainAnchoredToFace()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var lucy = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            "Red Lucy",
            LucyEditorId);

        Assert.NotNull(lucy);
        var model = BuildFullBodyHeadModel(assets, lucy!);
        AssertAttachmentAnchoring(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildCraigBooneFullBodyHead_Xbox360AttachmentsRemainAnchoredToFace()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var boone = assets.AppearanceResolver.ResolveHeadOnly(BooneFormId, pluginName);

        Assert.NotNull(boone);

        var model = BuildFullBodyHeadModel(assets, boone);
        AssertAttachmentAnchoring(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildCraigBooneFullBodyHead_Xbox360HeadEquipmentAnchorsOverFace()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var boone = assets.AppearanceResolver.ResolveHeadOnly(BooneFormId, pluginName);

        Assert.NotNull(boone);

        var model = BuildFullBodyHeadModel(assets, boone);
        AssertHeadEquipmentAnchoring(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildGeneralOliverFullBodyHead_Xbox360HeadEquipmentAnchorsOverFace()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var oliver = ResolveNpcAppearanceByExactEditorId(
            assets.AppearanceResolver,
            pluginName,
            GeneralOliverEditorId);

        Assert.NotNull(oliver);

        var model = BuildFullBodyHeadModel(assets, oliver!);
        AssertHeadEquipmentAnchoring(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildCraigBooneHeadOnly_Xbox360AttachmentsRemainAnchoredToFace()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var boone = assets.AppearanceResolver.ResolveHeadOnly(BooneFormId, pluginName);

        Assert.NotNull(boone);

        var model = BuildHeadOnlyModel(assets, boone);
        AssertAttachmentAnchoring(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildCraigBooneHeadOnly_Xbox360IncludesHeadEquipmentUnlessNoEquip()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var boone = assets.AppearanceResolver.ResolveHeadOnly(BooneFormId, pluginName);

        Assert.NotNull(boone);
        Assert.NotNull(boone.EquippedItems);
        Assert.Contains(boone.EquippedItems!, item => NpcRenderHelpers.IsHeadEquipment(item.BipedFlags));

        var withEquip = BuildHeadOnlyModel(assets, boone);
        var withoutEquip = BuildHeadOnlyModel(assets, boone, true);

        Assert.Contains(withEquip.Submeshes, submesh => submesh.RenderOrder == 3);
        Assert.DoesNotContain(withoutEquip.Submeshes, submesh => submesh.RenderOrder == 3);
    }

    [Fact]
    public void BuildDocMitchellHeadOnly_Xbox360AttachmentsRemainAnchoredToFace()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var docMitchell = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            "Doc Mitchell",
            DocMitchellEditorId);

        Assert.NotNull(docMitchell);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var model = NpcHeadBuilder.Build(
            docMitchell!,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(assets, true));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);
        AssertAttachmentAnchoring(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildVeronicaSantiagoHeadOnly_Xbox360AttachmentsRemainAnchoredToFace()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var veronica = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            "Veronica",
            VeronicaEditorId);

        Assert.NotNull(veronica);

        var model = BuildHeadOnlyModel(assets, veronica!);
        AssertAttachmentAnchoring(model, assets.TextureResolver);
    }

    [Theory]
    [InlineData(VeronicaEditorId)]
    [InlineData(FiendArmorEditorId)]
    public void BuildHeadOnly_Xbox360HairAndHeadPartsRemainAnchoredToFace(string editorId)
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var npc = ResolveNpcAppearanceByExactEditorId(
            assets.AppearanceResolver,
            Path.GetFileName(assets.EsmPath),
            editorId);

        Assert.NotNull(npc);

        var model = BuildHeadOnlyModel(assets, npc!);
        AssertHairAnchoring(model, assets.TextureResolver);
    }

    [Theory]
    [InlineData(VeronicaEditorId)]
    [InlineData(FiendArmorEditorId)]
    [InlineData(EnclaveArmorEditorId)]
    [InlineData(JayBarnesEditorId)]
    [InlineData(GeneralOliverEditorId)]
    public void BuildFullBody_Xbox360MatchesPcBounds(string editorId)
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        using var xboxAssets = CreateXboxAssets();
        using var pcAssets = CreatePcAssets();

        var xboxNpc = ResolveNpcAppearanceByExactEditorId(
            xboxAssets.AppearanceResolver,
            Path.GetFileName(xboxAssets.EsmPath),
            editorId);
        var pcNpc = ResolveNpcAppearanceByExactEditorId(
            pcAssets.AppearanceResolver,
            Path.GetFileName(pcAssets.EsmPath),
            editorId);

        Assert.NotNull(xboxNpc);
        Assert.NotNull(pcNpc);

        var xboxModel = BuildFullBodyModel(xboxAssets, xboxNpc!);
        var pcModel = BuildFullBodyModel(pcAssets, pcNpc!);

        using var _ = new RendererStateScope();

        var pcFront = RenderSprite(pcModel, null, 384, 0f);
        var xboxFront = RenderSprite(xboxModel, null, 384, 0f);
        var pcLeft = RenderSprite(pcModel, null, 384, 90f);
        var xboxLeft = RenderSprite(xboxModel, null, 384, 90f);

        AssertSpriteBoundsSimilar(pcFront, xboxFront, editorId, "front", "full-body");
        AssertSpriteBoundsSimilar(pcLeft, xboxLeft, editorId, "left", "full-body");
    }

    [Theory]
    [InlineData(EnclaveArmorEditorId)]
    [InlineData(FiendArmorEditorId)]
    [InlineData(JayBarnesEditorId)]
    public void BuildEquippedArmor_Xbox360MatchesPcBoundsAndUsesExpandedSkinning(string editorId)
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        using var xboxAssets = CreateXboxAssets();
        using var pcAssets = CreatePcAssets();

        var xboxNpc = ResolveNpcAppearanceByExactEditorId(
            xboxAssets.AppearanceResolver,
            Path.GetFileName(xboxAssets.EsmPath),
            editorId);
        var pcNpc = ResolveNpcAppearanceByExactEditorId(
            pcAssets.AppearanceResolver,
            Path.GetFileName(pcAssets.EsmPath),
            editorId);

        Assert.NotNull(xboxNpc);
        Assert.NotNull(pcNpc);

        var xboxArmorMeshPath = ResolvePrimaryTorsoArmorMeshPath(xboxNpc!);
        var pcArmorMeshPath = ResolvePrimaryTorsoArmorMeshPath(pcNpc!);

        Assert.Equal(pcArmorMeshPath, xboxArmorMeshPath);

        var diagnosticReport = InspectSkinning(xboxAssets, xboxArmorMeshPath);
        AssertArmorUsesExpandedInfluences(diagnosticReport, editorId, xboxArmorMeshPath);

        var xboxArmorModel = BuildArmorModel(xboxAssets, xboxNpc!, xboxArmorMeshPath);
        var pcArmorModel = BuildArmorModel(pcAssets, pcNpc!, pcArmorMeshPath);

        Assert.True(
            xboxArmorModel.WasSkinned,
            $"Expected Xbox armor '{xboxArmorMeshPath}' to remain skinned for {editorId}");
        Assert.True(
            pcArmorModel.WasSkinned,
            $"Expected PC armor '{pcArmorMeshPath}' to remain skinned for {editorId}");

        using var _ = new RendererStateScope();

        var pcFront = RenderSprite(pcArmorModel, null, 384, 0f);
        var xboxFront = RenderSprite(xboxArmorModel, null, 384, 0f);
        var pcLeft = RenderSprite(pcArmorModel, null, 384, 90f);
        var xboxLeft = RenderSprite(xboxArmorModel, null, 384, 90f);

        AssertSpriteBoundsSimilar(pcFront, xboxFront, editorId, "front", pcArmorMeshPath);
        AssertSpriteBoundsSimilar(pcLeft, xboxLeft, editorId, "left", pcArmorMeshPath);
    }

    private NpcRenderAssets CreateXboxAssets()
    {
        var meshesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Meshes.bsa");
        var texturesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures.bsa");
        var textures2Bsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures2.bsa");

        Assert.SkipWhen(meshesBsa is null, "Xbox 360 final meshes BSA not available");
        Assert.SkipWhen(texturesBsa is null, "Xbox 360 final textures BSA not available");

        var esm = EsmFileLoader.Load(samples.Xbox360FinalEsm!, false);
        Assert.NotNull(esm);

        var texturePaths = textures2Bsa == null
            ? [texturesBsa!]
            : new[] { texturesBsa!, textures2Bsa! };

        return new NpcRenderAssets(
            samples.Xbox360FinalEsm!,
            meshesBsa!,
            NpcMeshArchiveSet.Open(meshesBsa!, null),
            new NifTextureResolver(texturePaths),
            NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian));
    }

    private NpcRenderAssets CreatePcAssets()
    {
        var meshesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa");
        var texturesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Textures.bsa");
        var textures2Bsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Textures2.bsa");

        Assert.SkipWhen(meshesBsa is null, "PC final meshes BSA not available");
        Assert.SkipWhen(texturesBsa is null, "PC final textures BSA not available");
        Assert.SkipWhen(textures2Bsa is null, "PC final textures2 BSA not available");

        var esm = EsmFileLoader.Load(samples.PcFinalEsm!, false);
        Assert.NotNull(esm);

        return new NpcRenderAssets(
            samples.PcFinalEsm!,
            meshesBsa!,
            NpcMeshArchiveSet.Open(meshesBsa!, null),
            new NifTextureResolver(texturesBsa!, textures2Bsa!),
            NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian));
    }

    private static NpcRenderSettings CreateSettings(
        NpcRenderAssets assets,
        bool headOnly,
        bool noEquip = false)
    {
        return new NpcRenderSettings
        {
            MeshesBsaPath = "unused",
            EsmPath = assets.EsmPath,
            OutputDir = Path.GetTempPath(),
            HeadOnly = headOnly,
            NoEquip = noEquip,
            ForceCpu = true,
            NoEgt = false,
            NoEgm = false
        };
    }

    private static SpriteResult? RenderSprite(
        NifRenderableModel model,
        NifTextureResolver? textureResolver,
        int fixedSize = 160,
        float azimuthDeg = 90f,
        float elevationDeg = 0f)
    {
        return NifSpriteRenderer.Render(
            model,
            textureResolver,
            1f,
            64,
            192,
            azimuthDeg,
            elevationDeg,
            fixedSize);
    }

    private static NifRenderableModel CreateModel(
        IEnumerable<RenderableSubmesh> submeshes,
        NifRenderableModel? boundsSource = null)
    {
        var model = new NifRenderableModel();
        foreach (var submesh in submeshes)
        {
            model.Submeshes.Add(submesh);
            model.ExpandBounds(submesh.Positions);
        }

        if (boundsSource != null)
        {
            model.MinX = boundsSource.MinX;
            model.MinY = boundsSource.MinY;
            model.MinZ = boundsSource.MinZ;
            model.MaxX = boundsSource.MaxX;
            model.MaxY = boundsSource.MaxY;
            model.MaxZ = boundsSource.MaxZ;
        }

        return model;
    }

    private static int CountVisiblePixels(SpriteResult sprite)
    {
        return Enumerable.Range(0, sprite.Pixels.Length / 4)
            .Count(index => sprite.Pixels[index * 4 + 3] > 0);
    }

    private static NifRenderableModel BuildHeadOnlyModel(
        NpcRenderAssets assets,
        NpcAppearance npc,
        bool noEquip = false)
    {
        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var model = NpcHeadBuilder.Build(
            npc,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(assets, true, noEquip));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);
        return model;
    }

    private static NifRenderableModel BuildFullBodyHeadModel(NpcRenderAssets assets, NpcAppearance npc)
    {
        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Matrix4x4>? skeletonBoneCache = null;
        Dictionary<string, Matrix4x4>? poseDeltaCache = null;

        var bodyModel = NpcBodyBuilder.Build(
            npc,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            ref skeletonBoneCache,
            ref poseDeltaCache,
            CreateSettings(assets, false));

        Assert.NotNull(bodyModel);
        Assert.NotNull(skeletonBoneCache);

        var bonelessHeadAttachmentTransform = NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(
            skeletonBoneCache,
            poseDeltaCache);

        var fullBodyHeadModel = NpcHeadBuilder.Build(
            npc,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(assets, false),
            idlePoseBones: skeletonBoneCache,
            headEquipmentTransformOverride: bonelessHeadAttachmentTransform);

        Assert.NotNull(fullBodyHeadModel);
        Assert.True(fullBodyHeadModel.HasGeometry);
        return fullBodyHeadModel;
    }

    private static NifRenderableModel BuildFullBodyModel(NpcRenderAssets assets, NpcAppearance npc)
    {
        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Matrix4x4>? skeletonBoneCache = null;
        Dictionary<string, Matrix4x4>? poseDeltaCache = null;

        var bodyModel = NpcBodyBuilder.Build(
            npc,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            ref skeletonBoneCache,
            ref poseDeltaCache,
            CreateSettings(assets, false));

        Assert.NotNull(bodyModel);
        Assert.True(bodyModel.HasGeometry);
        return bodyModel;
    }

    private static NifRenderableModel BuildArmorModel(
        NpcRenderAssets assets,
        NpcAppearance npc,
        string armorMeshPath)
    {
        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Matrix4x4>? skeletonBoneCache = null;
        Dictionary<string, Matrix4x4>? poseDeltaCache = null;

        _ = NpcBodyBuilder.Build(
            npc,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            ref skeletonBoneCache,
            ref poseDeltaCache,
            CreateSettings(assets, false));

        Assert.NotNull(skeletonBoneCache);

        var raw = LoadNifRaw(assets, armorMeshPath);
        var armorModel = NifGeometryExtractor.Extract(
            raw.Data,
            raw.Info,
            null,
            externalBoneTransforms: skeletonBoneCache,
            useDualQuaternionSkinning: true);

        Assert.NotNull(armorModel);
        Assert.True(armorModel.HasGeometry);
        return armorModel;
    }

    private static (byte[] Data, NifInfo Info) LoadNifRaw(
        NpcRenderAssets assets,
        string meshPath)
    {
        var raw = NpcRenderHelpers.LoadNifRawFromBsa(meshPath, assets.MeshArchives);
        Assert.True(raw.HasValue, $"Failed to load NIF '{meshPath}' from {assets.MeshesBsaPath}");
        return raw!.Value;
    }

    private static NifSkinningDiagnosticReport InspectSkinning(
        NpcRenderAssets assets,
        string meshPath)
    {
        var raw = LoadNifRaw(assets, meshPath);
        return NifSkinningDiagnostics.Inspect(raw.Data, raw.Info);
    }

    private static void AssertAttachmentAnchoring(
        NifRenderableModel model,
        NifTextureResolver textureResolver)
    {
        var faceSubmeshes = model.Submeshes.Where(IsBaseFaceSubmesh).ToList();
        var attachmentSubmeshes = model.Submeshes.Where(IsAnchoredAttachmentSubmesh).ToList();
        var eyeSubmeshes = model.Submeshes.Where(IsEyeSubmesh).ToList();

        Assert.NotEmpty(faceSubmeshes);
        Assert.NotEmpty(attachmentSubmeshes);
        Assert.NotEmpty(eyeSubmeshes);

        using var _ = new RendererStateScope();
        var faceSprite = RenderSprite(CreateModel(faceSubmeshes, model), textureResolver, 320);
        var attachmentSprite = RenderSprite(CreateModel(attachmentSubmeshes, model), textureResolver, 320);
        var eyeSprite = RenderSprite(CreateModel(eyeSubmeshes, model), textureResolver, 320);

        Assert.NotNull(faceSprite);
        Assert.NotNull(attachmentSprite);
        Assert.NotNull(eyeSprite);

        var faceBounds = GetVisibleBounds(faceSprite!);
        var attachmentBounds = GetVisibleBounds(attachmentSprite!);
        var eyeBounds = GetVisibleBounds(eyeSprite!);

        Assert.NotNull(faceBounds);
        Assert.NotNull(attachmentBounds);
        Assert.NotNull(eyeBounds);

        var eyeEnvelope = ExpandBounds(faceBounds!.Value, eyeSprite!.Width, eyeSprite.Height, 0.08f, 0.10f);
        var attachmentEnvelope =
            ExpandBounds(faceBounds.Value, attachmentSprite!.Width, attachmentSprite.Height, 0.55f, 0.45f);

        var eyeInsideRatio = CountVisiblePixelsInsideBounds(eyeSprite, eyeEnvelope) /
                             (float)CountVisiblePixels(eyeSprite);
        var attachmentInsideRatio = CountVisiblePixelsInsideBounds(attachmentSprite, attachmentEnvelope) /
                                    (float)CountVisiblePixels(attachmentSprite);

        Assert.True(
            eyeEnvelope.Contains(eyeBounds.Value.CenterX, eyeBounds.Value.CenterY),
            $"Eye bbox center ({eyeBounds.Value.CenterX:F1},{eyeBounds.Value.CenterY:F1}) fell outside face envelope [{eyeEnvelope.MinX},{eyeEnvelope.MinY}]→[{eyeEnvelope.MaxX},{eyeEnvelope.MaxY}]");
        Assert.True(
            attachmentEnvelope.Contains(attachmentBounds.Value.CenterX, attachmentBounds.Value.CenterY),
            $"Attachment bbox center ({attachmentBounds.Value.CenterX:F1},{attachmentBounds.Value.CenterY:F1}) fell outside face envelope [{attachmentEnvelope.MinX},{attachmentEnvelope.MinY}]→[{attachmentEnvelope.MaxX},{attachmentEnvelope.MaxY}]");
        Assert.True(eyeInsideRatio >= 0.45f, $"Eye coverage inside face envelope too low: {eyeInsideRatio:F3}");
        Assert.True(
            attachmentInsideRatio >= 0.45f,
            $"Attachment coverage inside face envelope too low: {attachmentInsideRatio:F3}");
    }

    private static void AssertHeadEquipmentAnchoring(
        NifRenderableModel model,
        NifTextureResolver textureResolver)
    {
        var faceSubmeshes = model.Submeshes.Where(IsBaseFaceSubmesh).ToList();
        var headEquipmentSubmeshes = model.Submeshes
            .Where(submesh => submesh.RenderOrder == 3)
            .ToList();

        Assert.NotEmpty(faceSubmeshes);
        Assert.NotEmpty(headEquipmentSubmeshes);

        using var _ = new RendererStateScope();
        var faceSprite = RenderSprite(CreateModel(faceSubmeshes, model), textureResolver, 320);
        var equipmentSprite = RenderSprite(CreateModel(headEquipmentSubmeshes, model), textureResolver, 320);

        Assert.NotNull(faceSprite);
        Assert.NotNull(equipmentSprite);

        var faceBounds = GetVisibleBounds(faceSprite!);
        var equipmentBounds = GetVisibleBounds(equipmentSprite!);

        Assert.NotNull(faceBounds);
        Assert.NotNull(equipmentBounds);

        var equipmentEnvelope = ExpandBounds(
            faceBounds!.Value,
            equipmentSprite!.Width,
            equipmentSprite.Height,
            0.35f,
            0.55f);
        var equipmentInsideRatio = CountVisiblePixelsInsideBounds(equipmentSprite, equipmentEnvelope) /
                                   (float)CountVisiblePixels(equipmentSprite);
        var maxCenterOffsetX = faceBounds.Value.Width * 0.55f;
        var centerOffsetX = Math.Abs(equipmentBounds!.Value.CenterX - faceBounds.Value.CenterX);

        Assert.True(
            equipmentEnvelope.Contains(equipmentBounds.Value.CenterX, equipmentBounds.Value.CenterY),
            $"Head equipment bbox center ({equipmentBounds.Value.CenterX:F1},{equipmentBounds.Value.CenterY:F1}) fell outside face envelope [{equipmentEnvelope.MinX},{equipmentEnvelope.MinY}]→[{equipmentEnvelope.MaxX},{equipmentEnvelope.MaxY}]");
        Assert.True(
            centerOffsetX <= maxCenterOffsetX,
            $"Head equipment center X offset {centerOffsetX:F1} exceeded face-relative tolerance {maxCenterOffsetX:F1}");
        Assert.True(
            equipmentInsideRatio >= 0.55f,
            $"Head equipment coverage inside face envelope too low: {equipmentInsideRatio:F3}");
    }

    private static void AssertHairAnchoring(
        NifRenderableModel model,
        NifTextureResolver textureResolver)
    {
        var faceSubmeshes = model.Submeshes.Where(IsBaseFaceSubmesh).ToList();
        var hairSubmeshes = model.Submeshes.Where(IsHairLikeSubmesh).ToList();

        Assert.NotEmpty(faceSubmeshes);
        Assert.NotEmpty(hairSubmeshes);

        using var _ = new RendererStateScope();
        var faceSprite = RenderSprite(
            CreateModel(faceSubmeshes, model),
            textureResolver,
            320,
            90f);
        var hairSprite = RenderSprite(
            CreateModel(hairSubmeshes, model),
            textureResolver,
            320,
            90f);

        Assert.NotNull(faceSprite);
        Assert.NotNull(hairSprite);

        var faceBounds = GetVisibleBounds(faceSprite!);
        var hairBounds = GetVisibleBounds(hairSprite!);

        Assert.NotNull(faceBounds);
        Assert.NotNull(hairBounds);

        var hairEnvelope = ExpandBounds(
            faceBounds!.Value,
            hairSprite!.Width,
            hairSprite.Height,
            0.22f,
            0.30f);
        var hairInsideRatio = CountVisiblePixelsInsideBounds(hairSprite, hairEnvelope) /
                              (float)CountVisiblePixels(hairSprite);

        Assert.True(
            hairEnvelope.Contains(hairBounds!.Value.CenterX, hairBounds.Value.CenterY),
            $"Hair bbox center ({hairBounds.Value.CenterX:F1},{hairBounds.Value.CenterY:F1}) fell outside head envelope [{hairEnvelope.MinX},{hairEnvelope.MinY}]→[{hairEnvelope.MaxX},{hairEnvelope.MaxY}]");
        Assert.True(
            hairInsideRatio >= 0.70f,
            $"Hair coverage inside head envelope too low: {hairInsideRatio:F3}");
    }

    private static void AssertVisibleWeaponLayer(
        NifRenderableModel model,
        NifTextureResolver textureResolver)
    {
        var weaponSubmeshes = model.Submeshes.Where(submesh => submesh.RenderOrder == 6).ToList();

        Assert.NotEmpty(weaponSubmeshes);

        using var _ = new RendererStateScope();
        var weaponSprite = RenderSprite(CreateModel(weaponSubmeshes, model), textureResolver, 320);

        Assert.NotNull(weaponSprite);
        Assert.True(CountVisiblePixels(weaponSprite!) > 0);
    }

    private static void AssertHeavyWeaponBackpackAnchoring(NifRenderableModel model)
    {
        var bodySubmeshes = model.Submeshes.Where(submesh => submesh.RenderOrder != 6).ToList();
        var weaponSubmeshes = model.Submeshes.Where(submesh => submesh.RenderOrder == 6).ToList();

        Assert.NotEmpty(bodySubmeshes);
        Assert.NotEmpty(weaponSubmeshes);

        var (bodyMinX, bodyMaxX, bodyMinZ, bodyMaxZ) = ComputeBounds(bodySubmeshes);
        var (weaponMinX, weaponMaxX, weaponMinZ, weaponMaxZ) = ComputeBounds(weaponSubmeshes);

        var bodyWidth = bodyMaxX - bodyMinX;
        var bodyHeight = bodyMaxZ - bodyMinZ;
        var bodyCenterX = (bodyMinX + bodyMaxX) * 0.5f;
        var weaponCenterX = (weaponMinX + weaponMaxX) * 0.5f;
        var weaponCenterZ = (weaponMinZ + weaponMaxZ) * 0.5f;

        Assert.True(bodyWidth > 0f);
        Assert.True(bodyHeight > 0f);

        Assert.True(
            weaponMinZ >= bodyMinZ + bodyHeight * 0.38f,
            $"Heavy-weapon backpack sat too low: weapon z=[{weaponMinZ:F1},{weaponMaxZ:F1}], body z=[{bodyMinZ:F1},{bodyMaxZ:F1}]");
        Assert.True(
            weaponCenterZ >= bodyMinZ + bodyHeight * 0.56f,
            $"Heavy-weapon backpack center sat below upper torso: weapon center z={weaponCenterZ:F1}, body z=[{bodyMinZ:F1},{bodyMaxZ:F1}]");
        Assert.True(
            Math.Abs(weaponCenterX - bodyCenterX) <= bodyWidth * 0.35f,
            $"Heavy-weapon backpack drifted too far sideways: weapon center x={weaponCenterX:F1}, body center x={bodyCenterX:F1}, body width={bodyWidth:F1}");
    }

    private static void AssertOmittedWeaponLayer(NifRenderableModel model)
    {
        Assert.DoesNotContain(model.Submeshes, submesh => submesh.RenderOrder == 6);
    }

    private static void AssertArmorUsesExpandedInfluences(
        NifSkinningDiagnosticReport report,
        string editorId,
        string meshPath)
    {
        var principalShapes = report.Shapes
            .Where(shape => shape.VertexCount >= 200 && shape.BoneRefCount >= 4)
            .ToList();

        Assert.NotEmpty(principalShapes);

        Assert.All(
            principalShapes,
            shape =>
            {
                Assert.True(
                    shape.UsesNiSkinDataVertexWeights || shape.AllPartitionsExpanded,
                    $"Armor shape for {editorId} did not expose expanded influences: {DescribeSkinningShape(shape, meshPath)}");
                Assert.True(
                    shape.MaxInfluencesPerVertex >= 2,
                    $"Armor shape for {editorId} still looks like single-bone fallback skinning: {DescribeSkinningShape(shape, meshPath)}");
                Assert.True(
                    shape.VerticesWithMultipleInfluences > 0,
                    $"Armor shape for {editorId} had no multi-influence vertices: {DescribeSkinningShape(shape, meshPath)}");
            });
    }

    private static void AssertSpriteBoundsSimilar(
        SpriteResult? expectedSprite,
        SpriteResult? actualSprite,
        string editorId,
        string viewLabel,
        string meshPath)
    {
        Assert.NotNull(expectedSprite);
        Assert.NotNull(actualSprite);

        var expectedBounds = GetVisibleBounds(expectedSprite!);
        var actualBounds = GetVisibleBounds(actualSprite!);

        Assert.NotNull(expectedBounds);
        Assert.NotNull(actualBounds);

        var widthDelta = Math.Abs(actualBounds!.Value.Width - expectedBounds!.Value.Width);
        var heightDelta = Math.Abs(actualBounds.Value.Height - expectedBounds.Value.Height);
        var centerXDelta = Math.Abs(actualBounds.Value.CenterX - expectedBounds.Value.CenterX);
        var centerYDelta = Math.Abs(actualBounds.Value.CenterY - expectedBounds.Value.CenterY);

        Assert.True(
            widthDelta <= 18,
            $"{editorId} {viewLabel} width diverged for '{meshPath}': pc={expectedBounds.Value.Width}, xbox={actualBounds.Value.Width}");
        Assert.True(
            heightDelta <= 18,
            $"{editorId} {viewLabel} height diverged for '{meshPath}': pc={expectedBounds.Value.Height}, xbox={actualBounds.Value.Height}");
        Assert.True(
            centerXDelta <= 12f,
            $"{editorId} {viewLabel} center X diverged for '{meshPath}': pc={expectedBounds.Value.CenterX:F1}, xbox={actualBounds.Value.CenterX:F1}");
        Assert.True(
            centerYDelta <= 12f,
            $"{editorId} {viewLabel} center Y diverged for '{meshPath}': pc={expectedBounds.Value.CenterY:F1}, xbox={actualBounds.Value.CenterY:F1}");
    }

    private static SpriteBounds? GetVisibleBounds(SpriteResult sprite)
    {
        var minX = sprite.Width;
        var minY = sprite.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < sprite.Height; y++)
        {
            for (var x = 0; x < sprite.Width; x++)
            {
                if (ReadPixel(sprite, x, y).A == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < minX || maxY < minY
            ? null
            : new SpriteBounds(minX, minY, maxX, maxY);
    }

    private static SpriteBounds ExpandBounds(
        SpriteBounds bounds,
        int width,
        int height,
        float xPaddingFraction,
        float yPaddingFraction)
    {
        var paddingX = (int)MathF.Ceiling(bounds.Width * xPaddingFraction);
        var paddingY = (int)MathF.Ceiling(bounds.Height * yPaddingFraction);
        return new SpriteBounds(
            Math.Max(0, bounds.MinX - paddingX),
            Math.Max(0, bounds.MinY - paddingY),
            Math.Min(width - 1, bounds.MaxX + paddingX),
            Math.Min(height - 1, bounds.MaxY + paddingY));
    }

    private static int CountVisiblePixelsInsideBounds(SpriteResult sprite, SpriteBounds bounds)
    {
        var count = 0;
        for (var y = bounds.MinY; y <= bounds.MaxY; y++)
        {
            for (var x = bounds.MinX; x <= bounds.MaxX; x++)
            {
                if (ReadPixel(sprite, x, y).A > 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static NpcAppearance? ResolveNpcAppearance(
        NpcAppearanceResolver resolver,
        string pluginName,
        string fullName,
        string editorIdFragment)
    {
        var match = resolver.GetAllNpcs().FirstOrDefault(entry =>
            string.Equals(
                entry.Value.FullName,
                fullName,
                StringComparison.OrdinalIgnoreCase) ||
            (entry.Value.EditorId?.Contains(
                editorIdFragment,
                StringComparison.OrdinalIgnoreCase) ?? false));

        return match.Value == null
            ? null
            : resolver.ResolveHeadOnly(match.Key, pluginName);
    }

    private static NpcAppearance? ResolveNpcAppearanceByExactEditorId(
        NpcAppearanceResolver resolver,
        string pluginName,
        string editorId)
    {
        foreach (var (formId, entry) in resolver.GetAllNpcs())
        {
            if (string.Equals(entry.EditorId, editorId, StringComparison.OrdinalIgnoreCase))
            {
                return resolver.ResolveHeadOnly(formId, pluginName);
            }
        }

        return null;
    }

    private static string ResolvePrimaryTorsoArmorMeshPath(NpcAppearance npc)
    {
        Assert.NotNull(npc.EquippedItems);

        var torsoArmor = npc.EquippedItems!.FirstOrDefault(item =>
            !NpcRenderHelpers.IsHeadEquipment(item.BipedFlags) &&
            (item.BipedFlags & UpperBodySlot) != 0);
        Assert.NotNull(torsoArmor);
        Assert.False(
            string.IsNullOrWhiteSpace(torsoArmor!.MeshPath),
            $"NPC '{npc.EditorId ?? npc.FullName ?? npc.NpcFormId.ToString("X8", CultureInfo.InvariantCulture)}' did not resolve a torso armor mesh");
        return torsoArmor.MeshPath;
    }

    private static string DescribeSkinningShape(
        NifSkinnedShapeDiagnostic shape,
        string meshPath)
    {
        return
            $"mesh={meshPath}, shape={shape.ShapeName}, verts={shape.VertexCount}, bones={shape.BoneRefCount}, " +
            $"skinDataWeights={shape.UsesNiSkinDataVertexWeights}, expandedPartitions={shape.PartitionsWithExpandedData}/{shape.PartitionCount}, " +
            $"multiInfluenceVerts={shape.VerticesWithMultipleInfluences}, maxInfluences={shape.MaxInfluencesPerVertex}, overallTransform={shape.HasNonIdentityOverallTransform}";
    }

    private static NpcAppearance CloneAppearance(
        NpcAppearance source,
        WeaponVisual? weaponVisual = null)
    {
        return new NpcAppearance
        {
            NpcFormId = source.NpcFormId,
            EditorId = source.EditorId,
            FullName = source.FullName,
            IsFemale = source.IsFemale,
            BaseHeadNifPath = source.BaseHeadNifPath,
            BaseHeadTriPath = source.BaseHeadTriPath,
            HeadDiffuseOverride = source.HeadDiffuseOverride,
            FaceGenNifPath = source.FaceGenNifPath,
            FaceGenSymmetricCoeffs = source.FaceGenSymmetricCoeffs,
            FaceGenAsymmetricCoeffs = source.FaceGenAsymmetricCoeffs,
            FaceGenTextureCoeffs = source.FaceGenTextureCoeffs,
            HairNifPath = source.HairNifPath,
            HairTexturePath = source.HairTexturePath,
            LeftEyeNifPath = source.LeftEyeNifPath,
            RightEyeNifPath = source.RightEyeNifPath,
            EyeTexturePath = source.EyeTexturePath,
            HeadPartNifPaths = source.HeadPartNifPaths,
            HairColor = source.HairColor,
            EquippedItems = source.EquippedItems,
            WeaponVisual = weaponVisual ?? source.WeaponVisual,
            UpperBodyNifPath = source.UpperBodyNifPath,
            LeftHandNifPath = source.LeftHandNifPath,
            RightHandNifPath = source.RightHandNifPath,
            BodyTexturePath = source.BodyTexturePath,
            HandTexturePath = source.HandTexturePath,
            SkeletonNifPath = source.SkeletonNifPath,
            BodyEgtPath = source.BodyEgtPath,
            LeftHandEgtPath = source.LeftHandEgtPath,
            RightHandEgtPath = source.RightHandEgtPath
        };
    }

    private static bool IsHairLikeSubmesh(RenderableSubmesh submesh)
    {
        if (submesh.TintColor.HasValue || submesh.RenderOrder > 0)
        {
            return true;
        }

        return ContainsHairHint(submesh.ShapeName) || ContainsHairHint(submesh.DiffuseTexturePath);

        static bool ContainsHairHint(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("brow", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("lash", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("beard", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsBaseFaceSubmesh(RenderableSubmesh submesh)
    {
        return submesh.RenderOrder == 0 && !IsHairLikeSubmesh(submesh);
    }

    private static bool IsAnchoredAttachmentSubmesh(RenderableSubmesh submesh)
    {
        if (IsEyeSubmesh(submesh) || IsBaseFaceSubmesh(submesh))
        {
            return false;
        }

        return submesh.RenderOrder is 1 or 3 ||
               ContainsHint(submesh.ShapeName, "hair") ||
               ContainsHint(submesh.ShapeName, "brow") ||
               ContainsHint(submesh.ShapeName, "lash") ||
               ContainsHint(submesh.ShapeName, "beard") ||
               ContainsHint(submesh.ShapeName, "mustache") ||
               ContainsHint(submesh.ShapeName, "moustache") ||
               ContainsHint(submesh.ShapeName, "hood") ||
               ContainsHint(submesh.ShapeName, "hat") ||
               ContainsHint(submesh.ShapeName, "beret") ||
               ContainsHint(submesh.ShapeName, "shade") ||
               ContainsHint(submesh.ShapeName, "glass") ||
               ContainsHint(submesh.DiffuseTexturePath, "hair") ||
               ContainsHint(submesh.DiffuseTexturePath, "brow") ||
               ContainsHint(submesh.DiffuseTexturePath, "lash") ||
               ContainsHint(submesh.DiffuseTexturePath, "beard") ||
               ContainsHint(submesh.DiffuseTexturePath, "mustache") ||
               ContainsHint(submesh.DiffuseTexturePath, "moustache") ||
               ContainsHint(submesh.DiffuseTexturePath, "hood") ||
               ContainsHint(submesh.DiffuseTexturePath, "hat") ||
               ContainsHint(submesh.DiffuseTexturePath, "beret") ||
               ContainsHint(submesh.DiffuseTexturePath, "shade") ||
               ContainsHint(submesh.DiffuseTexturePath, "glass");
    }

    private static bool IsEyeSubmesh(RenderableSubmesh submesh)
    {
        return submesh.RenderOrder == 2 ||
               submesh.IsEyeEnvmap ||
               HasExplicitEyeHint(submesh.ShapeName) ||
               HasExplicitEyeHint(submesh.DiffuseTexturePath);
    }

    private static bool ContainsHint(string? value, string hint)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Contains(hint, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExplicitEyeHint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return (value.Contains("eyeleft", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("eyeright", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("\\eyes\\", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("/eyes/", StringComparison.OrdinalIgnoreCase)) &&
               !value.Contains("eyebrow", StringComparison.OrdinalIgnoreCase);
    }

    private static HairCompositionEvidence AnalyzeHairComposition(
        SpriteResult fullSprite,
        SpriteResult faceSprite,
        SpriteResult hairSprite)
    {
        var translucentCandidateCount = 0;
        var translucentBlendCount = 0;
        var opaqueHairCandidateCount = 0;
        var opaqueHairDominanceCount = 0;

        for (var y = 0; y < fullSprite.Height; y++)
        {
            var normY = fullSprite.Height <= 1 ? 0f : y / (float)(fullSprite.Height - 1);
            for (var x = 0; x < fullSprite.Width; x++)
            {
                var normX = fullSprite.Width <= 1 ? 0f : x / (float)(fullSprite.Width - 1);
                var fullPixel = ReadPixel(fullSprite, x, y);
                var facePixel = ReadNormalizedPixel(faceSprite, normX, normY);
                var hairPixel = ReadNormalizedPixel(hairSprite, normX, normY);

                if (facePixel.A > 0 && hairPixel.A >= 32 && hairPixel.A <= 223 && fullPixel.A > 0)
                {
                    translucentCandidateCount++;
                    if (ColorDistance(fullPixel, facePixel) > 24 &&
                        ColorDistance(fullPixel, hairPixel) > 24)
                    {
                        translucentBlendCount++;
                    }
                }

                if (facePixel.A > 0 && hairPixel.A >= 224 && fullPixel.A > 0)
                {
                    opaqueHairCandidateCount++;
                    if (ColorDistance(fullPixel, facePixel) > 24)
                    {
                        opaqueHairDominanceCount++;
                    }
                }
            }
        }

        return new HairCompositionEvidence(
            translucentCandidateCount,
            translucentBlendCount,
            opaqueHairCandidateCount,
            opaqueHairDominanceCount);
    }

    private static (byte R, byte G, byte B, byte A) ReadNormalizedPixel(
        SpriteResult sprite,
        float normX,
        float normY)
    {
        var x = sprite.Width <= 1 ? 0 : (int)Math.Round(normX * (sprite.Width - 1));
        var y = sprite.Height <= 1 ? 0 : (int)Math.Round(normY * (sprite.Height - 1));
        return ReadPixel(sprite, x, y);
    }

    private static (byte R, byte G, byte B, byte A) ReadPixel(SpriteResult sprite, int x, int y)
    {
        x = Math.Clamp(x, 0, sprite.Width - 1);
        y = Math.Clamp(y, 0, sprite.Height - 1);
        var offset = (y * sprite.Width + x) * 4;
        return (
            sprite.Pixels[offset],
            sprite.Pixels[offset + 1],
            sprite.Pixels[offset + 2],
            sprite.Pixels[offset + 3]);
    }

    private static int ColorDistance(
        (byte R, byte G, byte B, byte A) left,
        (byte R, byte G, byte B, byte A) right)
    {
        return Math.Abs(left.R - right.R) +
               Math.Abs(left.G - right.G) +
               Math.Abs(left.B - right.B);
    }

    private static (float MinX, float MaxX, float MinZ, float MaxZ) ComputeBounds(
        IEnumerable<RenderableSubmesh> submeshes)
    {
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minZ = float.MaxValue;
        var maxZ = float.MinValue;

        foreach (var submesh in submeshes)
        {
            for (var i = 0; i < submesh.Positions.Length; i += 3)
            {
                var x = submesh.Positions[i];
                var z = submesh.Positions[i + 2];
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
        }

        return (minX, maxX, minZ, maxZ);
    }

    private sealed record HairCompositionEvidence(
        int TranslucentCandidateCount,
        int TranslucentBlendCount,
        int OpaqueHairCandidateCount,
        int OpaqueHairDominanceCount);

    private readonly record struct SpriteBounds(int MinX, int MinY, int MaxX, int MaxY)
    {
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
        public float CenterX => (MinX + MaxX) * 0.5f;
        public float CenterY => (MinY + MaxY) * 0.5f;

        public bool Contains(float x, float y)
        {
            return x >= MinX && x <= MaxX &&
                   y >= MinY && y <= MaxY;
        }
    }

    private sealed class RendererStateScope : IDisposable
    {
        private readonly float _bumpStrength;
        private readonly bool _disableBilinear;
        private readonly bool _disableBumpMapping;
        private readonly bool _disableTextures;

        public RendererStateScope()
        {
            _disableBilinear = NifSpriteRenderer.DisableBilinear;
            _disableBumpMapping = NifSpriteRenderer.DisableBumpMapping;
            _disableTextures = NifSpriteRenderer.DisableTextures;
            _bumpStrength = NifSpriteRenderer.BumpStrength;

            NifSpriteRenderer.DisableBilinear = true;
            NifSpriteRenderer.DisableBumpMapping = false;
            NifSpriteRenderer.DisableTextures = false;
            NifSpriteRenderer.BumpStrength = 0.5f;
        }

        public void Dispose()
        {
            NifSpriteRenderer.DisableBilinear = _disableBilinear;
            NifSpriteRenderer.DisableBumpMapping = _disableBumpMapping;
            NifSpriteRenderer.DisableTextures = _disableTextures;
            NifSpriteRenderer.BumpStrength = _bumpStrength;
        }
    }

    private sealed record NpcRenderAssets(
        string EsmPath,
        string MeshesBsaPath,
        NpcMeshArchiveSet MeshArchives,
        NifTextureResolver TextureResolver,
        NpcAppearanceResolver AppearanceResolver) : IDisposable
    {
        public void Dispose()
        {
            MeshArchives.Dispose();
            TextureResolver.Dispose();
        }
    }
}
