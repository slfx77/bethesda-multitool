using System.Text;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

[Collection(LoggerSerialTestGroup.Name)]
public sealed class XboxNpcEgtRenderedComparisonTests(SampleFileFixture samples)
{
    private const uint CrockerFormId = 0x00112640;
    private const uint JeanBaptisteFormId = 0x0010C681;

    [Fact]
    public void ExportCrockerHeadComparison_WritesShippedAndGeneratedHeadRenders()
    {
        ExportHeadComparison(
            CrockerFormId,
            "egt-head-render-compare-crocker");
    }

    [Fact]
    public void ExportJeanBaptisteHeadComparison_WritesShippedAndGeneratedHeadRenders()
    {
        ExportHeadComparison(
            JeanBaptisteFormId,
            "egt-head-render-compare-jean-baptiste");
    }

    private void ExportHeadComparison(
        uint formId,
        string artifactRootName)
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

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

        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var texturePaths = textures2Bsa == null
            ? [texturesBsa!]
            : new[] { texturesBsa!, textures2Bsa! };
        var appearanceResolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var appearance = appearanceResolver.ResolveHeadOnly(formId, pluginName);

        Assert.NotNull(appearance);

        var shippedTextures = NpcFaceGenTextureVerifier.DiscoverShippedFaceTextures(
            texturePaths,
            pluginName);
        Assert.True(shippedTextures.TryGetValue(formId, out var shippedTexture));
        Assert.NotNull(shippedTexture);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);
        using var textureResolver = new NifTextureResolver(texturePaths);
        var detail = NpcEgtBlendInvestigator.InvestigateDetailed(
            appearance!,
            shippedTexture!,
            meshArchives,
            textureResolver,
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase));

        Assert.True(detail.Result.Verified, detail.Result.FailureReason);
        Assert.NotNull(detail.ShippedTexture);
        Assert.NotNull(detail.RecoveredPolicy);

        var baseTexturePath = NifTexturePathUtility.Normalize(appearance.HeadDiffuseOverride!);
        var baseTexture = textureResolver.GetTexture(baseTexturePath);
        Assert.NotNull(baseTexture);

        var shippedAppliedDiffuse = FaceGenTextureMorpher.ApplyEncodedDeltaTexture(baseTexture!, detail.ShippedTexture!);
        Assert.NotNull(shippedAppliedDiffuse);

        var neutralModel = BuildNeutralHeadModel(appearance!, meshArchives, textureResolver);
        var artifactDir = Path.Combine(
            GetRepoRoot(),
            "artifacts",
            artifactRootName,
            $"{appearance.NpcFormId:X8}_{NpcExportFileNaming.SanitizeStem(appearance.EditorId) ?? "npc"}");

        if (Directory.Exists(artifactDir))
        {
            Directory.Delete(artifactDir, true);
        }

        Directory.CreateDirectory(artifactDir);
        SaveTexture(
            detail.ShippedTexture!,
            Path.Combine(artifactDir, "shipped_prebaked_egt.png"));
        SaveTexture(
            detail.RecoveredPolicy.GeneratedEgtTexture,
            Path.Combine(artifactDir, "generated_recovered_egt.png"));
        SaveTexture(
            shippedAppliedDiffuse!,
            Path.Combine(artifactDir, "shipped_prebaked_applied_diffuse.png"));
        SaveTexture(
            detail.RecoveredPolicy!.AppliedDiffuseTexture,
            Path.Combine(artifactDir, "generated_recovered_applied_diffuse.png"));

        using var _ = new RendererStateScope();

        var shippedFront = RenderHeadVariant(
            neutralModel,
            textureResolver,
            baseTexturePath,
            $"egt_compare\\{appearance.NpcFormId:X8}_shipped_prebaked.dds",
            shippedAppliedDiffuse!,
            azimuth: 90f,
            elevation: 0f);
        var generatedFront = RenderHeadVariant(
            neutralModel,
            textureResolver,
            baseTexturePath,
            $"egt_compare\\{appearance.NpcFormId:X8}_generated_recovered.dds",
            detail.RecoveredPolicy.AppliedDiffuseTexture,
            azimuth: 90f,
            elevation: 0f);
        var shippedLeft = RenderHeadVariant(
            neutralModel,
            textureResolver,
            baseTexturePath,
            $"egt_compare\\{appearance.NpcFormId:X8}_shipped_prebaked.dds",
            shippedAppliedDiffuse!,
            azimuth: 180f,
            elevation: 0f);
        var generatedLeft = RenderHeadVariant(
            neutralModel,
            textureResolver,
            baseTexturePath,
            $"egt_compare\\{appearance.NpcFormId:X8}_generated_recovered.dds",
            detail.RecoveredPolicy.AppliedDiffuseTexture,
            azimuth: 180f,
            elevation: 0f);

        Assert.NotNull(shippedFront);
        Assert.NotNull(generatedFront);
        Assert.NotNull(shippedLeft);
        Assert.NotNull(generatedLeft);

        SaveSprite(shippedFront!, Path.Combine(artifactDir, "shipped_prebaked_front.png"));
        SaveSprite(generatedFront!, Path.Combine(artifactDir, "generated_recovered_front.png"));
        SaveSprite(shippedLeft!, Path.Combine(artifactDir, "shipped_prebaked_left.png"));
        SaveSprite(generatedLeft!, Path.Combine(artifactDir, "generated_recovered_left.png"));

        var frontDiff = NpcTextureComparison.BuildDiffTexture(
            DecodedTexture.FromBaseLevel(shippedFront.Pixels, shippedFront.Width, shippedFront.Height),
            DecodedTexture.FromBaseLevel(generatedFront.Pixels, generatedFront.Width, generatedFront.Height));
        var leftDiff = NpcTextureComparison.BuildDiffTexture(
            DecodedTexture.FromBaseLevel(shippedLeft.Pixels, shippedLeft.Width, shippedLeft.Height),
            DecodedTexture.FromBaseLevel(generatedLeft.Pixels, generatedLeft.Width, generatedLeft.Height));
        var egtDiff = NpcTextureComparison.BuildDiffTexture(
            detail.ShippedTexture!,
            detail.RecoveredPolicy.GeneratedEgtTexture);
        var egtBias = NpcTextureComparison.BuildSignedBiasTexture(
            detail.ShippedTexture!,
            detail.RecoveredPolicy.GeneratedEgtTexture);
        var appliedBias = NpcTextureComparison.BuildSignedBiasTexture(
            shippedAppliedDiffuse,
            detail.RecoveredPolicy.AppliedDiffuseTexture);
        var frontBias = NpcTextureComparison.BuildSignedBiasTexture(
            DecodedTexture.FromBaseLevel(shippedFront.Pixels, shippedFront.Width, shippedFront.Height),
            DecodedTexture.FromBaseLevel(generatedFront.Pixels, generatedFront.Width, generatedFront.Height));
        var leftBias = NpcTextureComparison.BuildSignedBiasTexture(
            DecodedTexture.FromBaseLevel(shippedLeft.Pixels, shippedLeft.Width, shippedLeft.Height),
            DecodedTexture.FromBaseLevel(generatedLeft.Pixels, generatedLeft.Width, generatedLeft.Height));

        SaveTexture(egtDiff, Path.Combine(artifactDir, "egt_diff.png"));
        SaveTexture(egtBias, Path.Combine(artifactDir, "egt_signed_bias.png"));
        SaveTexture(frontDiff, Path.Combine(artifactDir, "front_diff.png"));
        SaveTexture(leftDiff, Path.Combine(artifactDir, "left_diff.png"));
        SaveTexture(appliedBias, Path.Combine(artifactDir, "applied_diffuse_signed_bias.png"));
        SaveTexture(frontBias, Path.Combine(artifactDir, "front_signed_bias.png"));
        SaveTexture(leftBias, Path.Combine(artifactDir, "left_signed_bias.png"));

        var egtMetrics = NpcTextureComparison.CompareRgb(
            detail.ShippedTexture!.Pixels,
            detail.RecoveredPolicy.GeneratedEgtTexture.Pixels,
            detail.ShippedTexture.Width,
            detail.ShippedTexture.Height);
        var appliedMetrics = NpcTextureComparison.CompareRgb(
            shippedAppliedDiffuse.Pixels,
            detail.RecoveredPolicy.AppliedDiffuseTexture.Pixels,
            shippedAppliedDiffuse.Width,
            shippedAppliedDiffuse.Height);
        var frontMetrics = NpcTextureComparison.CompareRgb(
            shippedFront.Pixels,
            generatedFront.Pixels,
            shippedFront.Width,
            shippedFront.Height);
        var leftMetrics = NpcTextureComparison.CompareRgb(
            shippedLeft.Pixels,
            generatedLeft.Pixels,
            shippedLeft.Width,
            shippedLeft.Height);
        var egtSignedMetrics = NpcTextureComparison.CompareSignedRgb(
            detail.ShippedTexture.Pixels,
            detail.RecoveredPolicy.GeneratedEgtTexture.Pixels,
            detail.ShippedTexture.Width,
            detail.ShippedTexture.Height);
        var appliedSignedMetrics = NpcTextureComparison.CompareSignedRgb(
            shippedAppliedDiffuse.Pixels,
            detail.RecoveredPolicy.AppliedDiffuseTexture.Pixels,
            shippedAppliedDiffuse.Width,
            shippedAppliedDiffuse.Height);
        var frontSignedMetrics = NpcTextureComparison.CompareSignedRgb(
            shippedFront.Pixels,
            generatedFront.Pixels,
            shippedFront.Width,
            shippedFront.Height);
        var leftSignedMetrics = NpcTextureComparison.CompareSignedRgb(
            shippedLeft.Pixels,
            generatedLeft.Pixels,
            shippedLeft.Width,
            shippedLeft.Height);

        File.WriteAllText(
            Path.Combine(artifactDir, "metadata.txt"),
            BuildMetadata(
                detail,
                egtMetrics,
                appliedMetrics,
                frontMetrics,
                leftMetrics,
                egtSignedMetrics,
                appliedSignedMetrics,
                frontSignedMetrics,
                leftSignedMetrics),
            Encoding.UTF8);

        Assert.True(File.Exists(Path.Combine(artifactDir, "shipped_prebaked_front.png")));
        Assert.True(File.Exists(Path.Combine(artifactDir, "generated_recovered_front.png")));
        Assert.True(File.Exists(Path.Combine(artifactDir, "front_diff.png")));
    }

    private NifRenderableModel BuildNeutralHeadModel(
        NpcAppearance appearance,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver)
    {
        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var model = NpcHeadBuilder.Build(
            appearance,
            meshArchives,
            textureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            new NpcRenderSettings
            {
                MeshesBsaPath = "unused",
                EsmPath = samples.Xbox360FinalEsm!,
                OutputDir = Path.GetTempPath(),
                HeadOnly = true,
                NoEquip = true,
                ForceCpu = true,
                NoEgt = true,
                NoEgm = false
            });

        Assert.NotNull(model);
        Assert.True(model!.HasGeometry);
        return model;
    }

    private static SpriteResult? RenderHeadVariant(
        NifRenderableModel neutralModel,
        NifTextureResolver textureResolver,
        string baseTexturePath,
        string injectedTextureKey,
        DecodedTexture appliedDiffuse,
        float azimuth,
        float elevation)
    {
        var model = NpcRenderHelpers.DeepCloneModel(neutralModel);
        textureResolver.InjectTexture(injectedTextureKey, appliedDiffuse);
        try
        {
            OverrideFaceTexture(model, baseTexturePath, injectedTextureKey);
            return NifSpriteRenderer.Render(
                model,
                textureResolver,
                1.0f,
                32,
                1024,
                azimuth,
                elevation,
                1024);
        }
        finally
        {
            textureResolver.EvictTexture(injectedTextureKey);
        }
    }

    private static void OverrideFaceTexture(
        NifRenderableModel model,
        string baseTexturePath,
        string injectedTextureKey)
    {
        foreach (var submesh in model.Submeshes)
        {
            if (submesh.DiffuseTexturePath == null)
            {
                continue;
            }

            if (string.Equals(
                    NifTexturePathUtility.Normalize(submesh.DiffuseTexturePath),
                    baseTexturePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                submesh.DiffuseTexturePath = injectedTextureKey;
            }
        }
    }

    private static void SaveSprite(SpriteResult sprite, string path)
    {
        PngWriter.SaveRgba(sprite.Pixels, sprite.Width, sprite.Height, path);
    }

    private static void SaveTexture(DecodedTexture texture, string path)
    {
        PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height, path);
    }

    private static string BuildMetadata(
        NpcEgtBlendInvestigationDetail detail,
        NpcTextureComparison.RgbComparisonMetrics egtMetrics,
        NpcTextureComparison.RgbComparisonMetrics appliedMetrics,
        NpcTextureComparison.RgbComparisonMetrics frontMetrics,
        NpcTextureComparison.RgbComparisonMetrics leftMetrics,
        NpcTextureComparison.SignedRgbComparisonMetrics egtSignedMetrics,
        NpcTextureComparison.SignedRgbComparisonMetrics appliedSignedMetrics,
        NpcTextureComparison.SignedRgbComparisonMetrics frontSignedMetrics,
        NpcTextureComparison.SignedRgbComparisonMetrics leftSignedMetrics)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"form_id=0x{detail.Result.FormId:X8}");
        sb.AppendLine($"editor_id={detail.Result.EditorId ?? string.Empty}");
        sb.AppendLine($"full_name={detail.Result.FullName ?? string.Empty}");
        sb.AppendLine($"shipped_texture={detail.Result.ShippedTexturePath}");
        sb.AppendLine($"base_texture={detail.Result.BaseTexturePath ?? string.Empty}");
        sb.AppendLine($"egt_path={detail.Result.EgtPath ?? string.Empty}");
        sb.AppendLine($"generated_policy={detail.RecoveredPolicy?.Result.Description ?? string.Empty}");
        sb.AppendLine($"generated_coefficients={detail.RecoveredPolicy?.Result.CoefficientPolicy ?? string.Empty}");
        sb.AppendLine($"generated_accumulation={detail.RecoveredPolicy?.Result.AccumulationMode ?? string.Empty}");
        AppendMetrics(sb, "egt", egtMetrics, egtSignedMetrics);
        AppendMetrics(sb, "applied_diffuse", appliedMetrics, appliedSignedMetrics);
        AppendMetrics(sb, "front_render", frontMetrics, frontSignedMetrics);
        AppendMetrics(sb, "left_render", leftMetrics, leftSignedMetrics);
        return sb.ToString();
    }

    private static void AppendMetrics(
        StringBuilder sb,
        string label,
        NpcTextureComparison.RgbComparisonMetrics metrics,
        NpcTextureComparison.SignedRgbComparisonMetrics signedMetrics)
    {
        sb.AppendLine($"{label}_mae_rgb={metrics.MeanAbsoluteRgbError:F6}");
        sb.AppendLine($"{label}_rmse_rgb={metrics.RootMeanSquareRgbError:F6}");
        sb.AppendLine($"{label}_max_abs_rgb={metrics.MaxAbsoluteRgbError}");
        sb.AppendLine($"{label}_pixels_gt4={metrics.PixelsWithRgbErrorAbove4}");
        sb.AppendLine($"{label}_mean_signed_r={signedMetrics.MeanSignedRedError:F6}");
        sb.AppendLine($"{label}_mean_signed_g={signedMetrics.MeanSignedGreenError:F6}");
        sb.AppendLine($"{label}_mean_signed_b={signedMetrics.MeanSignedBlueError:F6}");
        sb.AppendLine($"{label}_mae_r={signedMetrics.MeanAbsoluteRedError:F6}");
        sb.AppendLine($"{label}_mae_g={signedMetrics.MeanAbsoluteGreenError:F6}");
        sb.AppendLine($"{label}_mae_b={signedMetrics.MeanAbsoluteBlueError:F6}");
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "FalloutXbox360Utils.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return Directory.GetCurrentDirectory();
    }

    private sealed class RendererStateScope : IDisposable
    {
        private readonly bool _disableBilinear;
        private readonly bool _disableBumpMapping;
        private readonly bool _disableTextures;
        private readonly bool _drawWireframeOverlay;
        private readonly float _bumpStrength;

        public RendererStateScope()
        {
            _disableBilinear = NifSpriteRenderer.DisableBilinear;
            _disableBumpMapping = NifSpriteRenderer.DisableBumpMapping;
            _disableTextures = NifSpriteRenderer.DisableTextures;
            _drawWireframeOverlay = NifSpriteRenderer.DrawWireframeOverlay;
            _bumpStrength = NifSpriteRenderer.BumpStrength;

            NifSpriteRenderer.DisableBilinear = false;
            NifSpriteRenderer.DisableBumpMapping = false;
            NifSpriteRenderer.DisableTextures = false;
            NifSpriteRenderer.DrawWireframeOverlay = false;
            NifSpriteRenderer.BumpStrength = 0.5f;
        }

        public void Dispose()
        {
            NifSpriteRenderer.DisableBilinear = _disableBilinear;
            NifSpriteRenderer.DisableBumpMapping = _disableBumpMapping;
            NifSpriteRenderer.DisableTextures = _disableTextures;
            NifSpriteRenderer.DrawWireframeOverlay = _drawWireframeOverlay;
            NifSpriteRenderer.BumpStrength = _bumpStrength;
        }
    }
}
