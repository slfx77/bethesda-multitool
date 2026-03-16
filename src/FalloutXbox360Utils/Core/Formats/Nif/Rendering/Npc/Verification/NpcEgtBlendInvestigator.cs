using System.Globalization;
using System.Text;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal static class NpcEgtBlendInvestigator
{
    internal static NpcEgtBlendInvestigationDetail InvestigateDetailed(
        NpcAppearance appearance,
        NpcFaceGenTextureVerifier.ShippedNpcFaceTexture shippedTexture,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache)
    {
        var baseTexturePath = GetHeadTexturePath(appearance.HeadDiffuseOverride);
        var egtPath = appearance.BaseHeadNifPath != null
            ? Path.ChangeExtension(appearance.BaseHeadNifPath, ".egt")
            : null;

        var result = new NpcEgtBlendInvestigationResult
        {
            FormId = appearance.NpcFormId,
            PluginName = shippedTexture.PluginName,
            EditorId = appearance.EditorId,
            FullName = appearance.FullName,
            ShippedTexturePath = shippedTexture.VirtualPath,
            BaseTexturePath = baseTexturePath,
            EgtPath = egtPath
        };

        if (appearance.BaseHeadNifPath == null)
        {
            return new NpcEgtBlendInvestigationDetail(
                result with { FailureReason = "missing base head nif path" },
                null,
                null,
                null,
                null);
        }

        if (baseTexturePath == null)
        {
            return new NpcEgtBlendInvestigationDetail(
                result with { FailureReason = "missing head diffuse texture path" },
                null,
                null,
                null,
                null);
        }

        if (egtPath == null)
        {
            return new NpcEgtBlendInvestigationDetail(
                result with { FailureReason = "missing head egt path" },
                null,
                null,
                null,
                null);
        }

        if (!egtCache.TryGetValue(egtPath, out var egt))
        {
            egt = NpcRenderHelpers.LoadEgtFromBsa(egtPath, meshArchives);
            egtCache[egtPath] = egt;
        }

        if (egt == null)
        {
            return new NpcEgtBlendInvestigationDetail(
                result with { FailureReason = $"egt not found: {egtPath}" },
                null,
                null,
                null,
                null);
        }

        var shippedDecodedTexture = textureResolver.GetTexture(shippedTexture.VirtualPath);
        if (shippedDecodedTexture == null)
        {
            return new NpcEgtBlendInvestigationDetail(
                result with { FailureReason = $"shipped texture not found: {shippedTexture.VirtualPath}" },
                null,
                null,
                null,
                null);
        }

        var baseTexture = textureResolver.GetTexture(baseTexturePath);
        if (baseTexture == null)
        {
            return new NpcEgtBlendInvestigationDetail(
                result with { FailureReason = $"base texture not found: {baseTexturePath}" },
                shippedDecodedTexture,
                null,
                null,
                null);
        }

        var comparisonMode = shippedDecodedTexture.Width == egt.Cols &&
                             shippedDecodedTexture.Height == egt.Rows
            ? "native_egt"
            : "upscaled_egt";

        var currentPolicy = InvestigatePolicy(
            egt,
            shippedDecodedTexture,
            baseTexture,
            comparisonMode,
            "current",
            "Float accumulation replay",
            "npc_plus_race",
            "current_float",
            "engine_getcompressedimage",
            appearance.FaceGenTextureCoeffs ?? [],
            FaceGenTextureMorpher.TextureAccumulationMode.CurrentFloat,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half);
        if (currentPolicy == null)
        {
            return new NpcEgtBlendInvestigationDetail(
                result with { FailureReason = "current policy generation failed" },
                shippedDecodedTexture,
                null,
                null,
                null);
        }

        var recoveredCoeffs = NpcFaceGenCoefficientMerger.Merge(
                                  appearance.NpcFaceGenTextureCoeffs,
                                  appearance.RaceFaceGenTextureCoeffs) ??
                              [];
        var recoveredPolicy = InvestigatePolicy(
            egt,
            shippedDecodedTexture,
            baseTexture,
            comparisonMode,
            "recovered",
            "Decomp-backed engine replay",
            "npc_plus_race",
            "engine_quantized256",
            "engine_getcompressedimage",
            recoveredCoeffs,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half);
        if (recoveredPolicy == null)
        {
            return new NpcEgtBlendInvestigationDetail(
                result with { FailureReason = "recovered policy generation failed" },
                shippedDecodedTexture,
                currentPolicy,
                null,
                null);
        }

        var appliedDiffuseMetrics = NpcTextureComparison.CompareRgb(
            currentPolicy.AppliedDiffuseTexture.Pixels,
            recoveredPolicy.AppliedDiffuseTexture.Pixels,
            currentPolicy.AppliedDiffuseTexture.Width,
            currentPolicy.AppliedDiffuseTexture.Height);

        return new NpcEgtBlendInvestigationDetail(
            result with
            {
                ComparisonMode = comparisonMode,
                ShippedWidth = shippedDecodedTexture.Width,
                ShippedHeight = shippedDecodedTexture.Height,
                AppliedDiffuseWidth = currentPolicy.AppliedDiffuseTexture.Width,
                AppliedDiffuseHeight = currentPolicy.AppliedDiffuseTexture.Height,
                CurrentVsRecoveredAppliedDiffuseMeanAbsoluteRgbError = appliedDiffuseMetrics.MeanAbsoluteRgbError,
                CurrentVsRecoveredAppliedDiffuseRootMeanSquareRgbError = appliedDiffuseMetrics.RootMeanSquareRgbError,
                CurrentVsRecoveredAppliedDiffuseMaxAbsoluteRgbError = appliedDiffuseMetrics.MaxAbsoluteRgbError
            },
            shippedDecodedTexture,
            currentPolicy,
            recoveredPolicy,
            NpcTextureComparison.BuildDiffTexture(
                currentPolicy.AppliedDiffuseTexture,
                recoveredPolicy.AppliedDiffuseTexture));
    }

    internal static void WriteArtifacts(
        string rootDir,
        NpcEgtBlendInvestigationDetail detail)
    {
        var npcDir = Path.Combine(rootDir, BuildArtifactDirectoryName(detail.Result));
        if (Directory.Exists(npcDir))
        {
            Directory.Delete(npcDir, true);
        }

        Directory.CreateDirectory(npcDir);

        if (detail.ShippedTexture != null)
        {
            SaveTexture(detail.ShippedTexture, Path.Combine(npcDir, "shipped_egt.png"));
        }

        WritePolicyArtifacts(npcDir, detail.CurrentPolicy);
        WritePolicyArtifacts(npcDir, detail.RecoveredPolicy);

        if (detail.CurrentVsRecoveredAppliedDiffuseDiffTexture != null)
        {
            SaveTexture(
                detail.CurrentVsRecoveredAppliedDiffuseDiffTexture,
                Path.Combine(npcDir, "current_vs_recovered_applied_diffuse_diff.png"));
        }

        File.WriteAllText(
            Path.Combine(npcDir, "metadata.txt"),
            BuildMetadata(detail),
            Encoding.UTF8);
    }

    internal static void WriteSummaryCsv(
        IEnumerable<NpcEgtBlendInvestigationDetail> details,
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
            "form_id,plugin_name,editor_id,full_name,verified,failure_reason,comparison_mode,shipped_width,shipped_height,applied_width,applied_height,current_coeff_policy,current_accumulation,current_encoding,current_mae_rgb,current_rmse_rgb,current_max_abs_rgb,current_pixels_gt4,recovered_coeff_policy,recovered_accumulation,recovered_encoding,recovered_mae_rgb,recovered_rmse_rgb,recovered_max_abs_rgb,recovered_pixels_gt4,current_vs_recovered_applied_mae_rgb,current_vs_recovered_applied_rmse_rgb,current_vs_recovered_applied_max_abs_rgb,shipped_texture,base_texture,egt_path");

        foreach (var detail in details.OrderBy(item => item.Result.FormId))
        {
            var result = detail.Result;
            sb.Append(Csv(result.FormId.ToString("X8", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(result.PluginName)).Append(',');
            sb.Append(Csv(result.EditorId)).Append(',');
            sb.Append(Csv(result.FullName)).Append(',');
            sb.Append(Csv(result.Verified ? "true" : "false")).Append(',');
            sb.Append(Csv(result.FailureReason)).Append(',');
            sb.Append(Csv(result.ComparisonMode)).Append(',');
            sb.Append(Csv(result.ShippedWidth == 0 ? null : result.ShippedWidth.ToString(CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(
                    result.ShippedHeight == 0 ? null : result.ShippedHeight.ToString(CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(result.AppliedDiffuseWidth == 0
                ? null
                : result.AppliedDiffuseWidth.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(result.AppliedDiffuseHeight == 0
                ? null
                : result.AppliedDiffuseHeight.ToString(CultureInfo.InvariantCulture))).Append(',');
            AppendPolicyCsv(sb, detail.CurrentPolicy);
            AppendPolicyCsv(sb, detail.RecoveredPolicy);
            sb.Append(Csv(result.Verified
                ? result.CurrentVsRecoveredAppliedDiffuseMeanAbsoluteRgbError.ToString("F6",
                    CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.CurrentVsRecoveredAppliedDiffuseRootMeanSquareRgbError.ToString("F6",
                    CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.CurrentVsRecoveredAppliedDiffuseMaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.ShippedTexturePath)).Append(',');
            sb.Append(Csv(result.BaseTexturePath)).Append(',');
            sb.Append(Csv(result.EgtPath)).AppendLine();
        }

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
    }

    private static NpcEgtBlendInvestigationPolicyDetail? InvestigatePolicy(
        EgtParser egt,
        DecodedTexture shippedTexture,
        DecodedTexture baseTexture,
        string comparisonMode,
        string policyKey,
        string description,
        string coefficientPolicy,
        string accumulationModeLabel,
        string encodingModeLabel,
        float[] textureCoeffs,
        FaceGenTextureMorpher.TextureAccumulationMode accumulationMode,
        FaceGenTextureMorpher.DeltaTextureEncodingMode encodingMode)
    {
        var generatedEgt = comparisonMode == "native_egt"
            ? FaceGenTextureMorpher.BuildNativeDeltaTexture(egt, textureCoeffs, accumulationMode, encodingMode)
            : FaceGenTextureMorpher.BuildUpscaledDeltaTexture(
                egt,
                textureCoeffs,
                shippedTexture.Width,
                shippedTexture.Height,
                accumulationMode,
                encodingMode);

        if (generatedEgt == null)
        {
            return null;
        }

        if (generatedEgt.Width != shippedTexture.Width ||
            generatedEgt.Height != shippedTexture.Height)
        {
            return null;
        }

        var appliedDiffuse = FaceGenTextureMorpher.Apply(baseTexture, egt, textureCoeffs, accumulationMode);
        if (appliedDiffuse == null)
        {
            return null;
        }

        var metrics = NpcTextureComparison.CompareRgb(
            generatedEgt.Pixels,
            shippedTexture.Pixels,
            generatedEgt.Width,
            generatedEgt.Height);

        return new NpcEgtBlendInvestigationPolicyDetail(
            new NpcEgtBlendInvestigationPolicyResult
            {
                PolicyKey = policyKey,
                Description = description,
                CoefficientPolicy = coefficientPolicy,
                AccumulationMode = accumulationModeLabel,
                EncodingMode = encodingModeLabel,
                ComparisonMode = comparisonMode,
                Width = generatedEgt.Width,
                Height = generatedEgt.Height,
                MeanAbsoluteRgbError = metrics.MeanAbsoluteRgbError,
                RootMeanSquareRgbError = metrics.RootMeanSquareRgbError,
                MaxAbsoluteRgbError = metrics.MaxAbsoluteRgbError,
                PixelsWithAnyRgbDifference = metrics.PixelsWithAnyRgbDifference,
                PixelsWithRgbErrorAbove1 = metrics.PixelsWithRgbErrorAbove1,
                PixelsWithRgbErrorAbove2 = metrics.PixelsWithRgbErrorAbove2,
                PixelsWithRgbErrorAbove4 = metrics.PixelsWithRgbErrorAbove4,
                PixelsWithRgbErrorAbove8 = metrics.PixelsWithRgbErrorAbove8,
                AppliedDiffuseWidth = appliedDiffuse.Width,
                AppliedDiffuseHeight = appliedDiffuse.Height,
                TextureCoeffCount = textureCoeffs.Length
            },
            generatedEgt,
            NpcTextureComparison.BuildDiffTexture(generatedEgt, shippedTexture),
            appliedDiffuse);
    }

    private static string? GetHeadTexturePath(string? headDiffuseOverride)
    {
        if (string.IsNullOrWhiteSpace(headDiffuseOverride))
        {
            return null;
        }

        return NifTexturePathUtility.Normalize(headDiffuseOverride);
    }

    private static void WritePolicyArtifacts(
        string npcDir,
        NpcEgtBlendInvestigationPolicyDetail? policy)
    {
        if (policy == null)
        {
            return;
        }

        SaveTexture(
            policy.GeneratedEgtTexture,
            Path.Combine(npcDir, $"{policy.Result.PolicyKey}_generated_egt.png"));
        SaveTexture(
            policy.DiffEgtTexture,
            Path.Combine(npcDir, $"{policy.Result.PolicyKey}_diff_egt.png"));
        SaveTexture(
            policy.AppliedDiffuseTexture,
            Path.Combine(npcDir, $"{policy.Result.PolicyKey}_applied_diffuse.png"));
    }

    private static void SaveTexture(DecodedTexture texture, string path)
    {
        PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height, path);
    }

    private static string BuildArtifactDirectoryName(NpcEgtBlendInvestigationResult result)
    {
        var safeName = NpcExportFileNaming.SanitizeStem(result.EditorId) ??
                       NpcExportFileNaming.SanitizeStem(result.FullName);
        return string.IsNullOrWhiteSpace(safeName)
            ? $"{result.FormId:X8}"
            : $"{result.FormId:X8}_{safeName}";
    }

    private static string BuildMetadata(NpcEgtBlendInvestigationDetail detail)
    {
        var result = detail.Result;
        var sb = new StringBuilder();
        sb.AppendLine($"form_id=0x{result.FormId:X8}");
        sb.AppendLine($"plugin_name={result.PluginName}");
        sb.AppendLine($"editor_id={result.EditorId ?? string.Empty}");
        sb.AppendLine($"full_name={result.FullName ?? string.Empty}");
        sb.AppendLine($"comparison_mode={result.ComparisonMode ?? string.Empty}");
        sb.AppendLine($"shipped_texture={result.ShippedTexturePath}");
        sb.AppendLine($"base_texture={result.BaseTexturePath ?? string.Empty}");
        sb.AppendLine($"egt_path={result.EgtPath ?? string.Empty}");
        sb.AppendLine($"shipped_width={result.ShippedWidth}");
        sb.AppendLine($"shipped_height={result.ShippedHeight}");
        sb.AppendLine($"applied_diffuse_width={result.AppliedDiffuseWidth}");
        sb.AppendLine($"applied_diffuse_height={result.AppliedDiffuseHeight}");
        AppendPolicyMetadata(sb, detail.CurrentPolicy);
        AppendPolicyMetadata(sb, detail.RecoveredPolicy);
        if (result.Verified)
        {
            sb.AppendLine(
                $"current_vs_recovered_applied_mae_rgb={result.CurrentVsRecoveredAppliedDiffuseMeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
            sb.AppendLine(
                $"current_vs_recovered_applied_rmse_rgb={result.CurrentVsRecoveredAppliedDiffuseRootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
            sb.AppendLine(
                $"current_vs_recovered_applied_max_abs_rgb={result.CurrentVsRecoveredAppliedDiffuseMaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(result.FailureReason))
        {
            sb.AppendLine($"failure_reason={result.FailureReason}");
        }

        return sb.ToString();
    }

    private static void AppendPolicyMetadata(
        StringBuilder sb,
        NpcEgtBlendInvestigationPolicyDetail? policy)
    {
        if (policy == null)
        {
            return;
        }

        var prefix = policy.Result.PolicyKey;
        sb.AppendLine($"{prefix}_description={policy.Result.Description}");
        sb.AppendLine($"{prefix}_coefficient_policy={policy.Result.CoefficientPolicy}");
        sb.AppendLine($"{prefix}_accumulation_mode={policy.Result.AccumulationMode}");
        sb.AppendLine($"{prefix}_encoding_mode={policy.Result.EncodingMode}");
        sb.AppendLine($"{prefix}_comparison_mode={policy.Result.ComparisonMode}");
        sb.AppendLine($"{prefix}_texture_coeff_count={policy.Result.TextureCoeffCount}");
        sb.AppendLine($"{prefix}_width={policy.Result.Width}");
        sb.AppendLine($"{prefix}_height={policy.Result.Height}");
        sb.AppendLine(
            $"{prefix}_mae_rgb={policy.Result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        sb.AppendLine(
            $"{prefix}_rmse_rgb={policy.Result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        sb.AppendLine(
            $"{prefix}_max_abs_rgb={policy.Result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(
            $"{prefix}_pixels_any_diff={policy.Result.PixelsWithAnyRgbDifference.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(
            $"{prefix}_pixels_gt4={policy.Result.PixelsWithRgbErrorAbove4.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"{prefix}_applied_diffuse_width={policy.Result.AppliedDiffuseWidth}");
        sb.AppendLine($"{prefix}_applied_diffuse_height={policy.Result.AppliedDiffuseHeight}");
    }

    private static void AppendPolicyCsv(
        StringBuilder sb,
        NpcEgtBlendInvestigationPolicyDetail? policy)
    {
        if (policy == null)
        {
            sb.Append(",,,,,,,");
            return;
        }

        sb.Append(Csv(policy.Result.CoefficientPolicy)).Append(',');
        sb.Append(Csv(policy.Result.AccumulationMode)).Append(',');
        sb.Append(Csv(policy.Result.EncodingMode)).Append(',');
        sb.Append(Csv(policy.Result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
        sb.Append(Csv(policy.Result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
        sb.Append(Csv(policy.Result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture))).Append(',');
        sb.Append(Csv(policy.Result.PixelsWithRgbErrorAbove4.ToString(CultureInfo.InvariantCulture))).Append(',');
    }

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