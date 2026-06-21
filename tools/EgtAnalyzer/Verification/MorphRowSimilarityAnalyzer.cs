using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using static EgtAnalyzer.Verification.MorphCorrectionHelpers;

namespace EgtAnalyzer.Verification;

internal static class MorphRowSimilarityAnalyzer
{
    private const int MorphInspectionRowSampleCount = 16;

    internal static MorphRowSimilarityStats? ComputeMorphRowSimilarityStats(
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

        return ComputeMorphRowSimilarityStatsCore(
            egt,
            morph,
            morph,
            factor,
            currentNative,
            shippedDecoded);
    }

    internal static MorphNearestOtherRowStats? ComputeMorphNearestOtherRowStats(
        EgtParser egt,
        int sourceMorphIndex,
        EgtMorph sourceMorph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(sourceMorph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        MorphNearestOtherRowStats? best = null;
        for (var candidateIndex = 0; candidateIndex < egt.SymmetricMorphs.Length; candidateIndex++)
        {
            if (candidateIndex == sourceMorphIndex)
            {
                continue;
            }

            var candidateStats = ComputeMorphRowSimilarityStatsCore(
                egt,
                sourceMorph,
                egt.SymmetricMorphs[candidateIndex],
                factor,
                currentNative,
                shippedDecoded);
            if (candidateStats == null)
            {
                continue;
            }

            if (best == null || candidateStats.AffineFitMae < best.Stats.AffineFitMae)
            {
                best = new MorphNearestOtherRowStats(candidateIndex, candidateStats);
            }
        }

        return best;
    }

    internal static MorphNearestOtherRowPerChannelStats? ComputeMorphNearestOtherRowPerChannelStats(
        EgtParser egt,
        int sourceMorphIndex,
        EgtMorph sourceMorph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(sourceMorph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        var selfRed = ComputeMorphChannelSimilarityStatsCore(
            sourceMorph.DeltaR,
            sourceMorph.DeltaR,
            currentNative.R,
            shippedDecoded.R,
            factor);
        var selfGreen = ComputeMorphChannelSimilarityStatsCore(
            sourceMorph.DeltaG,
            sourceMorph.DeltaG,
            currentNative.G,
            shippedDecoded.G,
            factor);
        var selfBlue = ComputeMorphChannelSimilarityStatsCore(
            sourceMorph.DeltaB,
            sourceMorph.DeltaB,
            currentNative.B,
            shippedDecoded.B,
            factor);
        if (selfRed == null || selfGreen == null || selfBlue == null)
        {
            return null;
        }

        MorphNearestOtherChannelCandidate? bestRed = null;
        MorphNearestOtherChannelCandidate? bestGreen = null;
        MorphNearestOtherChannelCandidate? bestBlue = null;

        for (var candidateIndex = 0; candidateIndex < egt.SymmetricMorphs.Length; candidateIndex++)
        {
            if (candidateIndex == sourceMorphIndex)
            {
                continue;
            }

            var candidateMorph = egt.SymmetricMorphs[candidateIndex];
            var redStats = ComputeMorphChannelSimilarityStatsCore(
                sourceMorph.DeltaR,
                candidateMorph.DeltaR,
                currentNative.R,
                shippedDecoded.R,
                factor);
            var greenStats = ComputeMorphChannelSimilarityStatsCore(
                sourceMorph.DeltaG,
                candidateMorph.DeltaG,
                currentNative.G,
                shippedDecoded.G,
                factor);
            var blueStats = ComputeMorphChannelSimilarityStatsCore(
                sourceMorph.DeltaB,
                candidateMorph.DeltaB,
                currentNative.B,
                shippedDecoded.B,
                factor);

            if (redStats != null)
            {
                var vsSelf = selfRed.AffineFitMae <= 1e-9
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (redStats.AffineFitMae / selfRed.AffineFitMae)));
                var candidate = new MorphNearestOtherChannelCandidate(candidateIndex, redStats, vsSelf);
                if (bestRed == null || candidate.Stats.AffineFitMae < bestRed.Stats.AffineFitMae)
                {
                    bestRed = candidate;
                }
            }

            if (greenStats != null)
            {
                var vsSelf = selfGreen.AffineFitMae <= 1e-9
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (greenStats.AffineFitMae / selfGreen.AffineFitMae)));
                var candidate = new MorphNearestOtherChannelCandidate(candidateIndex, greenStats, vsSelf);
                if (bestGreen == null || candidate.Stats.AffineFitMae < bestGreen.Stats.AffineFitMae)
                {
                    bestGreen = candidate;
                }
            }

            if (blueStats != null)
            {
                var vsSelf = selfBlue.AffineFitMae <= 1e-9
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (blueStats.AffineFitMae / selfBlue.AffineFitMae)));
                var candidate = new MorphNearestOtherChannelCandidate(candidateIndex, blueStats, vsSelf);
                if (bestBlue == null || candidate.Stats.AffineFitMae < bestBlue.Stats.AffineFitMae)
                {
                    bestBlue = candidate;
                }
            }
        }

        if (bestRed == null || bestGreen == null || bestBlue == null)
        {
            return null;
        }

        var mixedCandidate = new EgtMorph
        {
            Scale = sourceMorph.Scale,
            DeltaR = egt.SymmetricMorphs[bestRed.MorphIndex].DeltaR,
            DeltaG = egt.SymmetricMorphs[bestGreen.MorphIndex].DeltaG,
            DeltaB = egt.SymmetricMorphs[bestBlue.MorphIndex].DeltaB,
        };
        var mixedStats = ComputeMorphRowSimilarityStatsCore(
            egt,
            sourceMorph,
            mixedCandidate,
            factor,
            currentNative,
            shippedDecoded);
        if (mixedStats == null)
        {
            return null;
        }

        return new MorphNearestOtherRowPerChannelStats(
            bestRed,
            bestGreen,
            bestBlue,
            mixedStats);
    }

    internal static MorphRowSimilarityStats? ComputeMorphRowSimilarityStatsCore(
        EgtParser egt,
        EgtMorph sourceMorph,
        EgtMorph candidateMorph,
        float factor,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {

        double sumX = 0d;
        double sumY = 0d;
        double sumXX = 0d;
        double sumYY = 0d;
        double sumXY = 0d;
        var sampleCount = egt.Cols * egt.Rows * 3;

        for (var pixelIndex = 0; pixelIndex < egt.Cols * egt.Rows; pixelIndex++)
        {
            AccumulateMorphRowSample(
                sourceMorph.DeltaR[pixelIndex],
                candidateMorph.DeltaR[pixelIndex],
                shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumYY,
                ref sumXY);
            AccumulateMorphRowSample(
                sourceMorph.DeltaG[pixelIndex],
                candidateMorph.DeltaG[pixelIndex],
                shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumYY,
                ref sumXY);
            AccumulateMorphRowSample(
                sourceMorph.DeltaB[pixelIndex],
                candidateMorph.DeltaB[pixelIndex],
                shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumYY,
                ref sumXY);
        }

        if (sampleCount == 0 || Math.Abs(sumXX) <= 1e-12 || Math.Abs(sumYY) <= 1e-12)
        {
            return null;
        }

        var count = (double)sampleCount;
        var meanX = sumX / count;
        var meanY = sumY / count;
        var covarianceXY = sumXY - (sumX * meanY);
        var varianceX = sumXX - (sumX * meanX);
        var varianceY = sumYY - (sumY * meanY);
        var gain = sumXY / sumXX;
        var affineScale = Math.Abs(varianceX) <= 1e-12 ? 0d : covarianceXY / varianceX;
        var affineBias = meanY - (affineScale * meanX);
        var cosine = sumXY / Math.Sqrt(sumXX * sumYY);
        var correlation = Math.Abs(varianceX) <= 1e-12 || Math.Abs(varianceY) <= 1e-12
            ? 0d
            : covarianceXY / Math.Sqrt(varianceX * varianceY);

        double targetMae = 0d;
        double gainFitMae = 0d;
        double affineFitMae = 0d;

        for (var pixelIndex = 0; pixelIndex < egt.Cols * egt.Rows; pixelIndex++)
        {
            AccumulateMorphRowSimilarityResidual(
                sourceMorph.DeltaR[pixelIndex],
                candidateMorph.DeltaR[pixelIndex],
                shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex],
                factor,
                gain,
                affineScale,
                affineBias,
                ref targetMae,
                ref gainFitMae,
                ref affineFitMae);
            AccumulateMorphRowSimilarityResidual(
                sourceMorph.DeltaG[pixelIndex],
                candidateMorph.DeltaG[pixelIndex],
                shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex],
                factor,
                gain,
                affineScale,
                affineBias,
                ref targetMae,
                ref gainFitMae,
                ref affineFitMae);
            AccumulateMorphRowSimilarityResidual(
                sourceMorph.DeltaB[pixelIndex],
                candidateMorph.DeltaB[pixelIndex],
                shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex],
                factor,
                gain,
                affineScale,
                affineBias,
                ref targetMae,
                ref gainFitMae,
                ref affineFitMae);
        }

        targetMae /= sampleCount;
        gainFitMae /= sampleCount;
        affineFitMae /= sampleCount;

        return new MorphRowSimilarityStats(
            cosine,
            correlation,
            targetMae,
            gainFitMae,
            affineFitMae,
            targetMae <= 1e-9 ? 0d : Math.Max(0d, 100d * (1d - (gainFitMae / targetMae))),
            targetMae <= 1e-9 ? 0d : Math.Max(0d, 100d * (1d - (affineFitMae / targetMae))),
            gain,
            affineScale,
            affineBias);
    }

    internal static MorphRowSimilarityStats? ComputeMorphChannelRowSimilarityStatsCore(
        sbyte[] sourceDelta,
        sbyte[] candidateDelta,
        float factor,
        float[] currentChannel,
        float[] shippedChannel)
    {
        var channelStats = ComputeMorphChannelSimilarityStatsCore(
            sourceDelta,
            candidateDelta,
            currentChannel,
            shippedChannel,
            factor);
        if (channelStats == null)
        {
            return null;
        }

        return new MorphRowSimilarityStats(
            channelStats.Cosine,
            channelStats.Correlation,
            channelStats.TargetMae,
            0d,
            channelStats.AffineFitMae,
            0d,
            channelStats.AffineExplainedPercent,
            0d,
            channelStats.AffineScale,
            channelStats.AffineBias);
    }

    internal static MorphChannelSimilarityStats? ComputeMorphChannelSimilarityStatsCore(
        sbyte[] sourceDelta,
        sbyte[] candidateDelta,
        float[] currentChannel,
        float[] shippedChannel,
        float factor)
    {
        if (sourceDelta.Length != candidateDelta.Length ||
            sourceDelta.Length != currentChannel.Length ||
            sourceDelta.Length != shippedChannel.Length)
        {
            return null;
        }

        double sumX = 0d;
        double sumY = 0d;
        double sumXX = 0d;
        double sumYY = 0d;
        double sumXY = 0d;
        var sampleCount = sourceDelta.Length;

        for (var pixelIndex = 0; pixelIndex < sampleCount; pixelIndex++)
        {
            var x = (double)candidateDelta[pixelIndex];
            var y = sourceDelta[pixelIndex] + ((shippedChannel[pixelIndex] - currentChannel[pixelIndex]) / factor);
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumYY += y * y;
            sumXY += x * y;
        }

        if (sampleCount == 0 || Math.Abs(sumXX) <= 1e-12 || Math.Abs(sumYY) <= 1e-12)
        {
            return null;
        }

        var count = (double)sampleCount;
        var meanX = sumX / count;
        var meanY = sumY / count;
        var covarianceXY = sumXY - (sumX * meanY);
        var varianceX = sumXX - (sumX * meanX);
        var varianceY = sumYY - (sumY * meanY);
        var affineScale = Math.Abs(varianceX) <= 1e-12 ? 0d : covarianceXY / varianceX;
        var affineBias = meanY - (affineScale * meanX);
        var cosine = sumXY / Math.Sqrt(sumXX * sumYY);
        var correlation = Math.Abs(varianceX) <= 1e-12 || Math.Abs(varianceY) <= 1e-12
            ? 0d
            : covarianceXY / Math.Sqrt(varianceX * varianceY);

        double targetMae = 0d;
        double affineFitMae = 0d;
        for (var pixelIndex = 0; pixelIndex < sampleCount; pixelIndex++)
        {
            var x = (double)candidateDelta[pixelIndex];
            var y = sourceDelta[pixelIndex] + ((shippedChannel[pixelIndex] - currentChannel[pixelIndex]) / factor);
            targetMae += Math.Abs(y - x);
            affineFitMae += Math.Abs(y - ((affineScale * x) + affineBias));
        }

        targetMae /= sampleCount;
        affineFitMae /= sampleCount;

        return new MorphChannelSimilarityStats(
            cosine,
            correlation,
            targetMae,
            affineFitMae,
            targetMae <= 1e-9 ? 0d : Math.Max(0d, 100d * (1d - (affineFitMae / targetMae))),
            affineScale,
            affineBias);
    }

    internal static void DumpMorphChannelInspection(
        string label,
        byte[] rawEgtData,
        int channelOffset,
        int rowStride,
        int cols,
        int rows,
        sbyte[] parsedChannel)
    {
        var sampleCount = Math.Min(cols, MorphInspectionRowSampleCount);
        var rawTop = ReadRawChannelRowBytes(rawEgtData, channelOffset, rowStride, 0, sampleCount);
        var rawBottom = ReadRawChannelRowBytes(rawEgtData, channelOffset, rowStride, rows - 1, sampleCount);
        var parsedTop = ReadParsedChannelRow(parsedChannel, cols, 0, sampleCount);
        var parsedBottom = ReadParsedChannelRow(parsedChannel, cols, rows - 1, sampleCount);
        var topMatches = RawFileRowMatchesParsed(rawEgtData, channelOffset, rowStride, 0, parsedChannel, cols, rows - 1);
        var bottomMatches = RawFileRowMatchesParsed(rawEgtData, channelOffset, rowStride, rows - 1, parsedChannel, cols, 0);

        Console.WriteLine($"         {label} rawTop[{sampleCount}]      = {FormatByteSamples(rawTop)}");
        Console.WriteLine($"         {label} rawBottom[{sampleCount}]   = {FormatByteSamples(rawBottom)}");
        Console.WriteLine($"         {label} parsedTop[{sampleCount}]   = {FormatSbyteSamples(parsedTop)}");
        Console.WriteLine($"         {label} parsedBottom[{sampleCount}] = {FormatSbyteSamples(parsedBottom)}");
        Console.WriteLine(
            $"         {label} map rawTop->parsedBottom={topMatches} rawBottom->parsedTop={bottomMatches}");
    }

    private static byte[] ReadRawChannelRowBytes(
        byte[] rawEgtData,
        int channelOffset,
        int rowStride,
        int fileRow,
        int sampleCount)
    {
        var rowOffset = channelOffset + (fileRow * rowStride);
        var result = new byte[sampleCount];
        Array.Copy(rawEgtData, rowOffset, result, 0, sampleCount);
        return result;
    }

    private static sbyte[] ReadParsedChannelRow(
        sbyte[] parsedChannel,
        int cols,
        int row,
        int sampleCount)
    {
        var rowOffset = row * cols;
        var result = new sbyte[sampleCount];
        Array.Copy(parsedChannel, rowOffset, result, 0, sampleCount);
        return result;
    }

    private static bool RawFileRowMatchesParsed(
        byte[] rawEgtData,
        int channelOffset,
        int rowStride,
        int fileRow,
        sbyte[] parsedChannel,
        int cols,
        int parsedRow)
    {
        var fileOffset = channelOffset + (fileRow * rowStride);
        var parsedOffset = parsedRow * cols;
        for (var index = 0; index < cols; index++)
        {
            if (unchecked((sbyte)rawEgtData[fileOffset + index]) != parsedChannel[parsedOffset + index])
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatByteSamples(IEnumerable<byte> values)
    {
        return string.Join(" ", values.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string FormatSbyteSamples(IEnumerable<sbyte> values)
    {
        return string.Join(" ", values.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }
}
