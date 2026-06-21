using System.Globalization;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using static EgtAnalyzer.Verification.DeltaTextureHelpers;
using static EgtAnalyzer.Verification.RawDeltaFitSolver;

namespace EgtAnalyzer.Verification;

internal static class RawDeltaFitDumper
{
    private const int TopRegionRawFitCount = 5;
    private static readonly int[] LateHotspotFamilyIndices = [35, 36, 37, 38, 39, 40, 41, 42, 43, 45, 46, 49];

    internal static RawDeltaCoefficientFitResult? DumpRawDeltaCoefficientFit(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        DecodedTexture shippedEncodedTexture,
        (float[] R, float[] G, float[] B) shippedDecoded,
        FloatDeltaRgbComparisonMetrics currentRawMetrics,
        IReadOnlyList<ResidualProjectionRow>? residualProjectionRows,
        int[]? residualSubspaceIndices)
    {
        var fit = SolveQuantizedRawDeltaCoefficientFit(egt, shippedDecoded, currentCoefficients);
        if (fit == null)
        {
            return null;
        }

        var quantizedCoefficients = fit.QuantizedCoefficient256
            .Select(v => v / 256f)
            .ToArray();
        var fittedTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            quantizedCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (fittedTexture == null)
        {
            return null;
        }

        var fittedRgbMetrics = NpcTextureComparison.CompareRgb(
            fittedTexture.Pixels,
            shippedEncodedTexture.Pixels,
            fittedTexture.Width,
            fittedTexture.Height);

        Console.WriteLine(
            $"  RAWFIT 0x{appearance.NpcFormId:X8}: " +
            $"rawMAE={currentRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawMAE={fit.FittedRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawRMSE={fit.FittedRawMetrics.RootMeanSquareRgbError:F4} " +
            $"fitRgbMAE={fittedRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRgbMax={fittedRgbMetrics.MaxAbsoluteRgbError}");

        var floatOracleTexture = DecodedTexture.FromBaseLevel(
            EncodeEngineCompressedDeltaPixels(
                fit.FloatOracleBuffers.R,
                fit.FloatOracleBuffers.G,
                fit.FloatOracleBuffers.B,
                egt.Cols,
                egt.Rows),
            egt.Cols,
            egt.Rows);
        var floatOracleRgbMetrics = NpcTextureComparison.CompareRgb(
            floatOracleTexture.Pixels,
            shippedEncodedTexture.Pixels,
            floatOracleTexture.Width,
            floatOracleTexture.Height);

        Console.WriteLine(
            $"  RAWFIT-FLOAT-ORACLE 0x{appearance.NpcFormId:X8}: " +
            $"fitRawMAE={fit.FloatOracleRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawRMSE={fit.FloatOracleRawMetrics.RootMeanSquareRgbError:F4} " +
            $"fitRgbMAE={floatOracleRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRgbMax={floatOracleRgbMetrics.MaxAbsoluteRgbError}");

        if (residualSubspaceIndices is { Length: > 0 })
        {
            var subspaceFit = SolveQuantizedRawDeltaResidualSubspaceFit(
                egt,
                currentCoefficients,
                shippedDecoded,
                residualSubspaceIndices);
            if (subspaceFit != null)
            {
                var subspaceTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                    egt,
                    subspaceFit.AbsoluteCoefficients,
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
                    FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
                if (subspaceTexture != null)
                {
                    var subspaceRgbMetrics = NpcTextureComparison.CompareRgb(
                        subspaceTexture.Pixels,
                        shippedEncodedTexture.Pixels,
                        subspaceTexture.Width,
                        subspaceTexture.Height);

                    Console.WriteLine(
                        $"  RAWFIT-SUBSPACE 0x{appearance.NpcFormId:X8}: " +
                        $"fitRawMAE={subspaceFit.FittedRawMetrics.MeanAbsoluteRgbError:F4} " +
                        $"fitRawRMSE={subspaceFit.FittedRawMetrics.RootMeanSquareRgbError:F4} " +
                        $"fitRgbMAE={subspaceRgbMetrics.MeanAbsoluteRgbError:F4} " +
                        $"fitRgbMax={subspaceRgbMetrics.MaxAbsoluteRgbError}");

                    Console.WriteLine($"  RAWFIT-SUBSPACE-TOP 0x{appearance.NpcFormId:X8}:");
                    foreach (var row in subspaceFit.Rows
                                 .OrderByDescending(item => Math.Abs(item.Delta256))
                                 .ThenBy(item => item.Index)
                                 .Take(TopRegionRawFitCount))
                    {
                        Console.WriteLine(
                            $"    [{row.Index:D2}] current256={row.Current256,6} fit256={row.Fit256,6} " +
                            $"delta256={row.Delta256,6:+#;-#;0} current={row.CurrentCoeff,8:F4} fit={row.FitCoeff,8:F4}");
                    }
                }
            }
        }

        var channelFreeFit = SolveQuantizedRawDeltaChannelFreeCoefficientFit(egt, shippedDecoded, currentCoefficients);
        if (channelFreeFit != null)
        {
            var channelFreeTexture = DecodedTexture.FromBaseLevel(
                EncodeEngineCompressedDeltaPixels(
                    channelFreeFit.FittedR,
                    channelFreeFit.FittedG,
                    channelFreeFit.FittedB,
                    egt.Cols,
                    egt.Rows),
                egt.Cols,
                egt.Rows);
            var channelFreeRgbMetrics = NpcTextureComparison.CompareRgb(
                channelFreeTexture.Pixels,
                shippedEncodedTexture.Pixels,
                channelFreeTexture.Width,
                channelFreeTexture.Height);

            Console.WriteLine(
                $"  RAWFIT-RGBFREE 0x{appearance.NpcFormId:X8}: " +
                $"fitRawMAE={channelFreeFit.FittedRawMetrics.MeanAbsoluteRgbError:F4} " +
                $"fitRawRMSE={channelFreeFit.FittedRawMetrics.RootMeanSquareRgbError:F4} " +
                $"fitRgbMAE={channelFreeRgbMetrics.MeanAbsoluteRgbError:F4} " +
                $"fitRgbMax={channelFreeRgbMetrics.MaxAbsoluteRgbError}");

            var rankedChannelDelta = channelFreeFit.QuantizedCoefficient256R
                .Select((valueR, index) =>
                {
                    var current256 = index < currentCoefficients.Length
                        ? (int)(currentCoefficients[index] * 256f)
                        : 0;
                    var fit256G = channelFreeFit.QuantizedCoefficient256G[index];
                    var fit256B = channelFreeFit.QuantizedCoefficient256B[index];
                    var deltaR = valueR - current256;
                    var deltaG = fit256G - current256;
                    var deltaB = fit256B - current256;
                    return new
                    {
                        Index = index,
                        Current256 = current256,
                        Fit256R = valueR,
                        Fit256G = fit256G,
                        Fit256B = fit256B,
                        DeltaMagnitude = Math.Max(Math.Abs(deltaR), Math.Max(Math.Abs(deltaG), Math.Abs(deltaB)))
                    };
                })
                .OrderByDescending(x => x.DeltaMagnitude)
                .ThenBy(x => x.Index)
                .Take(10)
                .ToArray();

            Console.WriteLine($"  RAWFIT-RGBFREE-TOP 0x{appearance.NpcFormId:X8}:");
            foreach (var row in rankedChannelDelta)
            {
                Console.WriteLine(
                    $"    [{row.Index:D2}] current256={row.Current256,6} " +
                    $"fitR={row.Fit256R,6} fitG={row.Fit256G,6} fitB={row.Fit256B,6}");
            }
        }

        var rankedDelta = fit.QuantizedCoefficient256
            .Select((value, index) =>
            {
                var current256 = index < currentCoefficients.Length
                    ? (int)(currentCoefficients[index] * 256f)
                    : 0;
                var delta256 = value - current256;
                return new
                {
                    Index = index,
                    Current256 = current256,
                    Fit256 = value,
                    Delta256 = delta256,
                    CurrentCoeff = index < currentCoefficients.Length ? currentCoefficients[index] : 0f,
                    FitCoeff = value / 256f
                };
            })
            .OrderByDescending(x => Math.Abs(x.Delta256))
            .ThenBy(x => x.Index)
            .Take(10)
            .ToArray();

        Console.WriteLine($"  RAWFIT-TOP 0x{appearance.NpcFormId:X8}:");
        foreach (var row in rankedDelta)
        {
            Console.WriteLine(
                $"    [{row.Index:D2}] current256={row.Current256,6} fit256={row.Fit256,6} " +
                $"delta256={row.Delta256,6:+#;-#;0} current={row.CurrentCoeff,8:F4} fit={row.FitCoeff,8:F4}");
        }

        DumpHotspotSubspaceFit(
            appearance,
            egt,
            currentCoefficients,
            shippedEncodedTexture,
            shippedDecoded,
            residualProjectionRows,
            currentRawMetrics);

        return fit;
    }

    internal static void DumpRawFitProvenancePcaSummary(
        NpcAppearance appearance,
        EgtParser egt,
        DecodedTexture shippedEncodedTexture,
        IReadOnlyList<float[]> familyCoefficients,
        int[]? rawFitQuantizedCoefficient256 = null)
    {
        var currentCoefficients = appearance.FaceGenTextureCoeffs ?? [];
        var count = Math.Min(currentCoefficients.Length, egt.SymmetricMorphs.Length);
        if (count == 0)
        {
            return;
        }

        var shippedDecoded = DecodeEncodedDeltaTextureToFloatBuffers(shippedEncodedTexture);
        var currentNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (currentNative == null)
        {
            return;
        }

        var currentRawMetrics = CompareFloatDeltaRgb(currentNative.Value, shippedDecoded);
        var rawFitQuantized = rawFitQuantizedCoefficient256 is { Length: > 0 }
            ? rawFitQuantizedCoefficient256.Take(count).ToArray()
            : null;
        FloatDeltaRgbComparisonMetrics rawFitRawMetrics;

        if (rawFitQuantized == null || rawFitQuantized.Length != count)
        {
            var rawFit = SolveQuantizedRawDeltaCoefficientFit(egt, shippedDecoded, currentCoefficients);
            if (rawFit == null)
            {
                return;
            }

            rawFitQuantized = rawFit.QuantizedCoefficient256.Take(count).ToArray();
            rawFitRawMetrics = rawFit.FittedRawMetrics;
        }
        else
        {
            var rawFitCoefficients = rawFitQuantized
                .Select(value => value / 256f)
                .ToArray();
            var rawFitNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
                egt,
                rawFitCoefficients,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
            if (rawFitNative == null)
            {
                return;
            }

            rawFitRawMetrics = CompareFloatDeltaRgb(rawFitNative.Value, shippedDecoded);
        }

        var bestFamilyQuantized = Array.Empty<int>();
        FloatDeltaRgbComparisonMetrics? bestFamilyRawMetrics = null;
        DecodedTexture? bestFamilyTexture = null;
        var usableFamilyCount = 0;

        foreach (var candidate in familyCoefficients)
        {
            if (candidate.Length < count)
            {
                continue;
            }

            usableFamilyCount++;
            var candidateQuantized = candidate
                .Take(count)
                .Select(value => (int)Math.Round(value * 256f, MidpointRounding.AwayFromZero))
                .ToArray();
            var candidateCoefficients = candidateQuantized
                .Select(value => value / 256f)
                .ToArray();
            var candidateNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
                egt,
                candidateCoefficients,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
            if (candidateNative == null)
            {
                continue;
            }

            var candidateRawMetrics = CompareFloatDeltaRgb(candidateNative.Value, shippedDecoded);
            if (bestFamilyRawMetrics != null &&
                (candidateRawMetrics.MeanAbsoluteRgbError > bestFamilyRawMetrics.MeanAbsoluteRgbError ||
                 (Math.Abs(candidateRawMetrics.MeanAbsoluteRgbError - bestFamilyRawMetrics.MeanAbsoluteRgbError) <= 1e-9 &&
                  candidateRawMetrics.RootMeanSquareRgbError >= bestFamilyRawMetrics.RootMeanSquareRgbError)))
            {
                continue;
            }

            var candidateTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                egt,
                candidateCoefficients,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
                FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
            if (candidateTexture == null)
            {
                continue;
            }

            bestFamilyQuantized = candidateQuantized;
            bestFamilyRawMetrics = candidateRawMetrics;
            bestFamilyTexture = candidateTexture;
        }

        if (bestFamilyRawMetrics == null || bestFamilyTexture == null)
        {
            return;
        }

        var rawFitTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            rawFitQuantized.Select(value => value / 256f).ToArray(),
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (rawFitTexture == null)
        {
            return;
        }

        var familyRgbMetrics = NpcTextureComparison.CompareRgb(
            bestFamilyTexture.Pixels,
            shippedEncodedTexture.Pixels,
            bestFamilyTexture.Width,
            bestFamilyTexture.Height);
        var rawFitRgbMetrics = NpcTextureComparison.CompareRgb(
            rawFitTexture.Pixels,
            shippedEncodedTexture.Pixels,
            rawFitTexture.Width,
            rawFitTexture.Height);

        var denominator = currentRawMetrics.MeanAbsoluteRgbError - rawFitRawMetrics.MeanAbsoluteRgbError;
        var explainedShare = Math.Abs(denominator) <= 1e-9
            ? 0d
            : (currentRawMetrics.MeanAbsoluteRgbError - bestFamilyRawMetrics.MeanAbsoluteRgbError) / denominator;

        Console.WriteLine(
            $"  RAWFIT-PROV-FAMILY 0x{appearance.NpcFormId:X8}: " +
            $"family={usableFamilyCount} " +
            $"currentRawMAE={currentRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"familyRawMAE={bestFamilyRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"rawFitRawMAE={rawFitRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"familyRgbMAE={familyRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"rawFitRgbMAE={rawFitRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"explained={explainedShare * 100d:F1}%");

        Console.WriteLine($"  RAWFIT-PROV-FAMILY-HOTSPOT 0x{appearance.NpcFormId:X8}:");
        foreach (var morphIndex in LateHotspotFamilyIndices)
        {
            if (morphIndex >= count)
            {
                continue;
            }

            var current256 = (int)Math.Round(currentCoefficients[morphIndex] * 256f, MidpointRounding.AwayFromZero);
            var family256 = bestFamilyQuantized[morphIndex];
            var rawFit256 = rawFitQuantized[morphIndex];
            Console.WriteLine(
                $"    [{morphIndex:D2}] current256={current256,6} family256={family256,6} rawFit256={rawFit256,6} " +
                $"deltaFam={family256 - current256,6:+#;-#;0} deltaRaw={rawFit256 - current256,6:+#;-#;0}");
        }
    }

    internal static void DumpHotspotSubspaceFit(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        DecodedTexture shippedEncodedTexture,
        (float[] R, float[] G, float[] B) shippedDecoded,
        IReadOnlyList<ResidualProjectionRow>? residualProjectionRows,
        FloatDeltaRgbComparisonMetrics currentRawMetrics)
    {
        if (residualProjectionRows == null || residualProjectionRows.Count == 0)
        {
            return;
        }

        var hotspotIndices = residualProjectionRows
            .OrderByDescending(row => row.MaxAbsDelta256)
            .ThenBy(row => row.MorphIndex)
            .Take(8)
            .Select(row => row.MorphIndex)
            .OrderBy(index => index)
            .ToArray();
        if (hotspotIndices.Length == 0)
        {
            return;
        }

        var currentNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (currentNative == null)
        {
            return;
        }

        var deltaFit = SolveQuantizedRawResidualDeltaFit(
            egt,
            currentNative.Value,
            shippedDecoded,
            hotspotIndices);
        if (deltaFit == null)
        {
            return;
        }

        var adjustedCoefficients = (float[])currentCoefficients.Clone();
        foreach (var (morphIndex, delta256) in hotspotIndices.Zip(deltaFit.DeltaCoefficient256))
        {
            if (morphIndex < adjustedCoefficients.Length)
            {
                adjustedCoefficients[morphIndex] += delta256 / 256f;
            }
        }

        var fittedTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            adjustedCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (fittedTexture == null)
        {
            return;
        }

        var fittedRgbMetrics = NpcTextureComparison.CompareRgb(
            fittedTexture.Pixels,
            shippedEncodedTexture.Pixels,
            fittedTexture.Width,
            fittedTexture.Height);
        var currentGenerated = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        var currentEyesMae = currentGenerated != null ? GetRegionMae(currentGenerated, shippedEncodedTexture, "eyes") : 0d;
        var currentMouthMae = currentGenerated != null ? GetRegionMae(currentGenerated, shippedEncodedTexture, "mouth") : 0d;
        var fittedEyesMae = GetRegionMae(fittedTexture, shippedEncodedTexture, "eyes");
        var fittedMouthMae = GetRegionMae(fittedTexture, shippedEncodedTexture, "mouth");

        Console.WriteLine(
            $"  RAWFIT-HOTSPOT8 0x{appearance.NpcFormId:X8}: " +
            $"indices=[{string.Join(", ", hotspotIndices.Select(index => index.ToString("D2", CultureInfo.InvariantCulture)))}] " +
            $"rawMAE={currentRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawMAE={deltaFit.FittedResidualMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRgbMAE={fittedRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"eyesMAE={fittedEyesMae:F4} (Δ={fittedEyesMae - currentEyesMae:+0.0000;-0.0000}) " +
            $"mouthMAE={fittedMouthMae:F4} (Δ={fittedMouthMae - currentMouthMae:+0.0000;-0.0000})");

        Console.WriteLine($"  RAWFIT-HOTSPOT8-DELTA 0x{appearance.NpcFormId:X8}:");
        foreach (var (morphIndex, delta256) in hotspotIndices.Zip(deltaFit.DeltaCoefficient256))
        {
            var current256 = morphIndex < currentCoefficients.Length
                ? (int)(currentCoefficients[morphIndex] * 256f)
                : 0;
            Console.WriteLine(
                $"    [{morphIndex:D2}] current256={current256,6} delta256={delta256,6:+#;-#;0} " +
                $"new256={current256 + delta256,6}");
        }
    }

    internal static void DumpRegionalRawDeltaFits(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        DecodedTexture currentGeneratedTexture,
        DecodedTexture shippedEncodedTexture,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var fit = SolveQuantizedRawDeltaCoefficientFit(egt, shippedDecoded, currentCoefficients);
        if (fit == null)
        {
            return;
        }

        var fittedCoefficients = fit.QuantizedCoefficient256
            .Select(value => value / 256f)
            .ToArray();
        var fittedTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            fittedCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (fittedTexture == null)
        {
            return;
        }

        Console.WriteLine($"  RAWFIT-REGION 0x{appearance.NpcFormId:X8}:");
        foreach (var regionName in new[] { "whole", "eyes", "mouth", "nose", "forehead" })
        {
            var currentMae = GetRegionMae(currentGeneratedTexture, shippedEncodedTexture, regionName);
            var fittedMae = GetRegionMae(fittedTexture, shippedEncodedTexture, regionName);
            Console.WriteLine(
                $"    {regionName,-8} currentMAE={currentMae:F4} fitMAE={fittedMae:F4} " +
                $"delta={fittedMae - currentMae:+0.0000;-0.0000}");
        }

        var currentRawWhole = CompareFloatDeltaRgb(currentNative, shippedDecoded);
        var fittedNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            fittedCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (fittedNative != null)
        {
            var fittedRawWhole = CompareFloatDeltaRgb(fittedNative.Value, shippedDecoded);
            Console.WriteLine(
                $"    rawWhole  currentMAE={currentRawWhole.MeanAbsoluteRgbError:F4} " +
                $"fitMAE={fittedRawWhole.MeanAbsoluteRgbError:F4} " +
                $"delta={fittedRawWhole.MeanAbsoluteRgbError - currentRawWhole.MeanAbsoluteRgbError:+0.0000;-0.0000}");
        }
    }

    internal static void DumpResidualSubspaceFit(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        DecodedTexture shippedEncodedTexture,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded,
        FloatDeltaRgbComparisonMetrics currentRawMetrics,
        IReadOnlyList<int> residualSubspaceIndices)
    {
        var fit = SolveQuantizedRawDeltaResidualSubspaceFit(
            egt,
            currentCoefficients,
            shippedDecoded,
            residualSubspaceIndices);
        if (fit == null)
        {
            return;
        }

        var fittedTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            fit.AbsoluteCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (fittedTexture == null)
        {
            return;
        }

        var fittedRgbMetrics = NpcTextureComparison.CompareRgb(
            fittedTexture.Pixels,
            shippedEncodedTexture.Pixels,
            fittedTexture.Width,
            fittedTexture.Height);
        var currentGenerated = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        var currentEyesMae = currentGenerated != null ? GetRegionMae(currentGenerated, shippedEncodedTexture, "eyes") : 0d;
        var currentMouthMae = currentGenerated != null ? GetRegionMae(currentGenerated, shippedEncodedTexture, "mouth") : 0d;
        var fittedEyesMae = GetRegionMae(fittedTexture, shippedEncodedTexture, "eyes");
        var fittedMouthMae = GetRegionMae(fittedTexture, shippedEncodedTexture, "mouth");

        Console.WriteLine(
            $"  RAWFIT-SUBSPACE-EXPL 0x{appearance.NpcFormId:X8}: " +
            $"indices=[{string.Join(", ", fit.Rows.Select(row => row.Index.ToString("D2", CultureInfo.InvariantCulture)))}] " +
            $"rawMAE={currentRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawMAE={fit.FittedRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRgbMAE={fittedRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"eyesMAE={fittedEyesMae:F4} (Δ={fittedEyesMae - currentEyesMae:+0.0000;-0.0000}) " +
            $"mouthMAE={fittedMouthMae:F4} (Δ={fittedMouthMae - currentMouthMae:+0.0000;-0.0000})");

        Console.WriteLine($"  RAWFIT-SUBSPACE-EXPL-TOP 0x{appearance.NpcFormId:X8}:");
        foreach (var row in fit.Rows
                     .OrderByDescending(item => Math.Abs(item.Delta256))
                     .ThenBy(item => item.Index)
                     .Take(TopRegionRawFitCount))
        {
            Console.WriteLine(
                $"    [{row.Index:D2}] current256={row.Current256,6} fit256={row.Fit256,6} " +
                $"delta256={row.Delta256,6:+#;-#;0} current={row.CurrentCoeff,8:F4} fit={row.FitCoeff,8:F4}");
        }

        var fittedNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            fit.AbsoluteCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (fittedNative != null)
        {
            var currentResidual = CompareFloatDeltaRgb(currentNative, shippedDecoded);
            var fittedResidual = CompareFloatDeltaRgb(fittedNative.Value, shippedDecoded);
            Console.WriteLine(
                $"    rawWhole  currentMAE={currentResidual.MeanAbsoluteRgbError:F4} " +
                $"fitMAE={fittedResidual.MeanAbsoluteRgbError:F4} " +
                $"delta={fittedResidual.MeanAbsoluteRgbError - currentResidual.MeanAbsoluteRgbError:+0.0000;-0.0000}");
        }
    }
}
