using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

[Collection(LoggerSerialTestGroup.Name)]
public sealed class XboxNpcRenderRegressionTests(SampleFileFixture samples)
{
    private const uint BooneFormId = 0x00092BD2;
    private const string DocMitchellEditorId = "DocMitchell";
    private const string LucyEditorId = "VMS38RedLucy";
    private const string VeronicaEditorId = "VeronicaSantiago";
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
            fullName: "Red Lucy",
            editorIdFragment: LucyEditorId);

        Assert.NotNull(lucy);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, System.Numerics.Matrix4x4>? skeletonBoneCache = null;
        Dictionary<string, System.Numerics.Matrix4x4>? poseDeltaCache = null;

        var model = NpcBodyBuilder.Build(
            lucy!,
            assets.MeshesArchive,
            assets.MeshExtractor,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            ref skeletonBoneCache,
            ref poseDeltaCache,
            CreateSettings(headOnly: false));

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
        var flatSprite = RenderSprite(outfitModel, textureResolver: null);

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
            assets.MeshesArchive,
            assets.MeshExtractor,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(headOnly: true));

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
            fullName: "Red Lucy",
            editorIdFragment: LucyEditorId);

        Assert.NotNull(lucy);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var model = NpcHeadBuilder.Build(
            lucy!,
            assets.MeshesArchive,
            assets.MeshExtractor,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(headOnly: true));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);
        AssertAttachmentAnchoring(model, assets.TextureResolver);
    }

    [Fact]
    public void BuildRedLucyHeadOnly_Xbox360EyesAimTowardFrontCamera()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var lucy = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            fullName: "Red Lucy",
            editorIdFragment: LucyEditorId);

        Assert.NotNull(lucy);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var model = NpcHeadBuilder.Build(
            lucy!,
            assets.MeshesArchive,
            assets.MeshExtractor,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(headOnly: true));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);

        AssertEyesAimTowardCamera(model, new System.Numerics.Vector3(0f, 1f, 0f));
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
            fullName: "Red Lucy",
            editorIdFragment: LucyEditorId);

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
        var withoutEquip = BuildHeadOnlyModel(assets, boone, noEquip: true);

        Assert.Contains(withEquip.Submeshes, submesh => submesh.RenderOrder == 3);
        Assert.DoesNotContain(withoutEquip.Submeshes, submesh => submesh.RenderOrder == 3);
    }

    [Fact]
    public void BuildRedLucyFullBodyHead_Xbox360EyesAimTowardFrontCamera()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var lucy = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            fullName: "Red Lucy",
            editorIdFragment: LucyEditorId);

        Assert.NotNull(lucy);

        var model = BuildFullBodyHeadModel(assets, lucy!);
        AssertEyesAimTowardCamera(model, new System.Numerics.Vector3(0f, 1f, 0f));
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
            fullName: "Doc Mitchell",
            editorIdFragment: DocMitchellEditorId);

        Assert.NotNull(docMitchell);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var model = NpcHeadBuilder.Build(
            docMitchell!,
            assets.MeshesArchive,
            assets.MeshExtractor,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(headOnly: true));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);
        AssertAttachmentAnchoring(model, assets.TextureResolver);
        AssertEyesAimTowardCamera(model, new System.Numerics.Vector3(0f, 1f, 0f));
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
            fullName: "Veronica",
            editorIdFragment: VeronicaEditorId);

        Assert.NotNull(veronica);

        var model = BuildHeadOnlyModel(assets, veronica!);
        AssertAttachmentAnchoring(model, assets.TextureResolver);
    }

    private XboxAssets CreateXboxAssets()
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

        return new XboxAssets(
            meshesBsa!,
            BsaParser.Parse(meshesBsa!),
            new BsaExtractor(meshesBsa!),
            new NifTextureResolver(texturePaths),
            NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian));
    }

    private NpcRenderSettings CreateSettings(bool headOnly, bool noEquip = false)
    {
        return new NpcRenderSettings
        {
            MeshesBsaPath = "unused",
            EsmPath = samples.Xbox360FinalEsm!,
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
        int fixedSize = 160)
    {
        return NifSpriteRenderer.Render(
            model,
            textureResolver,
            pixelsPerUnit: 1f,
            minSize: 64,
            maxSize: 192,
            azimuthDeg: 90f,
            elevationDeg: 0f,
            fixedSize: fixedSize);
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

    private NifRenderableModel BuildHeadOnlyModel(
        XboxAssets assets,
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
            assets.MeshesArchive,
            assets.MeshExtractor,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(headOnly: true, noEquip: noEquip));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);
        return model;
    }

    private NifRenderableModel BuildFullBodyHeadModel(XboxAssets assets, NpcAppearance npc)
    {
        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, System.Numerics.Matrix4x4>? skeletonBoneCache = null;
        Dictionary<string, System.Numerics.Matrix4x4>? poseDeltaCache = null;

        var bodyModel = NpcBodyBuilder.Build(
            npc,
            assets.MeshesArchive,
            assets.MeshExtractor,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            ref skeletonBoneCache,
            ref poseDeltaCache,
            CreateSettings(headOnly: false));

        Assert.NotNull(bodyModel);
        Assert.NotNull(skeletonBoneCache);

        var bonelessHeadAttachmentTransform = NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(
            skeletonBoneCache,
            poseDeltaCache);

        var fullBodyHeadModel = NpcHeadBuilder.Build(
            npc,
            assets.MeshesArchive,
            assets.MeshExtractor,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(headOnly: false),
            idlePoseBones: skeletonBoneCache,
            headEquipmentTransformOverride: bonelessHeadAttachmentTransform);

        Assert.NotNull(fullBodyHeadModel);
        Assert.True(fullBodyHeadModel.HasGeometry);
        return fullBodyHeadModel;
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
        var faceSprite = RenderSprite(CreateModel(faceSubmeshes, model), textureResolver, fixedSize: 320);
        var attachmentSprite = RenderSprite(CreateModel(attachmentSubmeshes, model), textureResolver, fixedSize: 320);
        var eyeSprite = RenderSprite(CreateModel(eyeSubmeshes, model), textureResolver, fixedSize: 320);

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
        var attachmentEnvelope = ExpandBounds(faceBounds.Value, attachmentSprite!.Width, attachmentSprite.Height, 0.55f, 0.45f);

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
        var match = resolver.GetAllNpcs().FirstOrDefault(
            entry =>
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

    private static bool IsPrimaryHairSubmesh(RenderableSubmesh submesh)
    {
        return submesh.RenderOrder == 1 || ContainsHint(submesh.ShapeName, "hair");
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

    private static void AssertEyesAimTowardCamera(
        NifRenderableModel model,
        System.Numerics.Vector3 desiredForward)
    {
        var azimuth = MathF.Atan2(desiredForward.Y, desiredForward.X) * 180f / MathF.PI;
        var elevation = MathF.Asin(Math.Clamp(desiredForward.Z, -1f, 1f)) * 180f / MathF.PI;
        if (NpcEyeLookAt.HasEyeSubmeshes(model))
        {
            NpcEyeLookAt.Apply(model, azimuth, elevation);
        }

        var eyeSubmeshes = model.Submeshes.Where(IsEyeSubmesh).ToList();
        Assert.NotEmpty(eyeSubmeshes);

        foreach (var submesh in eyeSubmeshes)
        {
            Assert.True(NpcEyeLookAt.TryEstimateLookDirection(submesh, out _, out var lookDirection));
            Assert.True(
                System.Numerics.Vector3.Dot(lookDirection, desiredForward) >= 0.92f,
                $"Eye submesh '{submesh.ShapeName ?? "(unnamed)"}' look direction was {lookDirection}");
        }
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
        private readonly bool _disableBilinear;
        private readonly bool _disableBumpMapping;
        private readonly bool _disableTextures;
        private readonly float _bumpStrength;

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

    private sealed record XboxAssets(
        string MeshesBsaPath,
        BsaArchive MeshesArchive,
        BsaExtractor MeshExtractor,
        NifTextureResolver TextureResolver,
        NpcAppearanceResolver AppearanceResolver) : IDisposable
    {
        public void Dispose()
        {
            MeshExtractor.Dispose();
            TextureResolver.Dispose();
        }
    }
}
