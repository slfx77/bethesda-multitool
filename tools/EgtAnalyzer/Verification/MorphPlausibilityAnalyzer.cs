using BethesdaMultitool.Core.Formats.Nif.Rendering;
using static EgtAnalyzer.Verification.DeltaTextureHelpers;
using static EgtAnalyzer.Verification.MorphCorrectionHelpers;

namespace EgtAnalyzer.Verification;

internal static class MorphPlausibilityAnalyzer
{
    internal static MorphContentPlausibilityStats? ComputeMorphContentPlausibility(
        EgtParser egt,
        EgtMorph morph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(morph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var correctedR = new float[pixelCount];
        var correctedG = new float[pixelCount];
        var correctedB = new float[pixelCount];
        double sumAbsRequiredByteDelta = 0d;
        double sumAbsClipByte = 0d;
        var maxAbsRequiredByteDelta = 0f;
        var maxAbsClipByte = 0f;
        var inRangeCount = 0;
        var sampleCount = pixelCount * 3;

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var residualR = shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex];
            var residualG = shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex];
            var residualB = shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex];

            ApplyMorphContentCorrection(
                morph.DeltaR[pixelIndex],
                residualR,
                factor,
                out correctedR[pixelIndex],
                currentNative.R[pixelIndex],
                ref sumAbsRequiredByteDelta,
                ref sumAbsClipByte,
                ref maxAbsRequiredByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphContentCorrection(
                morph.DeltaG[pixelIndex],
                residualG,
                factor,
                out correctedG[pixelIndex],
                currentNative.G[pixelIndex],
                ref sumAbsRequiredByteDelta,
                ref sumAbsClipByte,
                ref maxAbsRequiredByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphContentCorrection(
                morph.DeltaB[pixelIndex],
                residualB,
                factor,
                out correctedB[pixelIndex],
                currentNative.B[pixelIndex],
                ref sumAbsRequiredByteDelta,
                ref sumAbsClipByte,
                ref maxAbsRequiredByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
        }

        var correctedRawMetrics = CompareFloatDeltaRgb(
            (correctedR, correctedG, correctedB),
            shippedDecoded);
        var regions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];

        return new MorphContentPlausibilityStats(
            factor,
            sampleCount == 0 ? 0d : inRangeCount * 100d / sampleCount,
            sampleCount == 0 ? 0d : sumAbsRequiredByteDelta / sampleCount,
            maxAbsRequiredByteDelta,
            sampleCount == 0 ? 0d : sumAbsClipByte / sampleCount,
            maxAbsClipByte,
            correctedRawMetrics,
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }

    internal static MorphGainPlausibilityStats? ComputeMorphGainPlausibility(
        EgtParser egt,
        EgtMorph morph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(morph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        double numerator = 0d;
        double denominator = 0d;
        var pixelCount = egt.Cols * egt.Rows;
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            AccumulateMorphGainFit(
                morph.DeltaR[pixelIndex],
                shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex],
                factor,
                ref numerator,
                ref denominator);
            AccumulateMorphGainFit(
                morph.DeltaG[pixelIndex],
                shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex],
                factor,
                ref numerator,
                ref denominator);
            AccumulateMorphGainFit(
                morph.DeltaB[pixelIndex],
                shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex],
                factor,
                ref numerator,
                ref denominator);
        }

        if (denominator < 1e-12)
        {
            return null;
        }

        var gain = 1d + (numerator / denominator);
        var correctedR = new float[pixelCount];
        var correctedG = new float[pixelCount];
        var correctedB = new float[pixelCount];
        double sumAbsByteDelta = 0d;
        double sumAbsClipByte = 0d;
        var maxAbsByteDelta = 0f;
        var maxAbsClipByte = 0f;
        var inRangeCount = 0;
        var sampleCount = pixelCount * 3;

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            ApplyMorphGainCorrection(
                morph.DeltaR[pixelIndex],
                factor,
                gain,
                out correctedR[pixelIndex],
                currentNative.R[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphGainCorrection(
                morph.DeltaG[pixelIndex],
                factor,
                gain,
                out correctedG[pixelIndex],
                currentNative.G[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphGainCorrection(
                morph.DeltaB[pixelIndex],
                factor,
                gain,
                out correctedB[pixelIndex],
                currentNative.B[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
        }

        var correctedRawMetrics = CompareFloatDeltaRgb(
            (correctedR, correctedG, correctedB),
            shippedDecoded);
        var regions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];

        return new MorphGainPlausibilityStats(
            gain,
            sampleCount == 0 ? 0d : inRangeCount * 100d / sampleCount,
            sampleCount == 0 ? 0d : sumAbsByteDelta / sampleCount,
            maxAbsByteDelta,
            sampleCount == 0 ? 0d : sumAbsClipByte / sampleCount,
            maxAbsClipByte,
            correctedRawMetrics,
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }

    internal static MorphAffinePlausibilityStats? ComputeMorphAffinePlausibility(
        EgtParser egt,
        EgtMorph morph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(morph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        double sumX = 0d;
        double sumY = 0d;
        double sumXX = 0d;
        double sumXY = 0d;
        var sampleCount = egt.Cols * egt.Rows * 3;

        for (var pixelIndex = 0; pixelIndex < egt.Cols * egt.Rows; pixelIndex++)
        {
            AccumulateMorphAffineFit(
                morph.DeltaR[pixelIndex],
                shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumXY);
            AccumulateMorphAffineFit(
                morph.DeltaG[pixelIndex],
                shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumXY);
            AccumulateMorphAffineFit(
                morph.DeltaB[pixelIndex],
                shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumXY);
        }

        if (sampleCount == 0)
        {
            return null;
        }

        var count = (double)sampleCount;
        var meanX = sumX / count;
        var meanY = sumY / count;
        var varianceX = sumXX - (sumX * meanX);
        if (Math.Abs(varianceX) <= 1e-12)
        {
            return null;
        }

        var covarianceXY = sumXY - (sumX * meanY);
        var scale = covarianceXY / varianceX;
        var bias = meanY - (scale * meanX);

        var correctedR = new float[egt.Cols * egt.Rows];
        var correctedG = new float[egt.Cols * egt.Rows];
        var correctedB = new float[egt.Cols * egt.Rows];
        double sumAbsByteDelta = 0d;
        double sumAbsClipByte = 0d;
        var maxAbsByteDelta = 0f;
        var maxAbsClipByte = 0f;
        var inRangeCount = 0;

        for (var pixelIndex = 0; pixelIndex < egt.Cols * egt.Rows; pixelIndex++)
        {
            ApplyMorphAffineCorrection(
                morph.DeltaR[pixelIndex],
                factor,
                scale,
                bias,
                out correctedR[pixelIndex],
                currentNative.R[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphAffineCorrection(
                morph.DeltaG[pixelIndex],
                factor,
                scale,
                bias,
                out correctedG[pixelIndex],
                currentNative.G[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphAffineCorrection(
                morph.DeltaB[pixelIndex],
                factor,
                scale,
                bias,
                out correctedB[pixelIndex],
                currentNative.B[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
        }

        var correctedRawMetrics = CompareFloatDeltaRgb(
            (correctedR, correctedG, correctedB),
            shippedDecoded);
        var regions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];

        return new MorphAffinePlausibilityStats(
            scale,
            bias,
            inRangeCount * 100d / sampleCount,
            sumAbsByteDelta / sampleCount,
            maxAbsByteDelta,
            sumAbsClipByte / sampleCount,
            maxAbsClipByte,
            correctedRawMetrics,
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }
}
