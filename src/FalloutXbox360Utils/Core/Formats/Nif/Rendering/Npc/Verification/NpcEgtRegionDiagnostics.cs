using System.Globalization;
using System.Text;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal static class NpcEgtRegionDiagnostics
{
    private const int MorphIsolationTopCount = 5;

    private static readonly string[] BasisContributionRegions =
    [
        "mouth",
        "eyelids",
        "left_eye",
        "right_eye"
    ];

    private static readonly string[] MorphIsolationRegions =
    [
        "eyelids"
    ];

    private static readonly RegionDefinition[] Regions =
    [
        new("eyelids", 64f / 256f, 44f / 256f, 128f / 256f, 56f / 256f),
        new("eyes", 64f / 256f, 56f / 256f, 128f / 256f, 36f / 256f),
        new("left_eye", 68f / 256f, 56f / 256f, 48f / 256f, 32f / 256f),
        new("right_eye", 140f / 256f, 56f / 256f, 48f / 256f, 32f / 256f),
        new("mouth", 88f / 256f, 120f / 256f, 80f / 256f, 56f / 256f),
        new("nose_mouth", 84f / 256f, 96f / 256f, 88f / 256f, 88f / 256f),
        new("lower_face", 72f / 256f, 96f / 256f, 112f / 256f, 112f / 256f),
        new("neck", 88f / 256f, 176f / 256f, 80f / 256f, 48f / 256f)
    ];

    internal static NpcEgtRegionDiagnosticDetail DiagnoseDetailed(
        NpcAppearance appearance,
        NpcFaceGenTextureVerifier.ShippedNpcFaceTexture shippedTexture,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache)
    {
        var verification = NpcFaceGenTextureVerifier.VerifyDetailed(
            appearance,
            shippedTexture,
            meshArchives,
            textureResolver,
            egtCache);

        var result = new NpcEgtRegionDiagnosticResult
        {
            FormId = verification.Result.FormId,
            PluginName = verification.Result.PluginName,
            EditorId = verification.Result.EditorId,
            FullName = verification.Result.FullName,
            ShippedTexturePath = verification.Result.ShippedTexturePath,
            BaseTexturePath = verification.Result.BaseTexturePath,
            EgtPath = verification.Result.EgtPath,
            ComparisonMode = verification.Result.ComparisonMode,
            Width = verification.Result.Width,
            Height = verification.Result.Height,
            MeanAbsoluteRgbError = verification.Result.MeanAbsoluteRgbError,
            RootMeanSquareRgbError = verification.Result.RootMeanSquareRgbError,
            MaxAbsoluteRgbError = verification.Result.MaxAbsoluteRgbError,
            FailureReason = verification.Result.FailureReason
        };

        if (!verification.Result.Verified ||
            verification.GeneratedTexture == null ||
            verification.ShippedTexture == null)
        {
            return new NpcEgtRegionDiagnosticDetail(
                result,
                verification.GeneratedTexture,
                verification.ShippedTexture,
                null,
                null,
                [],
                new Dictionary<string, IReadOnlyList<NpcEgtBasisContributionResult>>(StringComparer.Ordinal),
                [],
                []);
        }

        if (string.IsNullOrWhiteSpace(verification.Result.EgtPath))
        {
            return new NpcEgtRegionDiagnosticDetail(
                result with { FailureReason = "missing egt path for region diagnostics" },
                verification.GeneratedTexture,
                verification.ShippedTexture,
                null,
                null,
                [],
                new Dictionary<string, IReadOnlyList<NpcEgtBasisContributionResult>>(StringComparer.Ordinal),
                [],
                []);
        }

        if (!egtCache.TryGetValue(verification.Result.EgtPath!, out var egt))
        {
            egt = NpcRenderHelpers.LoadEgtFromBsa(verification.Result.EgtPath!, meshArchives);
            egtCache[verification.Result.EgtPath!] = egt;
        }

        if (egt == null)
        {
            return new NpcEgtRegionDiagnosticDetail(
                result with { FailureReason = $"egt not found: {verification.Result.EgtPath}" },
                verification.GeneratedTexture,
                verification.ShippedTexture,
                null,
                null,
                [],
                new Dictionary<string, IReadOnlyList<NpcEgtBasisContributionResult>>(StringComparer.Ordinal),
                [],
                []);
        }

        var generatedTexture = verification.GeneratedTexture;
        var shippedDecodedTexture = verification.ShippedTexture;
        var diffTexture = NpcTextureComparison.BuildDiffTexture(
            generatedTexture,
            shippedDecodedTexture);
        var signedBiasTexture = NpcTextureComparison.BuildSignedBiasTexture(
            shippedDecodedTexture,
            generatedTexture);

        var regionResults = BuildRegionResults(
            generatedTexture,
            shippedDecodedTexture,
            diffTexture,
            signedBiasTexture);
        var basisContributionsByRegion = BuildBasisContributionsByRegion(
            appearance,
            egt,
            regionResults,
            generatedTexture,
            shippedDecodedTexture);
        var morphIsolationDetails = BuildMorphIsolationDetails(
            egt,
            regionResults,
            basisContributionsByRegion);
        var textureControls = BuildTextureControls(appearance);

        return new NpcEgtRegionDiagnosticDetail(
            result,
            generatedTexture,
            shippedDecodedTexture,
            diffTexture,
            signedBiasTexture,
            regionResults,
            basisContributionsByRegion,
            morphIsolationDetails,
            textureControls);
    }

    internal static void WriteArtifacts(
        string rootDir,
        NpcEgtRegionDiagnosticDetail detail)
    {
        var npcDir = Path.Combine(rootDir, BuildArtifactDirectoryName(detail.Result));
        if (Directory.Exists(npcDir))
        {
            Directory.Delete(npcDir, true);
        }

        Directory.CreateDirectory(npcDir);

        if (detail.GeneratedTexture != null)
        {
            SaveTexture(detail.GeneratedTexture, Path.Combine(npcDir, "generated_egt.png"));
        }

        if (detail.ShippedTexture != null)
        {
            SaveTexture(detail.ShippedTexture, Path.Combine(npcDir, "shipped_egt.png"));
        }

        if (detail.DiffTexture != null)
        {
            SaveTexture(detail.DiffTexture, Path.Combine(npcDir, "diff_egt.png"));
        }

        if (detail.SignedBiasTexture != null)
        {
            SaveTexture(detail.SignedBiasTexture, Path.Combine(npcDir, "signed_bias_egt.png"));
        }

        var regionsDir = Path.Combine(npcDir, "regions");
        Directory.CreateDirectory(regionsDir);
        foreach (var region in detail.RegionDetails)
        {
            var prefix = region.Result.RegionName;
            SaveTexture(region.GeneratedCrop, Path.Combine(regionsDir, $"{prefix}_generated.png"));
            SaveTexture(region.ShippedCrop, Path.Combine(regionsDir, $"{prefix}_shipped.png"));
            SaveTexture(region.DiffCrop, Path.Combine(regionsDir, $"{prefix}_diff.png"));
            SaveTexture(region.SignedBiasCrop, Path.Combine(regionsDir, $"{prefix}_signed_bias.png"));
        }

        File.WriteAllText(
            Path.Combine(npcDir, "regions.csv"),
            BuildRegionCsv(detail.RegionDetails),
            Encoding.UTF8);
        foreach (var (regionName, contributions) in detail.BasisContributionsByRegion.OrderBy(item => item.Key))
        {
            File.WriteAllText(
                Path.Combine(npcDir, $"{regionName}_basis_contributions.csv"),
                BuildBasisContributionCsv(contributions),
                Encoding.UTF8);
        }

        WriteMorphIsolationArtifacts(npcDir, detail.MorphIsolationDetails);

        File.WriteAllText(
            Path.Combine(npcDir, "texture_controls.csv"),
            BuildTextureControlCsv(detail.TextureControls),
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(npcDir, "metadata.txt"),
            BuildMetadata(detail),
            Encoding.UTF8);
    }

    internal static void WriteSummaryCsv(
        IEnumerable<NpcEgtRegionDiagnosticDetail> details,
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
            "form_id,plugin_name,editor_id,full_name,verified,failure_reason,comparison_mode,width,height,overall_mae_rgb,overall_rmse_rgb,overall_max_abs_rgb,eyelids_mae_rgb,eyelids_rmse_rgb,eyelids_max_abs_rgb,eyelids_signed_red,eyelids_signed_green,eyelids_signed_blue,left_eye_mae_rgb,right_eye_mae_rgb,mouth_mae_rgb,mouth_rmse_rgb,mouth_max_abs_rgb,mouth_signed_red,mouth_signed_green,mouth_signed_blue,lower_face_mae_rgb,neck_mae_rgb,top_eyelids_morph_index,top_eyelids_morph_mean_abs_rgb,top_mouth_morph_index,top_mouth_morph_mean_abs_rgb");

        foreach (var detail in details.OrderBy(item => item.Result.FormId))
        {
            var eyelids = detail.RegionDetails.FirstOrDefault(region => region.Result.RegionName == "eyelids");
            var leftEye = detail.RegionDetails.FirstOrDefault(region => region.Result.RegionName == "left_eye");
            var rightEye = detail.RegionDetails.FirstOrDefault(region => region.Result.RegionName == "right_eye");
            var mouth = detail.RegionDetails.FirstOrDefault(region => region.Result.RegionName == "mouth");
            var lowerFace = detail.RegionDetails.FirstOrDefault(region => region.Result.RegionName == "lower_face");
            var neck = detail.RegionDetails.FirstOrDefault(region => region.Result.RegionName == "neck");
            var topEyelidsMorph = GetTopBasisContribution(detail, "eyelids");
            var topMouthMorph = GetTopBasisContribution(detail, "mouth");

            sb.Append(Csv(detail.Result.FormId.ToString("X8", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(detail.Result.PluginName)).Append(',');
            sb.Append(Csv(detail.Result.EditorId)).Append(',');
            sb.Append(Csv(detail.Result.FullName)).Append(',');
            sb.Append(Csv(detail.Result.Verified ? "true" : "false")).Append(',');
            sb.Append(Csv(detail.Result.FailureReason)).Append(',');
            sb.Append(Csv(detail.Result.ComparisonMode)).Append(',');
            sb.Append(Csv(detail.Result.Width == 0 ? null : detail.Result.Width.ToString(CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(
                    detail.Result.Height == 0 ? null : detail.Result.Height.ToString(CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(detail.Result.Verified
                ? detail.Result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(detail.Result.Verified
                ? detail.Result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(detail.Result.Verified
                ? detail.Result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            AppendRegionCsv(sb, eyelids);
            sb.Append(Csv(FormatRegionDouble(leftEye, static region => region.MeanAbsoluteRgbError))).Append(',');
            sb.Append(Csv(FormatRegionDouble(rightEye, static region => region.MeanAbsoluteRgbError))).Append(',');
            AppendRegionCsv(sb, mouth);
            sb.Append(Csv(FormatRegionDouble(lowerFace, static region => region.MeanAbsoluteRgbError))).Append(',');
            sb.Append(Csv(FormatRegionDouble(neck, static region => region.MeanAbsoluteRgbError))).Append(',');
            sb.Append(Csv(topEyelidsMorph?.MorphIndex.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(topEyelidsMorph == null
                    ? null
                    : topEyelidsMorph.MergedMeanAbsoluteRgbContribution.ToString("F6", CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(topMouthMorph?.MorphIndex.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(topMouthMorph == null
                    ? null
                    : topMouthMorph.MergedMeanAbsoluteRgbContribution.ToString("F6", CultureInfo.InvariantCulture)))
                .AppendLine();
        }

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
    }

    private static List<NpcEgtRegionMetricDetail> BuildRegionResults(
        DecodedTexture generatedTexture,
        DecodedTexture shippedTexture,
        DecodedTexture diffTexture,
        DecodedTexture signedBiasTexture)
    {
        var results = new List<NpcEgtRegionMetricDetail>(Regions.Length);
        foreach (var regionDefinition in Regions)
        {
            var bounds = ScaleRegion(regionDefinition, generatedTexture.Width, generatedTexture.Height);
            var generatedCrop = NpcTextureComparison.Crop(
                generatedTexture,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height);
            var shippedCrop = NpcTextureComparison.Crop(
                shippedTexture,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height);
            var diffCrop = NpcTextureComparison.Crop(
                diffTexture,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height);
            var signedBiasCrop = NpcTextureComparison.Crop(
                signedBiasTexture,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height);

            var metrics = NpcTextureComparison.CompareRgb(
                generatedCrop.Pixels,
                shippedCrop.Pixels,
                generatedCrop.Width,
                generatedCrop.Height);
            var signedMetrics = NpcTextureComparison.CompareSignedRgb(
                shippedCrop.Pixels,
                generatedCrop.Pixels,
                generatedCrop.Width,
                generatedCrop.Height);

            results.Add(
                new NpcEgtRegionMetricDetail(
                    new NpcEgtRegionMetricResult
                    {
                        RegionName = regionDefinition.Name,
                        X = bounds.X,
                        Y = bounds.Y,
                        Width = bounds.Width,
                        Height = bounds.Height,
                        MeanAbsoluteRgbError = metrics.MeanAbsoluteRgbError,
                        RootMeanSquareRgbError = metrics.RootMeanSquareRgbError,
                        MaxAbsoluteRgbError = metrics.MaxAbsoluteRgbError,
                        MeanSignedRedError = signedMetrics.MeanSignedRedError,
                        MeanSignedGreenError = signedMetrics.MeanSignedGreenError,
                        MeanSignedBlueError = signedMetrics.MeanSignedBlueError
                    },
                    generatedCrop,
                    shippedCrop,
                    diffCrop,
                    signedBiasCrop));
        }

        return results;
    }

    private static List<NpcEgtMorphIsolationDetail> BuildMorphIsolationDetails(
        EgtParser egt,
        IReadOnlyList<NpcEgtRegionMetricDetail> regionResults,
        IReadOnlyDictionary<string, IReadOnlyList<NpcEgtBasisContributionResult>> basisContributionsByRegion)
    {
        var results = new List<NpcEgtMorphIsolationDetail>();
        foreach (var regionName in MorphIsolationRegions)
        {
            if (!basisContributionsByRegion.TryGetValue(regionName, out var contributions))
            {
                continue;
            }

            var region = regionResults.FirstOrDefault(item => item.Result.RegionName == regionName);
            if (region == null)
            {
                continue;
            }

            var rank = 1;
            foreach (var contribution in contributions.Take(MorphIsolationTopCount))
            {
                if (contribution.MorphIndex < 0 || contribution.MorphIndex >= egt.SymmetricMorphs.Length)
                {
                    rank++;
                    continue;
                }

                var actualCoefficients = new float[egt.SymmetricMorphs.Length];
                actualCoefficients[contribution.MorphIndex] = contribution.MergedCoeff;
                var unitCoefficients = new float[egt.SymmetricMorphs.Length];
                unitCoefficients[contribution.MorphIndex] = 1f;

                var actualContributionTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                    egt,
                    actualCoefficients,
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256,
                    FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half);
                var floatContributionTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                    egt,
                    actualCoefficients,
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256,
                    FaceGenTextureMorpher.DeltaTextureEncodingMode.Centered128);
                var unitBasisTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                    egt,
                    unitCoefficients,
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256,
                    FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half);
                var rawBasisTexture = BuildRawBasisTexture(egt, contribution.MorphIndex);

                if (actualContributionTexture == null ||
                    floatContributionTexture == null ||
                    unitBasisTexture == null ||
                    rawBasisTexture == null)
                {
                    rank++;
                    continue;
                }

                results.Add(
                    new NpcEgtMorphIsolationDetail(
                        regionName,
                        rank,
                        contribution,
                        rawBasisTexture,
                        NpcTextureComparison.Crop(
                            rawBasisTexture,
                            region.Result.X,
                            region.Result.Y,
                            region.Result.Width,
                            region.Result.Height),
                        floatContributionTexture,
                        NpcTextureComparison.Crop(
                            floatContributionTexture,
                            region.Result.X,
                            region.Result.Y,
                            region.Result.Width,
                            region.Result.Height),
                        actualContributionTexture,
                        NpcTextureComparison.Crop(
                            actualContributionTexture,
                            region.Result.X,
                            region.Result.Y,
                            region.Result.Width,
                            region.Result.Height),
                        unitBasisTexture,
                        NpcTextureComparison.Crop(
                            unitBasisTexture,
                            region.Result.X,
                            region.Result.Y,
                            region.Result.Width,
                            region.Result.Height)));
                rank++;
            }
        }

        return results;
    }

    private static DecodedTexture? BuildRawBasisTexture(EgtParser egt, int morphIndex)
    {
        if (morphIndex < 0 || morphIndex >= egt.SymmetricMorphs.Length)
        {
            return null;
        }

        var morph = egt.SymmetricMorphs[morphIndex];
        var width = egt.Cols;
        var height = egt.Rows;
        var pixels = new byte[width * height * 4];

        for (var i = 0; i < width * height; i++)
        {
            var srcRow = height - 1 - i / width;
            var srcCol = i % width;
            var srcIndex = srcRow * width + srcCol;
            var offset = i * 4;
            pixels[offset] = EncodeCentered128(morph.DeltaR[srcIndex]);
            pixels[offset + 1] = EncodeCentered128(morph.DeltaG[srcIndex]);
            pixels[offset + 2] = EncodeCentered128(morph.DeltaB[srcIndex]);
            pixels[offset + 3] = 255;
        }

        return DecodedTexture.FromBaseLevel(pixels, width, height);
    }

    private static Dictionary<string, IReadOnlyList<NpcEgtBasisContributionResult>> BuildBasisContributionsByRegion(
        NpcAppearance appearance,
        EgtParser egt,
        IReadOnlyList<NpcEgtRegionMetricDetail> regionResults,
        DecodedTexture generatedTexture,
        DecodedTexture shippedTexture)
    {
        var results = new Dictionary<string, IReadOnlyList<NpcEgtBasisContributionResult>>(StringComparer.Ordinal);
        foreach (var regionName in BasisContributionRegions)
        {
            var region = regionResults.FirstOrDefault(item => item.Result.RegionName == regionName);
            if (region == null)
            {
                continue;
            }

            results[regionName] = BuildBasisContributions(
                appearance,
                egt,
                region.Result.X,
                region.Result.Y,
                region.Result.Width,
                region.Result.Height,
                generatedTexture,
                shippedTexture);
        }

        return results;
    }

    private static List<NpcEgtBasisContributionResult> BuildBasisContributions(
        NpcAppearance appearance,
        EgtParser egt,
        int x,
        int y,
        int width,
        int height,
        DecodedTexture generatedTexture,
        DecodedTexture shippedTexture)
    {
        if (generatedTexture.Width != egt.Cols ||
            generatedTexture.Height != egt.Rows ||
            shippedTexture.Width != egt.Cols ||
            shippedTexture.Height != egt.Rows)
        {
            return [];
        }

        var results = new List<NpcEgtBasisContributionResult>(egt.SymmetricMorphs.Length);
        var pixelCount = width * height;
        if (pixelCount <= 0)
        {
            return results;
        }

        for (var morphIndex = 0; morphIndex < egt.SymmetricMorphs.Length; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            var npcCoeff = GetCoefficient(appearance.NpcFaceGenTextureCoeffs, morphIndex);
            var raceCoeff = GetCoefficient(appearance.RaceFaceGenTextureCoeffs, morphIndex);
            var mergedCoeff = GetCoefficient(appearance.FaceGenTextureCoeffs, morphIndex);
            var npcCoeff256 = (int)(npcCoeff * 256f);
            var raceCoeff256 = (int)(raceCoeff * 256f);
            var mergedCoeff256 = (int)(mergedCoeff * 256f);

            double npcAbsR = 0;
            double npcAbsG = 0;
            double npcAbsB = 0;
            double raceAbsR = 0;
            double raceAbsG = 0;
            double raceAbsB = 0;
            double mergedAbsR = 0;
            double mergedAbsG = 0;
            double mergedAbsB = 0;
            double mergedSignedR = 0;
            double mergedSignedG = 0;
            double mergedSignedB = 0;
            double errorAlignment = 0;

            for (var regionY = y; regionY < y + height; regionY++)
            {
                // EGT morph data is stored bottom-up; diagnostic crops use top-down texture coordinates.
                var sourceRow = egt.Rows - 1 - regionY;
                for (var regionX = x; regionX < x + width; regionX++)
                {
                    var sourceIndex = sourceRow * egt.Cols + regionX;
                    var npcR = morph.DeltaR[sourceIndex] * npcCoeff256 * scale256 / 65536f;
                    var npcG = morph.DeltaG[sourceIndex] * npcCoeff256 * scale256 / 65536f;
                    var npcB = morph.DeltaB[sourceIndex] * npcCoeff256 * scale256 / 65536f;
                    var raceR = morph.DeltaR[sourceIndex] * raceCoeff256 * scale256 / 65536f;
                    var raceG = morph.DeltaG[sourceIndex] * raceCoeff256 * scale256 / 65536f;
                    var raceB = morph.DeltaB[sourceIndex] * raceCoeff256 * scale256 / 65536f;
                    var mergedR = morph.DeltaR[sourceIndex] * mergedCoeff256 * scale256 / 65536f;
                    var mergedG = morph.DeltaG[sourceIndex] * mergedCoeff256 * scale256 / 65536f;
                    var mergedB = morph.DeltaB[sourceIndex] * mergedCoeff256 * scale256 / 65536f;

                    npcAbsR += Math.Abs(npcR);
                    npcAbsG += Math.Abs(npcG);
                    npcAbsB += Math.Abs(npcB);
                    raceAbsR += Math.Abs(raceR);
                    raceAbsG += Math.Abs(raceG);
                    raceAbsB += Math.Abs(raceB);
                    mergedAbsR += Math.Abs(mergedR);
                    mergedAbsG += Math.Abs(mergedG);
                    mergedAbsB += Math.Abs(mergedB);
                    mergedSignedR += mergedR;
                    mergedSignedG += mergedG;
                    mergedSignedB += mergedB;

                    var pixelOffset = (regionY * generatedTexture.Width + regionX) * 4;
                    var errorR = generatedTexture.Pixels[pixelOffset] - shippedTexture.Pixels[pixelOffset];
                    var errorG = generatedTexture.Pixels[pixelOffset + 1] - shippedTexture.Pixels[pixelOffset + 1];
                    var errorB = generatedTexture.Pixels[pixelOffset + 2] - shippedTexture.Pixels[pixelOffset + 2];
                    errorAlignment += mergedR * errorR + mergedG * errorG + mergedB * errorB;
                }
            }

            results.Add(
                new NpcEgtBasisContributionResult
                {
                    MorphIndex = morphIndex,
                    MorphScale = morph.Scale,
                    Scale256 = scale256,
                    NpcCoeff = npcCoeff,
                    NpcCoeff256 = npcCoeff256,
                    RaceCoeff = raceCoeff,
                    RaceCoeff256 = raceCoeff256,
                    MergedCoeff = mergedCoeff,
                    MergedCoeff256 = mergedCoeff256,
                    NpcMeanAbsoluteRgbContribution = (npcAbsR + npcAbsG + npcAbsB) / (pixelCount * 3d),
                    RaceMeanAbsoluteRgbContribution = (raceAbsR + raceAbsG + raceAbsB) / (pixelCount * 3d),
                    MergedMeanAbsoluteRgbContribution = (mergedAbsR + mergedAbsG + mergedAbsB) / (pixelCount * 3d),
                    MergedMeanSignedRedContribution = mergedSignedR / pixelCount,
                    MergedMeanSignedGreenContribution = mergedSignedG / pixelCount,
                    MergedMeanSignedBlueContribution = mergedSignedB / pixelCount,
                    ErrorAlignment = errorAlignment / (pixelCount * 3d)
                });
        }

        return results
            .OrderByDescending(item => item.MergedMeanAbsoluteRgbContribution)
            .ThenBy(item => item.MorphIndex)
            .ToList();
    }

    private static List<NpcEgtTextureControlResult> BuildTextureControls(NpcAppearance appearance)
    {
        var values = new List<NpcEgtTextureControlResult>();
        AppendTextureControls(values, "npc", appearance.NpcFaceGenTextureCoeffs);
        AppendTextureControls(values, "race", appearance.RaceFaceGenTextureCoeffs);
        AppendTextureControls(values, "merged", appearance.FaceGenTextureCoeffs);
        return values
            .OrderBy(item => item.Source)
            .ThenByDescending(item => Math.Abs(item.Value))
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static void AppendTextureControls(
        List<NpcEgtTextureControlResult> values,
        string source,
        float[]? coefficients)
    {
        if (coefficients is not { Length: 50 })
        {
            return;
        }

        foreach (var (name, value) in FaceGenControls.ComputeTextureSymmetric(coefficients))
        {
            values.Add(
                new NpcEgtTextureControlResult
                {
                    Source = source,
                    Name = name,
                    Value = value
                });
        }
    }

    private static RegionBounds ScaleRegion(
        RegionDefinition definition,
        int width,
        int height)
    {
        var x = Math.Clamp((int)Math.Round(definition.X * width), 0, Math.Max(0, width - 1));
        var y = Math.Clamp((int)Math.Round(definition.Y * height), 0, Math.Max(0, height - 1));
        var regionWidth = Math.Max(1, (int)Math.Round(definition.Width * width));
        var regionHeight = Math.Max(1, (int)Math.Round(definition.Height * height));

        if (x + regionWidth > width)
        {
            regionWidth = width - x;
        }

        if (y + regionHeight > height)
        {
            regionHeight = height - y;
        }

        return new RegionBounds(x, y, regionWidth, regionHeight);
    }

    private static float GetCoefficient(float[]? coefficients, int index)
    {
        return coefficients is { Length: > 0 } && index < coefficients.Length
            ? coefficients[index]
            : 0f;
    }

    private static string BuildRegionCsv(IEnumerable<NpcEgtRegionMetricDetail> regions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("region_name,x,y,width,height,mae_rgb,rmse_rgb,max_abs_rgb,signed_red,signed_green,signed_blue");
        foreach (var region in regions)
        {
            sb.Append(Csv(region.Result.RegionName)).Append(',');
            sb.Append(Csv(region.Result.X.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(region.Result.Y.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(region.Result.Width.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(region.Result.Height.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(region.Result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(region.Result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(region.Result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(region.Result.MeanSignedRedError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(region.Result.MeanSignedGreenError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(region.Result.MeanSignedBlueError.ToString("F6", CultureInfo.InvariantCulture))).AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildBasisContributionCsv(IEnumerable<NpcEgtBasisContributionResult> contributions)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "morph_index,morph_scale,scale256,npc_coeff,npc_coeff256,race_coeff,race_coeff256,merged_coeff,merged_coeff256,npc_mean_abs_rgb,race_mean_abs_rgb,merged_mean_abs_rgb,merged_signed_red,merged_signed_green,merged_signed_blue,error_alignment");
        foreach (var contribution in contributions)
        {
            sb.Append(Csv(contribution.MorphIndex.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(contribution.MorphScale.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(contribution.Scale256.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(contribution.NpcCoeff.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(contribution.NpcCoeff256.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(contribution.RaceCoeff.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(contribution.RaceCoeff256.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(contribution.MergedCoeff.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(contribution.MergedCoeff256.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(contribution.NpcMeanAbsoluteRgbContribution.ToString("F6", CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(contribution.RaceMeanAbsoluteRgbContribution.ToString("F6", CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(contribution.MergedMeanAbsoluteRgbContribution.ToString("F6", CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(contribution.MergedMeanSignedRedContribution.ToString("F6", CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(contribution.MergedMeanSignedGreenContribution.ToString("F6", CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(contribution.MergedMeanSignedBlueContribution.ToString("F6", CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(contribution.ErrorAlignment.ToString("F6", CultureInfo.InvariantCulture))).AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildTextureControlCsv(IEnumerable<NpcEgtTextureControlResult> controls)
    {
        var sb = new StringBuilder();
        sb.AppendLine("source,name,value,abs_value");
        foreach (var control in controls)
        {
            sb.Append(Csv(control.Source)).Append(',');
            sb.Append(Csv(control.Name)).Append(',');
            sb.Append(Csv(control.Value.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(Math.Abs(control.Value).ToString("F6", CultureInfo.InvariantCulture))).AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildMetadata(NpcEgtRegionDiagnosticDetail detail)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"form_id=0x{detail.Result.FormId:X8}");
        sb.AppendLine($"plugin_name={detail.Result.PluginName}");
        sb.AppendLine($"editor_id={detail.Result.EditorId ?? string.Empty}");
        sb.AppendLine($"full_name={detail.Result.FullName ?? string.Empty}");
        sb.AppendLine($"comparison_mode={detail.Result.ComparisonMode ?? string.Empty}");
        sb.AppendLine($"shipped_texture={detail.Result.ShippedTexturePath}");
        sb.AppendLine($"base_texture={detail.Result.BaseTexturePath ?? string.Empty}");
        sb.AppendLine($"egt_path={detail.Result.EgtPath ?? string.Empty}");
        sb.AppendLine($"width={detail.Result.Width}");
        sb.AppendLine($"height={detail.Result.Height}");
        sb.AppendLine(
            $"overall_mae_rgb={detail.Result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        sb.AppendLine(
            $"overall_rmse_rgb={detail.Result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        sb.AppendLine(
            $"overall_max_abs_rgb={detail.Result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)}");

        foreach (var region in detail.RegionDetails)
        {
            var prefix = region.Result.RegionName;
            sb.AppendLine(
                $"{prefix}_bounds={region.Result.X},{region.Result.Y},{region.Result.Width},{region.Result.Height}");
            sb.AppendLine(
                $"{prefix}_mae_rgb={region.Result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
            sb.AppendLine(
                $"{prefix}_rmse_rgb={region.Result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
            sb.AppendLine(
                $"{prefix}_max_abs_rgb={region.Result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine(
                $"{prefix}_signed_red={region.Result.MeanSignedRedError.ToString("F6", CultureInfo.InvariantCulture)}");
            sb.AppendLine(
                $"{prefix}_signed_green={region.Result.MeanSignedGreenError.ToString("F6", CultureInfo.InvariantCulture)}");
            sb.AppendLine(
                $"{prefix}_signed_blue={region.Result.MeanSignedBlueError.ToString("F6", CultureInfo.InvariantCulture)}");
        }

        foreach (var (regionName, contributions) in detail.BasisContributionsByRegion.OrderBy(item => item.Key))
        {
            foreach (var contribution in contributions.Take(10))
            {
                sb.AppendLine(
                    $"{regionName}_top_morph_{contribution.MorphIndex}=mean_abs_rgb:{contribution.MergedMeanAbsoluteRgbContribution.ToString("F6", CultureInfo.InvariantCulture)},merged_coeff:{contribution.MergedCoeff.ToString("F6", CultureInfo.InvariantCulture)},error_alignment:{contribution.ErrorAlignment.ToString("F6", CultureInfo.InvariantCulture)}");
            }
        }

        foreach (var source in new[] { "npc", "race", "merged" })
        {
            foreach (var control in detail.TextureControls
                         .Where(item => item.Source == source)
                         .OrderByDescending(item => Math.Abs(item.Value))
                         .Take(5))
            {
                sb.AppendLine(
                    $"{source}_top_control={control.Name}:{control.Value.ToString("F6", CultureInfo.InvariantCulture)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(detail.Result.FailureReason))
        {
            sb.AppendLine($"failure_reason={detail.Result.FailureReason}");
        }

        return sb.ToString();
    }

    private static void WriteMorphIsolationArtifacts(
        string npcDir,
        IReadOnlyList<NpcEgtMorphIsolationDetail> morphIsolations)
    {
        if (morphIsolations.Count == 0)
        {
            return;
        }

        var rootDir = Path.Combine(npcDir, "morph_isolation");
        Directory.CreateDirectory(rootDir);

        foreach (var group in morphIsolations.GroupBy(item => item.RegionName).OrderBy(group => group.Key))
        {
            var regionDir = Path.Combine(rootDir, group.Key);
            Directory.CreateDirectory(regionDir);

            foreach (var isolation in group.OrderBy(item => item.Rank))
            {
                var stem = $"rank{isolation.Rank:D2}_morph{isolation.BasisContribution.MorphIndex:D2}";
                SaveTexture(
                    isolation.RawBasisTexture,
                    Path.Combine(regionDir, $"{stem}_raw_full.png"));
                SaveTexture(
                    isolation.RawBasisCrop,
                    Path.Combine(regionDir, $"{stem}_raw_crop.png"));
                SaveTexture(
                    isolation.FloatContributionTexture,
                    Path.Combine(regionDir, $"{stem}_float_full.png"));
                SaveTexture(
                    isolation.FloatContributionCrop,
                    Path.Combine(regionDir, $"{stem}_float_crop.png"));
                SaveTexture(
                    isolation.ActualContributionTexture,
                    Path.Combine(regionDir, $"{stem}_actual_full.png"));
                SaveTexture(
                    isolation.ActualContributionCrop,
                    Path.Combine(regionDir, $"{stem}_actual_crop.png"));
                SaveTexture(
                    isolation.UnitBasisTexture,
                    Path.Combine(regionDir, $"{stem}_unit_full.png"));
                SaveTexture(
                    isolation.UnitBasisCrop,
                    Path.Combine(regionDir, $"{stem}_unit_crop.png"));
            }

            File.WriteAllText(
                Path.Combine(regionDir, "manifest.csv"),
                BuildMorphIsolationCsv(group.OrderBy(item => item.Rank)),
                Encoding.UTF8);
        }
    }

    private static string BuildMorphIsolationCsv(IEnumerable<NpcEgtMorphIsolationDetail> morphIsolations)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "region_name,rank,morph_index,morph_scale,scale256,merged_coeff,merged_coeff256,merged_mean_abs_rgb,merged_signed_red,merged_signed_green,merged_signed_blue,error_alignment,raw_signed_red,raw_signed_green,raw_signed_blue,float_signed_red,float_signed_green,float_signed_blue,encoded_signed_red,encoded_signed_green,encoded_signed_blue,raw_full_png,raw_crop_png,float_full_png,float_crop_png,actual_full_png,actual_crop_png,unit_full_png,unit_crop_png");
        foreach (var isolation in morphIsolations)
        {
            var stem = $"rank{isolation.Rank:D2}_morph{isolation.BasisContribution.MorphIndex:D2}";
            var rawStats = BuildMorphStageStats(isolation.RawBasisCrop, MorphStageDecodeMode.Centered128);
            var floatStats = BuildMorphStageStats(isolation.FloatContributionCrop, MorphStageDecodeMode.Centered128);
            var encodedStats = BuildMorphStageStats(isolation.ActualContributionCrop, MorphStageDecodeMode.EngineCompressed255Half);
            sb.Append(Csv(isolation.RegionName)).Append(',');
            sb.Append(Csv(isolation.Rank.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.MorphIndex.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.MorphScale.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.Scale256.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.MergedCoeff.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.MergedCoeff256.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.MergedMeanAbsoluteRgbContribution.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.MergedMeanSignedRedContribution.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.MergedMeanSignedGreenContribution.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.MergedMeanSignedBlueContribution.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(isolation.BasisContribution.ErrorAlignment.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(rawStats.MeanSignedRed.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(rawStats.MeanSignedGreen.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(rawStats.MeanSignedBlue.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(floatStats.MeanSignedRed.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(floatStats.MeanSignedGreen.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(floatStats.MeanSignedBlue.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(encodedStats.MeanSignedRed.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(encodedStats.MeanSignedGreen.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(encodedStats.MeanSignedBlue.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv($"{stem}_raw_full.png")).Append(',');
            sb.Append(Csv($"{stem}_raw_crop.png")).Append(',');
            sb.Append(Csv($"{stem}_float_full.png")).Append(',');
            sb.Append(Csv($"{stem}_float_crop.png")).Append(',');
            sb.Append(Csv($"{stem}_actual_full.png")).Append(',');
            sb.Append(Csv($"{stem}_actual_crop.png")).Append(',');
            sb.Append(Csv($"{stem}_unit_full.png")).Append(',');
            sb.Append(Csv($"{stem}_unit_crop.png")).AppendLine();
        }

        return sb.ToString();
    }

    private static MorphStageStats BuildMorphStageStats(
        DecodedTexture texture,
        MorphStageDecodeMode decodeMode)
    {
        var pixelCount = texture.Width * texture.Height;
        double sumR = 0;
        double sumG = 0;
        double sumB = 0;
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var offset = pixelIndex * 4;
            sumR += DecodeMorphStageChannel(texture.Pixels[offset], decodeMode);
            sumG += DecodeMorphStageChannel(texture.Pixels[offset + 1], decodeMode);
            sumB += DecodeMorphStageChannel(texture.Pixels[offset + 2], decodeMode);
        }

        return new MorphStageStats(
            sumR / pixelCount,
            sumG / pixelCount,
            sumB / pixelCount);
    }

    private static double DecodeMorphStageChannel(byte value, MorphStageDecodeMode decodeMode)
    {
        return decodeMode switch
        {
            MorphStageDecodeMode.Centered128 => value - 128d,
            MorphStageDecodeMode.EngineCompressed255Half => value * 2d - 255d,
            _ => throw new ArgumentOutOfRangeException(nameof(decodeMode), decodeMode, null)
        };
    }

    private static byte EncodeCentered128(int value)
    {
        var centered = 128 + value;
        if (centered <= 0)
        {
            return 0;
        }

        if (centered >= 255)
        {
            return 255;
        }

        return (byte)centered;
    }

    private static NpcEgtBasisContributionResult? GetTopBasisContribution(
        NpcEgtRegionDiagnosticDetail detail,
        string regionName)
    {
        return detail.BasisContributionsByRegion.TryGetValue(regionName, out var contributions)
            ? contributions
                .OrderByDescending(item => item.MergedMeanAbsoluteRgbContribution)
                .FirstOrDefault()
            : null;
    }

    private static void AppendRegionCsv(
        StringBuilder sb,
        NpcEgtRegionMetricDetail? region)
    {
        if (region == null)
        {
            sb.Append(",,,,,,");
            return;
        }

        sb.Append(Csv(region.Result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
        sb.Append(Csv(region.Result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
        sb.Append(Csv(region.Result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture))).Append(',');
        sb.Append(Csv(region.Result.MeanSignedRedError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
        sb.Append(Csv(region.Result.MeanSignedGreenError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
        sb.Append(Csv(region.Result.MeanSignedBlueError.ToString("F6", CultureInfo.InvariantCulture))).Append(',');
    }

    private static string? FormatRegionDouble(
        NpcEgtRegionMetricDetail? region,
        Func<NpcEgtRegionMetricResult, double> selector)
    {
        return region == null
            ? null
            : selector(region.Result).ToString("F6", CultureInfo.InvariantCulture);
    }

    private static void SaveTexture(DecodedTexture texture, string path)
    {
        PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height, path);
    }

    private static string BuildArtifactDirectoryName(NpcEgtRegionDiagnosticResult result)
    {
        var safeName = NpcExportFileNaming.SanitizeStem(result.EditorId) ??
                       NpcExportFileNaming.SanitizeStem(result.FullName);
        return string.IsNullOrWhiteSpace(safeName)
            ? $"{result.FormId:X8}"
            : $"{result.FormId:X8}_{safeName}";
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

    private readonly record struct RegionDefinition(
        string Name,
        float X,
        float Y,
        float Width,
        float Height);

    private readonly record struct RegionBounds(
        int X,
        int Y,
        int Width,
        int Height);

    private readonly record struct MorphStageStats(
        double MeanSignedRed,
        double MeanSignedGreen,
        double MeanSignedBlue);

    private enum MorphStageDecodeMode
    {
        Centered128,
        EngineCompressed255Half
    }
}
