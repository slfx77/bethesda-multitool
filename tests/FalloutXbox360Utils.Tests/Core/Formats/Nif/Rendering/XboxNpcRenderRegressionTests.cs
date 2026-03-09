using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

[Collection(LoggerSerialTestGroup.Name)]
public sealed class XboxNpcRenderRegressionTests(SampleFileFixture samples)
{
    private const uint BooneFormId = 0x00092BD2;
    private const string LucyEditorId = "VMS38RedLucy";
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

    private NpcRenderSettings CreateSettings(bool headOnly)
    {
        return new NpcRenderSettings
        {
            MeshesBsaPath = "unused",
            EsmPath = samples.Xbox360FinalEsm!,
            OutputDir = Path.GetTempPath(),
            HeadOnly = headOnly,
            ForceCpu = true,
            NoEgt = false,
            NoEgm = false
        };
    }

    private static SpriteResult? RenderSprite(NifRenderableModel model, NifTextureResolver? textureResolver)
    {
        return NifSpriteRenderer.Render(
            model,
            textureResolver,
            pixelsPerUnit: 1f,
            minSize: 64,
            maxSize: 192,
            azimuthDeg: 90f,
            elevationDeg: 0f,
            fixedSize: 160);
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
