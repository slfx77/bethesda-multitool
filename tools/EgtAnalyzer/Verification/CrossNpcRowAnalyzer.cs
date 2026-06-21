using BethesdaMultitool.Core.Formats.Nif.Rendering;
using static EgtAnalyzer.Verification.DeltaTextureHelpers;

namespace EgtAnalyzer.Verification;

internal static class CrossNpcRowAnalyzer
{
    internal static CrossNpcRequiredRow CreateCrossNpcRequiredRow(
        string sourcePath,
        int morphIndex,
        EgtMorph morph)
    {
        return new CrossNpcRequiredRow(
            morphIndex,
            morph.DeltaR,
            morph.DeltaG,
            morph.DeltaB,
            sourcePath);
    }

    internal static bool AreComparableRows(
        CrossNpcRequiredRow source,
        CrossNpcRequiredRow target)
    {
        return source.RequiredR.Length == target.RequiredR.Length &&
               source.RequiredG.Length == target.RequiredG.Length &&
               source.RequiredB.Length == target.RequiredB.Length;
    }

    internal static void CompareRowCandidate(
        sbyte[] required,
        sbyte[] candidate,
        int pixelCount,
        int cols,
        int rows,
        bool flipCandidate,
        out double mae,
        out double cosine)
    {
        double sumAbsDiff = 0d;
        double sumXY = 0d;
        double sumXX = 0d;
        double sumYY = 0d;

        for (var row = 0; row < rows; row++)
        {
            var candidateRow = flipCandidate ? (rows - 1 - row) : row;
            for (var col = 0; col < cols; col++)
            {
                var reqVal = (double)required[row * cols + col];
                var candVal = (double)candidate[candidateRow * cols + col];
                sumAbsDiff += Math.Abs(reqVal - candVal);
                sumXY += reqVal * candVal;
                sumXX += reqVal * reqVal;
                sumYY += candVal * candVal;
            }
        }

        mae = sumAbsDiff / pixelCount;
        cosine = (Math.Abs(sumXX) <= 1e-12 || Math.Abs(sumYY) <= 1e-12)
            ? 0d
            : sumXY / Math.Sqrt(sumXX * sumYY);
    }

    internal static double ComputeSbyteMae(sbyte[] a, sbyte[] b, int count)
    {
        double sum = 0d;
        for (var i = 0; i < count; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return sum / count;
    }

    internal static CrossNpcRequiredRowSimilarity ComputeCrossNpcRequiredRowSimilarity(
        CrossNpcRequiredRow source,
        CrossNpcRequiredRow target)
    {
        var channelLength = source.RequiredR.Length;
        var vectorLength = channelLength * 3;

        double dot = 0d;
        double sumSourceSq = 0d;
        double sumTargetSq = 0d;
        double sumSource = 0d;
        double sumTarget = 0d;
        double sumSourceTimesTarget = 0d;
        double sumAbs = 0d;

        static void AccumulateChannel(
            sbyte[] sourceChannel,
            sbyte[] targetChannel,
            ref double dot,
            ref double sumSourceSq,
            ref double sumTargetSq,
            ref double sumSource,
            ref double sumTarget,
            ref double sumSourceTimesTarget,
            ref double sumAbs)
        {
            for (var i = 0; i < sourceChannel.Length; i++)
            {
                var x = (double)sourceChannel[i];
                var y = targetChannel[i];
                dot += x * y;
                sumSourceSq += x * x;
                sumTargetSq += y * y;
                sumSource += x;
                sumTarget += y;
                sumSourceTimesTarget += x * y;
                sumAbs += Math.Abs(y - x);
            }
        }

        AccumulateChannel(source.RequiredR, target.RequiredR, ref dot, ref sumSourceSq, ref sumTargetSq, ref sumSource, ref sumTarget, ref sumSourceTimesTarget, ref sumAbs);
        AccumulateChannel(source.RequiredG, target.RequiredG, ref dot, ref sumSourceSq, ref sumTargetSq, ref sumSource, ref sumTarget, ref sumSourceTimesTarget, ref sumAbs);
        AccumulateChannel(source.RequiredB, target.RequiredB, ref dot, ref sumSourceSq, ref sumTargetSq, ref sumSource, ref sumTarget, ref sumSourceTimesTarget, ref sumAbs);

        var cosine = sumSourceSq <= 1e-12 || sumTargetSq <= 1e-12
            ? 0d
            : dot / Math.Sqrt(sumSourceSq * sumTargetSq);

        var count = (double)vectorLength;
        var meanSource = sumSource / count;
        var meanTarget = sumTarget / count;
        var varianceSource = sumSourceSq - (sumSource * meanSource);
        var varianceTarget = sumTargetSq - (sumTarget * meanTarget);
        var covariance = sumSourceTimesTarget - (sumSource * meanTarget);
        var correlation = varianceSource <= 1e-12 || varianceTarget <= 1e-12
            ? 0d
            : covariance / Math.Sqrt(varianceSource * varianceTarget);

        var meanAbsoluteDifference = sumAbs / count;
        var affineScale = 0d;
        var affineBias = meanTarget;
        var affineFitMae = meanAbsoluteDifference;
        if (Math.Abs(varianceSource) > 1e-12)
        {
            affineScale = covariance / varianceSource;
            affineBias = meanTarget - (affineScale * meanSource);

            double sumAffineAbs = 0d;

            static void AccumulateAffineError(
                sbyte[] sourceChannel,
                sbyte[] targetChannel,
                double affineScale,
                double affineBias,
                ref double sumAffineAbs)
            {
                for (var i = 0; i < sourceChannel.Length; i++)
                {
                    sumAffineAbs += Math.Abs((affineScale * sourceChannel[i]) + affineBias - targetChannel[i]);
                }
            }

            AccumulateAffineError(source.RequiredR, target.RequiredR, affineScale, affineBias, ref sumAffineAbs);
            AccumulateAffineError(source.RequiredG, target.RequiredG, affineScale, affineBias, ref sumAffineAbs);
            AccumulateAffineError(source.RequiredB, target.RequiredB, affineScale, affineBias, ref sumAffineAbs);
            affineFitMae = sumAffineAbs / count;
        }

        return new CrossNpcRequiredRowSimilarity(
            cosine,
            correlation,
            meanAbsoluteDifference,
            affineFitMae,
            affineScale,
            affineBias);
    }

    internal static ExternalDonorApplyStats? ComputeExternalDonorApplyStats(
        InspectNpcState npcState,
        InspectMorphState sourceMorphState,
        EgtMorph donorMorph)
    {
        if (Math.Abs(sourceMorphState.Factor) <= 1e-9f)
        {
            return null;
        }

        var pixelCount = npcState.Cols * npcState.Rows;
        if (sourceMorphState.SourceMorph.DeltaR.Length != pixelCount ||
            donorMorph.DeltaR.Length != pixelCount ||
            sourceMorphState.SourceMorph.DeltaG.Length != pixelCount ||
            donorMorph.DeltaG.Length != pixelCount ||
            sourceMorphState.SourceMorph.DeltaB.Length != pixelCount ||
            donorMorph.DeltaB.Length != pixelCount)
        {
            return null;
        }

        var correctedR = new float[pixelCount];
        var correctedG = new float[pixelCount];
        var correctedB = new float[pixelCount];
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            correctedR[pixelIndex] = npcState.CurrentNative.R[pixelIndex] +
                ((donorMorph.DeltaR[pixelIndex] - sourceMorphState.SourceMorph.DeltaR[pixelIndex]) * sourceMorphState.Factor);
            correctedG[pixelIndex] = npcState.CurrentNative.G[pixelIndex] +
                ((donorMorph.DeltaG[pixelIndex] - sourceMorphState.SourceMorph.DeltaG[pixelIndex]) * sourceMorphState.Factor);
            correctedB[pixelIndex] = npcState.CurrentNative.B[pixelIndex] +
                ((donorMorph.DeltaB[pixelIndex] - sourceMorphState.SourceMorph.DeltaB[pixelIndex]) * sourceMorphState.Factor);
        }

        var corrected = (correctedR, correctedG, correctedB);
        var rawMetrics = CompareFloatDeltaRgb(corrected, npcState.ShippedDecoded);
        var regions = GetNamedRegions(npcState.Cols, npcState.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];

        return new ExternalDonorApplyStats(
            rawMetrics,
            GetRegionRawMae(corrected, npcState.ShippedDecoded, npcState.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            GetRegionRawMae(corrected, npcState.ShippedDecoded, npcState.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }

    internal static string NormalizeArchiveVirtualPath(string path)
    {
        return path
            .Replace('/', '\\')
            .TrimStart('\\');
    }
}
