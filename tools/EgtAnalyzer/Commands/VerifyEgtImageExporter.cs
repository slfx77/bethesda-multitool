using System.Globalization;
using System.Text;
using EgtAnalyzer.Verification;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

namespace EgtAnalyzer.Commands;

/// <summary>
///     PNG comparison-image export for <c>verify-egt</c>: writes generated/shipped/diff images,
///     affine-fit and DXT1-roundtrip variants, plus a per-NPC metadata sidecar.
/// </summary>
internal static class VerifyEgtImageExporter
{
    internal static void ExportComparisonImages(
        string rootDir,
        NpcAppearance appearance,
        NpcFaceGenTextureVerificationDetail detail)
    {
        var result = detail.Result;
        var generatedTexture = detail.GeneratedTexture!;
        var shippedTexture = detail.ShippedTexture!;
        var npcDir = Path.Combine(rootDir, BuildImageDirectoryName(appearance));
        if (Directory.Exists(npcDir))
        {
            Directory.Delete(npcDir, true);
        }

        Directory.CreateDirectory(npcDir);

        PngWriter.SaveRgba(
            generatedTexture.Pixels,
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "generated_egt.png"));
        PngWriter.SaveRgba(
            shippedTexture.Pixels,
            shippedTexture.Width,
            shippedTexture.Height,
            Path.Combine(npcDir, "shipped_egt.png"));
        PngWriter.SaveRgba(
            NpcTextureComparison.BuildDiffPixels(generatedTexture.Pixels, shippedTexture.Pixels),
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "diff_egt.png"));
        PngWriter.SaveRgba(
            NpcTextureComparison.BuildAmplifiedDiffPixels(generatedTexture.Pixels, shippedTexture.Pixels),
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "diff_egt_10x.png"));
        PngWriter.SaveRgba(
            NpcTextureComparison.BuildSignedBiasPixels(generatedTexture.Pixels, shippedTexture.Pixels),
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "diff_egt_signed.png"));

        if (detail.AffineFitTexture != null)
        {
            PngWriter.SaveRgba(
                detail.AffineFitTexture.Pixels,
                detail.AffineFitTexture.Width,
                detail.AffineFitTexture.Height,
                Path.Combine(npcDir, "generated_egt_affine_fit.png"));
            PngWriter.SaveRgba(
                NpcTextureComparison.BuildDiffPixels(detail.AffineFitTexture.Pixels, shippedTexture.Pixels),
                generatedTexture.Width,
                generatedTexture.Height,
                Path.Combine(npcDir, "diff_egt_affine_fit.png"));
            PngWriter.SaveRgba(
                NpcTextureComparison.BuildAmplifiedDiffPixels(detail.AffineFitTexture.Pixels, shippedTexture.Pixels),
                generatedTexture.Width,
                generatedTexture.Height,
                Path.Combine(npcDir, "diff_egt_affine_fit_10x.png"));
        }

        // DXT1-roundtripped generated texture and its diff against shipped
        var dxtRoundtripped = Bc1Codec.RoundTrip(
            generatedTexture.Pixels, generatedTexture.Width, generatedTexture.Height);
        PngWriter.SaveRgba(
            dxtRoundtripped,
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "generated_egt_dxt.png"));
        PngWriter.SaveRgba(
            NpcTextureComparison.BuildDiffPixels(dxtRoundtripped, shippedTexture.Pixels),
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "diff_egt_dxt.png"));
        PngWriter.SaveRgba(
            NpcTextureComparison.BuildAmplifiedDiffPixels(dxtRoundtripped, shippedTexture.Pixels),
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "diff_egt_dxt_10x.png"));

        var metadata = new StringBuilder();
        metadata.AppendLine($"form_id=0x{result.FormId:X8}");
        metadata.AppendLine($"plugin_name={result.PluginName}");
        metadata.AppendLine($"editor_id={result.EditorId ?? string.Empty}");
        metadata.AppendLine($"full_name={result.FullName ?? string.Empty}");
        metadata.AppendLine("generated_kind=egt_delta");
        metadata.AppendLine("shipped_kind=egt_delta");
        metadata.AppendLine($"comparison_mode={result.ComparisonMode ?? string.Empty}");
        metadata.AppendLine($"width={result.Width}");
        metadata.AppendLine($"height={result.Height}");
        metadata.AppendLine($"mae_rgb={result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"rmse_rgb={result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"max_abs_rgb={result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"pixels_any_diff={result.PixelsWithAnyRgbDifference.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"pixels_gt1={result.PixelsWithRgbErrorAbove1.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"pixels_gt2={result.PixelsWithRgbErrorAbove2.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"pixels_gt4={result.PixelsWithRgbErrorAbove4.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"pixels_gt8={result.PixelsWithRgbErrorAbove8.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"ssim_lum={result.SsimLuminance.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"ssim_rgb={result.SsimRgbMean.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"nssim_lum={result.SsimNormalizedLuminance.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"nssim_rgb={result.SsimNormalizedRgbMean.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"affine_mae_rgb={result.AffineFitMeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"affine_rmse_rgb={result.AffineFitRootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"affine_max_abs_rgb={result.AffineFitMaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"affine_scale_r={result.AffineFitScaleRed.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"affine_scale_g={result.AffineFitScaleGreen.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"affine_scale_b={result.AffineFitScaleBlue.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"affine_bias_r={result.AffineFitBiasRed.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"affine_bias_g={result.AffineFitBiasGreen.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"affine_bias_b={result.AffineFitBiasBlue.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"shipped_texture={result.ShippedTexturePath}");
        metadata.AppendLine($"shipped_source_format={result.ShippedSourceFormat ?? string.Empty}");
        metadata.AppendLine($"shipped_source_path={result.ShippedSourcePath ?? string.Empty}");
        metadata.AppendLine($"base_texture={result.BaseTexturePath ?? string.Empty}");
        metadata.AppendLine($"egt_path={result.EgtPath ?? string.Empty}");
        File.WriteAllText(Path.Combine(npcDir, "metadata.txt"), metadata.ToString(), Encoding.UTF8);
    }

    private static string BuildImageDirectoryName(NpcAppearance appearance)
    {
        var safeName = NpcExportFileNaming.SanitizeStem(appearance.EditorId) ??
                       NpcExportFileNaming.SanitizeStem(appearance.FullName);
        return string.IsNullOrWhiteSpace(safeName)
            ? $"{appearance.NpcFormId:X8}"
            : $"{appearance.NpcFormId:X8}_{safeName}";
    }
}

