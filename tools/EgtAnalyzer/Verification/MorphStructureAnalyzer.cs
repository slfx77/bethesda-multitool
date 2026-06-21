using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using static EgtAnalyzer.Verification.DeltaTextureHelpers;

namespace EgtAnalyzer.Verification;

internal static class MorphStructureAnalyzer
{
    private const int TopResidualProjectionCount = 10;

    internal static MorphContributionStats ComputeMorphContributionStats(
        EgtParser egt,
        EgtMorph morph,
        float contributionFactor)
    {
        var wholeR = ComputeWholeChannelAbsStats(morph.DeltaR, contributionFactor);
        var wholeG = ComputeWholeChannelAbsStats(morph.DeltaG, contributionFactor);
        var wholeB = ComputeWholeChannelAbsStats(morph.DeltaB, contributionFactor);
        var eyes = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "eyes");
        var mouth = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "mouth");

        return new MorphContributionStats(
            wholeR.MeanAbs,
            wholeG.MeanAbs,
            wholeB.MeanAbs,
            wholeR.MaxAbs,
            wholeG.MaxAbs,
            wholeB.MaxAbs,
            ComputeRegionMeanAbsRgb(morph, contributionFactor, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            ComputeRegionMeanAbsRgb(morph, contributionFactor, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }

    internal static (float MeanAbs, float MaxAbs) ComputeWholeChannelAbsStats(
        sbyte[] channel,
        float contributionFactor)
    {
        if (channel.Length == 0 || contributionFactor == 0f)
        {
            return (0f, 0f);
        }

        double sumAbs = 0;
        var maxAbs = 0f;
        for (var index = 0; index < channel.Length; index++)
        {
            var value = MathF.Abs(channel[index] * contributionFactor);
            sumAbs += value;
            if (value > maxAbs)
            {
                maxAbs = value;
            }
        }

        return ((float)(sumAbs / channel.Length), maxAbs);
    }

    internal static float ComputeRegionMeanAbsRgb(
        EgtMorph morph,
        float contributionFactor,
        int width,
        int x,
        int y,
        int regionWidth,
        int regionHeight)
    {
        if (contributionFactor == 0f || regionWidth <= 0 || regionHeight <= 0)
        {
            return 0f;
        }

        double sumAbs = 0;
        var samples = 0;
        for (var row = y; row < y + regionHeight; row++)
        {
            for (var col = x; col < x + regionWidth; col++)
            {
                var index = row * width + col;
                sumAbs += MathF.Abs(morph.DeltaR[index] * contributionFactor);
                sumAbs += MathF.Abs(morph.DeltaG[index] * contributionFactor);
                sumAbs += MathF.Abs(morph.DeltaB[index] * contributionFactor);
                samples += 3;
            }
        }

        return samples == 0 ? 0f : (float)(sumAbs / samples);
    }

    internal static MorphResidualAlignmentStats ComputeMorphResidualAlignment(
        EgtParser egt,
        EgtMorph morph,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(morph.Scale * 256f);
        var basisFactor = scale256 / 65536f;
        if (basisFactor == 0f)
        {
            return new MorphResidualAlignmentStats(0, 0, 0, 0, 0, 0);
        }

        var eyes = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "eyes");
        var mouth = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "mouth");

        var whole = ComputeResidualProjectionStats(
            morph,
            basisFactor,
            egt.Cols,
            0,
            0,
            egt.Cols,
            egt.Rows,
            currentNative,
            shippedDecoded);
        var eyesStats = ComputeResidualProjectionStats(
            morph,
            basisFactor,
            egt.Cols,
            eyes.X,
            eyes.Y,
            eyes.W,
            eyes.H,
            currentNative,
            shippedDecoded);
        var mouthStats = ComputeResidualProjectionStats(
            morph,
            basisFactor,
            egt.Cols,
            mouth.X,
            mouth.Y,
            mouth.W,
            mouth.H,
            currentNative,
            shippedDecoded);

        return new MorphResidualAlignmentStats(
            whole.Projection256,
            eyesStats.Projection256,
            mouthStats.Projection256,
            whole.Cosine,
            eyesStats.Cosine,
            mouthStats.Cosine);
    }

    internal static void DumpMorphStructureSummary(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var regions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];
        var nose = regions["nose"];
        var forehead = regions["forehead"];
        var rows = new List<MorphStructureRow>(egt.SymmetricMorphs.Length);

        for (var morphIndex = 0; morphIndex < egt.SymmetricMorphs.Length; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            var basisFactor = scale256 / 65536f;
            var stats = ComputeMorphContributionStats(egt, morph, basisFactor);
            var residualAlignment = ComputeMorphResidualAlignment(egt, morph, currentNative, shippedDecoded);
            var current256 = morphIndex < currentCoefficients.Length
                ? (int)(currentCoefficients[morphIndex] * 256f)
                : 0;
            var wholeAbsMeanRgb = (stats.WholeMeanAbsR + stats.WholeMeanAbsG + stats.WholeMeanAbsB) / 3f;
            var noseAbsMeanRgb = ComputeRegionMeanAbsRgb(
                morph,
                basisFactor,
                egt.Cols,
                nose.X,
                nose.Y,
                nose.W,
                nose.H);
            var foreheadAbsMeanRgb = ComputeRegionMeanAbsRgb(
                morph,
                basisFactor,
                egt.Cols,
                forehead.X,
                forehead.Y,
                forehead.W,
                forehead.H);

            rows.Add(new MorphStructureRow(
                morphIndex,
                current256,
                scale256,
                wholeAbsMeanRgb,
                stats.EyesMeanAbsRgb,
                stats.MouthMeanAbsRgb,
                noseAbsMeanRgb,
                foreheadAbsMeanRgb,
                residualAlignment.WholeProjection256,
                residualAlignment.EyesProjection256,
                residualAlignment.MouthProjection256,
                residualAlignment.WholeCosine,
                residualAlignment.EyesCosine,
                residualAlignment.MouthCosine));
        }

        Console.WriteLine($"  MORPH-STRUCTURE-TOP 0x{appearance.NpcFormId:X8}:");
        foreach (var row in rows
                     .OrderByDescending(item => item.FaceLocalizedRatio)
                     .ThenByDescending(item => item.WholeAbsMeanRgb)
                     .ThenBy(item => item.Index)
                     .Take(12))
        {
            Console.WriteLine(
                $"    [{row.Index:D2}] current256={row.Current256,6} scale256={row.Scale256,4} " +
                $"whole={row.WholeAbsMeanRgb,7:F4} eyes={row.EyesAbsMeanRgb,7:F4} mouth={row.MouthAbsMeanRgb,7:F4} " +
                $"ratio={row.FaceLocalizedRatio,6:F2} projW={row.WholeProjection256,8:F1} " +
                $"projE={row.EyesProjection256,8:F1} projM={row.MouthProjection256,8:F1}");
        }

        Console.WriteLine($"  MORPH-STRUCTURE-ALL 0x{appearance.NpcFormId:X8}:");
        foreach (var row in rows.OrderBy(item => item.Index))
        {
            Console.WriteLine(
                $"    [{row.Index:D2}] current256={row.Current256,6} scale256={row.Scale256,4} " +
                $"whole={row.WholeAbsMeanRgb,7:F4} eyes={row.EyesAbsMeanRgb,7:F4} mouth={row.MouthAbsMeanRgb,7:F4} " +
                $"nose={row.NoseAbsMeanRgb,7:F4} forehead={row.ForeheadAbsMeanRgb,7:F4} ratio={row.FaceLocalizedRatio,6:F2} " +
                $"cosW={row.WholeCosine,6:F3} cosE={row.EyesCosine,6:F3} cosM={row.MouthCosine,6:F3}");
        }
    }

    internal static ResidualProjectionStats ComputeResidualProjectionStats(
        EgtMorph morph,
        float basisFactor,
        int width,
        int x,
        int y,
        int regionWidth,
        int regionHeight,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        double dotBasisResidual = 0;
        double dotBasisBasis = 0;
        double dotResidualResidual = 0;

        for (var row = y; row < y + regionHeight; row++)
        {
            for (var col = x; col < x + regionWidth; col++)
            {
                var index = row * width + col;
                var basisR = morph.DeltaR[index] * basisFactor;
                var basisG = morph.DeltaG[index] * basisFactor;
                var basisB = morph.DeltaB[index] * basisFactor;

                var residualR = shippedDecoded.R[index] - currentNative.R[index];
                var residualG = shippedDecoded.G[index] - currentNative.G[index];
                var residualB = shippedDecoded.B[index] - currentNative.B[index];

                dotBasisResidual += (basisR * residualR) + (basisG * residualG) + (basisB * residualB);
                dotBasisBasis += (basisR * basisR) + (basisG * basisG) + (basisB * basisB);
                dotResidualResidual += (residualR * residualR) + (residualG * residualG) + (residualB * residualB);
            }
        }

        if (dotBasisBasis <= 0d)
        {
            return new ResidualProjectionStats(0, 0);
        }

        var projection256 = dotBasisResidual / dotBasisBasis;
        var cosine = dotResidualResidual <= 0d
            ? 0d
            : dotBasisResidual / Math.Sqrt(dotBasisBasis * dotResidualResidual);

        return new ResidualProjectionStats(projection256, cosine);
    }

    internal static IReadOnlyList<ResidualProjectionRow> DumpResidualProjectionSummary(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var eyes = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "eyes");
        var mouth = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "mouth");
        var whole = (Name: "whole", X: 0, Y: 0, W: egt.Cols, H: egt.Rows);
        var rows = new List<ResidualProjectionRow>();

        for (var morphIndex = 0; morphIndex < egt.SymmetricMorphs.Length; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            if (scale256 == 0)
            {
                continue;
            }

            var current256 = morphIndex < currentCoefficients.Length
                ? (int)(currentCoefficients[morphIndex] * 256f)
                : 0;
            var wholeDelta256 = SolveRegionCoefficientDelta256(
                morph,
                scale256,
                egt.Cols,
                currentNative,
                shippedDecoded,
                whole.X,
                whole.Y,
                whole.W,
                whole.H);
            var eyesDelta256 = SolveRegionCoefficientDelta256(
                morph,
                scale256,
                egt.Cols,
                currentNative,
                shippedDecoded,
                eyes.X,
                eyes.Y,
                eyes.W,
                eyes.H);
            var mouthDelta256 = SolveRegionCoefficientDelta256(
                morph,
                scale256,
                egt.Cols,
                currentNative,
                shippedDecoded,
                mouth.X,
                mouth.Y,
                mouth.W,
                mouth.H);

            rows.Add(new ResidualProjectionRow(
                morphIndex,
                current256,
                wholeDelta256,
                eyesDelta256,
                mouthDelta256));
        }

        Console.WriteLine($"  RAWRESID-PROJ 0x{appearance.NpcFormId:X8}:");
        foreach (var row in rows
                     .OrderByDescending(item => item.MaxAbsDelta256)
                     .ThenBy(item => item.MorphIndex)
                     .Take(TopResidualProjectionCount))
        {
            Console.WriteLine(
                $"    [{row.MorphIndex:D2}] current256={row.Current256,6} " +
                $"wholeΔ={row.WholeDelta256,6} eyesΔ={row.EyesDelta256,6} mouthΔ={row.MouthDelta256,6} " +
                $"dominant={row.DominantRegion}");
        }

        return rows;
    }

    internal static int SolveRegionCoefficientDelta256(
        EgtMorph morph,
        int scale256,
        int width,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded,
        int x,
        int y,
        int regionWidth,
        int regionHeight)
    {
        var basisScale = scale256 / 65536f;
        double numerator = 0;
        double denominator = 0;

        for (var row = y; row < y + regionHeight; row++)
        {
            for (var col = x; col < x + regionWidth; col++)
            {
                var index = row * width + col;
                var basisR = morph.DeltaR[index] * basisScale;
                var basisG = morph.DeltaG[index] * basisScale;
                var basisB = morph.DeltaB[index] * basisScale;
                var residualR = shippedDecoded.R[index] - currentNative.R[index];
                var residualG = shippedDecoded.G[index] - currentNative.G[index];
                var residualB = shippedDecoded.B[index] - currentNative.B[index];

                numerator += (residualR * basisR) + (residualG * basisG) + (residualB * basisB);
                denominator += (basisR * basisR) + (basisG * basisG) + (basisB * basisB);
            }
        }

        if (denominator < 1e-12)
        {
            return 0;
        }

        return (int)Math.Round(numerator / denominator, MidpointRounding.AwayFromZero);
    }
}
