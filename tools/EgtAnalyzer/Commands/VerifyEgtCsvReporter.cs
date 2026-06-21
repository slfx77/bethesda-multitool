using System.Globalization;
using System.Text;
using EgtAnalyzer.Verification;
using BethesdaMultitool.Core.Formats.Dds;

namespace EgtAnalyzer.Commands;

/// <summary>
///     CSV report writers for <c>verify-egt</c>: the per-NPC verification report and the
///     per-NPC alternative bake-mode variant report, plus their shared formatting helpers.
/// </summary>
internal static class VerifyEgtCsvReporter
{
    internal static void WriteCsvReport(
        IEnumerable<NpcFaceGenTextureVerificationResult> results,
        string reportPath)
    {
        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            "form_id,plugin_name,editor_id,full_name,verified,failure_reason,comparison_mode,width,height,mae_rgb,rmse_rgb,max_abs_rgb,pixels_any_diff,pixels_gt1,pixels_gt2,pixels_gt4,pixels_gt8,ssim_lum,ssim_rgb,nssim_lum,nssim_rgb,affine_mae_rgb,affine_rmse_rgb,affine_max_abs_rgb,affine_scale_r,affine_scale_g,affine_scale_b,affine_bias_r,affine_bias_g,affine_bias_b,shipped_texture,shipped_source_format,shipped_source_path,base_texture,egt_path");

        foreach (var result in results.OrderBy(item => item.FormId))
        {
            sb.Append(Csv(result.FormId.ToString("X8", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(result.PluginName)).Append(',');
            sb.Append(Csv(result.EditorId)).Append(',');
            sb.Append(Csv(result.FullName)).Append(',');
            sb.Append(Csv(result.Verified ? "true" : "false")).Append(',');
            sb.Append(Csv(result.FailureReason)).Append(',');
            sb.Append(Csv(result.ComparisonMode)).Append(',');
            sb.Append(Csv(result.Width == 0 ? null : result.Width.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(result.Height == 0 ? null : result.Height.ToString(CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(result.Verified
                ? result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified ? result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture) : null))
                .Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithAnyRgbDifference.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithRgbErrorAbove1.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithRgbErrorAbove2.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithRgbErrorAbove4.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithRgbErrorAbove8.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.SsimLuminance.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.SsimRgbMean.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.SsimNormalizedLuminance.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.SsimNormalizedRgbMean.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitMeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitRootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitMaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitScaleRed.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitScaleGreen.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitScaleBlue.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitBiasRed.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitBiasGreen.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitBiasBlue.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.ShippedTexturePath)).Append(',');
            sb.Append(Csv(result.ShippedSourceFormat)).Append(',');
            sb.Append(Csv(result.ShippedSourcePath)).Append(',');
            sb.Append(Csv(result.BaseTexturePath)).Append(',');
            sb.Append(Csv(result.EgtPath)).AppendLine();
        }

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
    }

    internal static void WriteVariantCsvReport(
        IEnumerable<NpcFaceGenTextureVerificationDetail> details,
        string reportPath)
    {
        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            "form_id,plugin_name,editor_id,full_name,editor_id_kind,verified,base_texture,base_texture_name,egt_path,egt_name,shipped_texture,shipped_source_format,shipped_source_path,shipped_mean_rgb,shipped_delta_darkness,shipped_negative_delta_mean,shipped_positive_delta_mean,generated_mean_rgb,generated_delta_darkness,generated_negative_delta_mean,generated_positive_delta_mean,mae_quantized_floor,mae_quantized_trunc,mae_float_floor,mae_float_trunc,mae_quantized_double_floor,mae_quantized_double_trunc,mae_combined_256_floor,mae_combined_256_trunc,mae_combined_65536_floor,mae_combined_65536_trunc,affine_mae_rgb,affine_rmse_rgb,affine_max_abs_rgb,affine_scale_r,affine_scale_g,affine_scale_b,affine_bias_r,affine_bias_g,affine_bias_b,best_mode,best_mae,delta_vs_quantized_floor");

        foreach (var detail in details.OrderBy(item => item.Result.FormId))
        {
            var result = detail.Result;
            var shippedStats = ComputeDeltaToneStats(detail.ShippedTexture);
            var generatedStats = ComputeDeltaToneStats(detail.GeneratedTexture);
            var quantizedFloor = GetVariantMae(detail, "Quantized+EngineFloor");
            var quantizedTrunc = GetVariantMae(detail, "Quantized+EngineTrunc");
            var floatFloor = GetVariantMae(detail, "Float+EngineFloor");
            var floatTrunc = GetVariantMae(detail, "Float+EngineTrunc");
            var quantizedDoubleFloor = GetVariantMae(detail, "QuantizedDouble+Floor");
            var quantizedDoubleTrunc = GetVariantMae(detail, "QuantizedDouble+Trunc");
            var combined256Floor = GetVariantMae(detail, "Combined256+Floor");
            var combined256Trunc = GetVariantMae(detail, "Combined256+Trunc");
            var combined65536Floor = GetVariantMae(detail, "Combined65536+Floor");
            var combined65536Trunc = GetVariantMae(detail, "Combined65536+Trunc");

            var variantRows = detail.DiagnosticVariants ?? [];
            var bestVariant = variantRows
                .OrderBy(metric => metric.MeanAbsoluteRgbError)
                .ThenBy(metric => metric.MaxAbsoluteRgbError)
                .FirstOrDefault();

            var bestMode = bestVariant?.Mode;
            var bestMae = bestVariant?.MeanAbsoluteRgbError;
            var deltaVsCurrent = quantizedFloor.HasValue && bestMae.HasValue
                ? quantizedFloor.Value - bestMae.Value
                : (double?)null;

            sb.Append(Csv(result.FormId.ToString("X8", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(result.PluginName)).Append(',');
            sb.Append(Csv(result.EditorId)).Append(',');
            sb.Append(Csv(result.FullName)).Append(',');
            sb.Append(Csv(ClassifyEditorId(result.EditorId))).Append(',');
            sb.Append(Csv(result.Verified ? "true" : "false")).Append(',');
            sb.Append(Csv(result.BaseTexturePath)).Append(',');
            sb.Append(Csv(GetFileName(result.BaseTexturePath))).Append(',');
            sb.Append(Csv(result.EgtPath)).Append(',');
            sb.Append(Csv(GetFileName(result.EgtPath))).Append(',');
            sb.Append(Csv(result.ShippedTexturePath)).Append(',');
            sb.Append(Csv(result.ShippedSourceFormat)).Append(',');
            sb.Append(Csv(result.ShippedSourcePath)).Append(',');
            sb.Append(Csv(FormatMae(shippedStats?.MeanRgb))).Append(',');
            sb.Append(Csv(FormatMae(shippedStats?.Darkness))).Append(',');
            sb.Append(Csv(FormatMae(shippedStats?.NegativeDeltaMean))).Append(',');
            sb.Append(Csv(FormatMae(shippedStats?.PositiveDeltaMean))).Append(',');
            sb.Append(Csv(FormatMae(generatedStats?.MeanRgb))).Append(',');
            sb.Append(Csv(FormatMae(generatedStats?.Darkness))).Append(',');
            sb.Append(Csv(FormatMae(generatedStats?.NegativeDeltaMean))).Append(',');
            sb.Append(Csv(FormatMae(generatedStats?.PositiveDeltaMean))).Append(',');
            sb.Append(Csv(FormatMae(quantizedFloor))).Append(',');
            sb.Append(Csv(FormatMae(quantizedTrunc))).Append(',');
            sb.Append(Csv(FormatMae(floatFloor))).Append(',');
            sb.Append(Csv(FormatMae(floatTrunc))).Append(',');
            sb.Append(Csv(FormatMae(quantizedDoubleFloor))).Append(',');
            sb.Append(Csv(FormatMae(quantizedDoubleTrunc))).Append(',');
            sb.Append(Csv(FormatMae(combined256Floor))).Append(',');
            sb.Append(Csv(FormatMae(combined256Trunc))).Append(',');
            sb.Append(Csv(FormatMae(combined65536Floor))).Append(',');
            sb.Append(Csv(FormatMae(combined65536Trunc))).Append(',');
            sb.Append(Csv(FormatMae(result.AffineFitMeanAbsoluteRgbError))).Append(',');
            sb.Append(Csv(FormatMae(result.AffineFitRootMeanSquareRgbError))).Append(',');
            sb.Append(Csv(result.Verified
                ? result.AffineFitMaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(FormatMae(result.AffineFitScaleRed))).Append(',');
            sb.Append(Csv(FormatMae(result.AffineFitScaleGreen))).Append(',');
            sb.Append(Csv(FormatMae(result.AffineFitScaleBlue))).Append(',');
            sb.Append(Csv(FormatMae(result.AffineFitBiasRed))).Append(',');
            sb.Append(Csv(FormatMae(result.AffineFitBiasGreen))).Append(',');
            sb.Append(Csv(FormatMae(result.AffineFitBiasBlue))).Append(',');
            sb.Append(Csv(bestMode)).Append(',');
            sb.Append(Csv(FormatMae(bestMae))).Append(',');
            sb.Append(Csv(FormatMae(deltaVsCurrent))).AppendLine();
        }

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
    }

    private static double? GetVariantMae(
        NpcFaceGenTextureVerificationDetail detail,
        string mode)
    {
        return detail.DiagnosticVariants?
            .FirstOrDefault(metric => string.Equals(metric.Mode, mode, StringComparison.Ordinal))?
            .MeanAbsoluteRgbError;
    }

    private static string? FormatMae(double? value)
    {
        return value?.ToString("F6", CultureInfo.InvariantCulture);
    }

    private static DeltaToneStats? ComputeDeltaToneStats(DecodedTexture? texture)
    {
        if (texture == null || texture.Pixels.Length == 0)
        {
            return null;
        }

        double sum = 0;
        double negative = 0;
        double positive = 0;
        var samples = 0;

        for (var offset = 0; offset < texture.Pixels.Length; offset += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                var value = texture.Pixels[offset + channel];
                sum += value;
                negative += Math.Max(0, 127 - value);
                positive += Math.Max(0, value - 127);
                samples++;
            }
        }

        if (samples == 0)
        {
            return null;
        }

        var mean = sum / samples;
        return new DeltaToneStats(
            mean,
            127d - mean,
            negative / samples,
            positive / samples);
    }

    private static string ClassifyEditorId(string? editorId)
    {
        if (string.IsNullOrWhiteSpace(editorId))
        {
            return "missing";
        }

        if (editorId.Contains("TEMPLATE", StringComparison.OrdinalIgnoreCase))
        {
            return "template";
        }

        if (editorId.StartsWith("CGPreset", StringComparison.OrdinalIgnoreCase))
        {
            return "preset";
        }

        return "named";
    }

    private static string? GetFileName(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);
    }

    private sealed record DeltaToneStats(
        double MeanRgb,
        double Darkness,
        double NegativeDeltaMean,
        double PositiveDeltaMean);

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}
