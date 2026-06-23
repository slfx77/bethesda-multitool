using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using static EgtAnalyzer.Verification.CrossNpcRowAnalyzer;
using static EgtAnalyzer.Verification.LinearAlgebraUtils;

namespace EgtAnalyzer.Verification;

internal static class ExternalEgtDonorAnalyzer
{
    internal static IReadOnlyList<string> EnumerateExternalHeadEgtPaths(MeshArchiveSet meshArchives)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archivePath in meshArchives.ArchivePaths)
        {
            var archive = BsaParser.Parse(archivePath);
            foreach (var file in archive.AllFiles)
            {
                var normalized = NormalizeArchiveVirtualPath(file.FullPath);
                if (!normalized.EndsWith(".egt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = Path.GetFileName(normalized);
                if (fileName?.Contains("head", StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                discovered.Add(normalized);
            }
        }

        return discovered.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static ExternalHeadEgtRowMatch? FindBestExternalHeadEgtRowMatch(
        CrossNpcRequiredRow sourceRow,
        IReadOnlyList<ExternalHeadEgtCandidate> candidates,
        int morphIndex)
    {
        CrossNpcRequiredRowSimilarity? bestStats = null;
        ExternalHeadEgtCandidate? bestCandidate = null;

        foreach (var candidate in candidates)
        {
            if (morphIndex < 0 || morphIndex >= candidate.Egt.SymmetricMorphs.Length)
            {
                continue;
            }

            var candidateRow = CreateCrossNpcRequiredRow(candidate.Path, morphIndex, candidate.Egt.SymmetricMorphs[morphIndex]);
            if (!AreComparableRows(sourceRow, candidateRow))
            {
                continue;
            }

            var stats = ComputeCrossNpcRequiredRowSimilarity(sourceRow, candidateRow);
            if (bestStats == null || stats.AffineFitMae < bestStats.AffineFitMae)
            {
                bestStats = stats;
                bestCandidate = candidate;
            }
        }

        return bestStats == null || bestCandidate == null
            ? null
            : new ExternalHeadEgtRowMatch(
                Path.GetFileName(bestCandidate.Path) ?? bestCandidate.Path,
                bestCandidate.Path,
                morphIndex,
                bestStats,
                bestCandidate.Egt.SymmetricMorphs[morphIndex]);
    }

    internal static ExternalHeadEgtRowMatch? FindBestExternalHeadEgtRowMatch(
        CrossNpcRequiredRow sourceRow,
        IReadOnlyList<ExternalHeadEgtCandidate> candidates,
        IReadOnlyList<int> morphIndices)
    {
        ExternalHeadEgtRowMatch? bestMatch = null;

        foreach (var morphIndex in morphIndices)
        {
            var candidateMatch = FindBestExternalHeadEgtRowMatch(sourceRow, candidates, morphIndex);
            if (candidateMatch == null)
            {
                continue;
            }

            if (bestMatch == null || candidateMatch.Stats.AffineFitMae < bestMatch.Stats.AffineFitMae)
            {
                bestMatch = candidateMatch;
            }
        }

        return bestMatch;
    }

    internal static ExternalHeadEgtCandidate? FindExternalHeadEgtCandidateByFileName(
        IReadOnlyList<ExternalHeadEgtCandidate> candidates,
        string fileName)
    {
        foreach (var candidate in candidates)
        {
            var candidateFileName = Path.GetFileName(candidate.Path);
            if (string.Equals(candidateFileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static ExternalDonorBlendStats? ComputeExternalDonorBlendStats(
        InspectNpcState npcState,
        InspectMorphState sourceMorphState,
        CrossNpcRequiredRow sourceRow,
        ExternalHeadEgtRowMatch donorA,
        ExternalHeadEgtRowMatch donorB,
        bool includeBias)
    {
        var fit = FitExternalDonorBlendRow(sourceRow, donorA.Morph, donorB.Morph, includeBias);
        if (fit == null)
        {
            return null;
        }

        var blendedMorph = new EgtMorph
        {
            Scale = sourceMorphState.SourceMorph.Scale,
            DeltaR = fit.DeltaR,
            DeltaG = fit.DeltaG,
            DeltaB = fit.DeltaB
        };
        var applyStats = ComputeExternalDonorApplyStats(npcState, sourceMorphState, blendedMorph);
        return applyStats == null
            ? null
            : new ExternalDonorBlendStats(
                fit.CoefficientA,
                fit.CoefficientB,
                fit.Bias,
                fit.RowMae,
                applyStats);
    }

    internal static ExternalDonorBlendStats? ComputeExternalDonorBlendApplyStats(
        InspectNpcState npcState,
        InspectMorphState sourceMorphState,
        ExternalDonorBlendFit fit)
    {
        var blendedMorph = new EgtMorph
        {
            Scale = sourceMorphState.SourceMorph.Scale,
            DeltaR = fit.DeltaR,
            DeltaG = fit.DeltaG,
            DeltaB = fit.DeltaB
        };
        var applyStats = ComputeExternalDonorApplyStats(npcState, sourceMorphState, blendedMorph);
        return applyStats == null
            ? null
            : new ExternalDonorBlendStats(
                fit.CoefficientA,
                fit.CoefficientB,
                fit.Bias,
                fit.RowMae,
                applyStats);
    }

    internal static ExternalDonorBlendFit? FitExternalDonorBlendRow(
        CrossNpcRequiredRow sourceRow,
        EgtMorph donorA,
        EgtMorph donorB,
        bool includeBias)
    {
        return FitExternalDonorBlendRows([sourceRow], donorA, donorB, includeBias);
    }

    internal static ExternalDonorBlendFit? FitExternalDonorBlendRows(
        IReadOnlyList<CrossNpcRequiredRow> sourceRows,
        EgtMorph donorA,
        EgtMorph donorB,
        bool includeBias)
    {
        if (sourceRows.Count == 0)
        {
            return null;
        }

        var pixelCount = sourceRows[0].RequiredR.Length;
        if (sourceRows[0].RequiredG.Length != pixelCount ||
            sourceRows[0].RequiredB.Length != pixelCount ||
            donorA.DeltaR.Length != pixelCount ||
            donorA.DeltaG.Length != pixelCount ||
            donorA.DeltaB.Length != pixelCount ||
            donorB.DeltaR.Length != pixelCount ||
            donorB.DeltaG.Length != pixelCount ||
            donorB.DeltaB.Length != pixelCount)
        {
            return null;
        }

        double sum11 = 0d;
        double sum12 = 0d;
        double sum22 = 0d;
        double sum1 = 0d;
        double sum2 = 0d;
        double sumY = 0d;
        double sum1Y = 0d;
        double sum2Y = 0d;

        static void AccumulateChannel(
            sbyte[] target,
            sbyte[] left,
            sbyte[] right,
            ref double sum11,
            ref double sum12,
            ref double sum22,
            ref double sum1,
            ref double sum2,
            ref double sumY,
            ref double sum1Y,
            ref double sum2Y)
        {
            for (var i = 0; i < target.Length; i++)
            {
                var x1 = (double)left[i];
                var x2 = (double)right[i];
                var y = (double)target[i];
                sum11 += x1 * x1;
                sum12 += x1 * x2;
                sum22 += x2 * x2;
                sum1 += x1;
                sum2 += x2;
                sumY += y;
                sum1Y += x1 * y;
                sum2Y += x2 * y;
            }
        }

        foreach (var sourceRow in sourceRows)
        {
            if (sourceRow.RequiredR.Length != pixelCount ||
                sourceRow.RequiredG.Length != pixelCount ||
                sourceRow.RequiredB.Length != pixelCount)
            {
                return null;
            }

            AccumulateChannel(sourceRow.RequiredR, donorA.DeltaR, donorB.DeltaR,
                ref sum11, ref sum12, ref sum22, ref sum1, ref sum2, ref sumY, ref sum1Y, ref sum2Y);
            AccumulateChannel(sourceRow.RequiredG, donorA.DeltaG, donorB.DeltaG,
                ref sum11, ref sum12, ref sum22, ref sum1, ref sum2, ref sumY, ref sum1Y, ref sum2Y);
            AccumulateChannel(sourceRow.RequiredB, donorA.DeltaB, donorB.DeltaB,
                ref sum11, ref sum12, ref sum22, ref sum1, ref sum2, ref sumY, ref sum1Y, ref sum2Y);
        }

        var sampleCount = pixelCount * 3 * sourceRows.Count;

        double[] solution;
        if (includeBias)
        {
            if (!TrySolveLinearSystem(
                    new[,]
                    {
                        { sum11, sum12, sum1 },
                        { sum12, sum22, sum2 },
                        { sum1, sum2, sampleCount }
                    },
                    [sum1Y, sum2Y, sumY],
                    out solution))
            {
                return null;
            }
        }
        else
        {
            if (!TrySolveLinearSystem(
                    new[,]
                    {
                        { sum11, sum12 },
                        { sum12, sum22 }
                    },
                    [sum1Y, sum2Y],
                    out solution))
            {
                return null;
            }
        }

        var coefficientA = solution[0];
        var coefficientB = solution[1];
        var bias = includeBias ? solution[2] : 0d;
        var blendedR = new sbyte[pixelCount];
        var blendedG = new sbyte[pixelCount];
        var blendedB = new sbyte[pixelCount];
        double sumAbs = 0d;

        static sbyte BlendSample(double coefficientA, double coefficientB, double bias, sbyte left, sbyte right)
        {
            return (sbyte)Math.Clamp(
                (int)Math.Round((coefficientA * left) + (coefficientB * right) + bias),
                -128,
                127);
        }

        for (var i = 0; i < pixelCount; i++)
        {
            blendedR[i] = BlendSample(coefficientA, coefficientB, bias, donorA.DeltaR[i], donorB.DeltaR[i]);
            blendedG[i] = BlendSample(coefficientA, coefficientB, bias, donorA.DeltaG[i], donorB.DeltaG[i]);
            blendedB[i] = BlendSample(coefficientA, coefficientB, bias, donorA.DeltaB[i], donorB.DeltaB[i]);
        }

        foreach (var sourceRow in sourceRows)
        {
            for (var i = 0; i < pixelCount; i++)
            {
                sumAbs += Math.Abs(blendedR[i] - sourceRow.RequiredR[i]);
                sumAbs += Math.Abs(blendedG[i] - sourceRow.RequiredG[i]);
                sumAbs += Math.Abs(blendedB[i] - sourceRow.RequiredB[i]);
            }
        }

        return new ExternalDonorBlendFit(
            coefficientA,
            coefficientB,
            bias,
            sumAbs / sampleCount,
            blendedR,
            blendedG,
            blendedB);
    }

    internal static bool TrySolveLinearSystem(
        double[,] matrix,
        double[] rightHandSide,
        out double[] solution)
    {
        var size = rightHandSide.Length;
        solution = new double[size];
        if (matrix.GetLength(0) != size || matrix.GetLength(1) != size)
        {
            return false;
        }

        var workingMatrix = (double[,])matrix.Clone();
        var workingRhs = (double[])rightHandSide.Clone();

        for (var column = 0; column < size; column++)
        {
            var pivotRow = column;
            var pivotAbs = Math.Abs(workingMatrix[pivotRow, column]);
            for (var row = column + 1; row < size; row++)
            {
                var candidateAbs = Math.Abs(workingMatrix[row, column]);
                if (candidateAbs > pivotAbs)
                {
                    pivotAbs = candidateAbs;
                    pivotRow = row;
                }
            }

            if (pivotAbs <= 1e-9)
            {
                return false;
            }

            if (pivotRow != column)
            {
                for (var swapColumn = column; swapColumn < size; swapColumn++)
                {
                    (workingMatrix[column, swapColumn], workingMatrix[pivotRow, swapColumn]) =
                        (workingMatrix[pivotRow, swapColumn], workingMatrix[column, swapColumn]);
                }

                (workingRhs[column], workingRhs[pivotRow]) = (workingRhs[pivotRow], workingRhs[column]);
            }

            var pivot = workingMatrix[column, column];
            for (var row = column + 1; row < size; row++)
            {
                var factor = workingMatrix[row, column] / pivot;
                if (Math.Abs(factor) <= 1e-12)
                {
                    continue;
                }

                workingMatrix[row, column] = 0d;
                for (var eliminationColumn = column + 1; eliminationColumn < size; eliminationColumn++)
                {
                    workingMatrix[row, eliminationColumn] -= factor * workingMatrix[column, eliminationColumn];
                }

                workingRhs[row] -= factor * workingRhs[column];
            }
        }

        for (var row = size - 1; row >= 0; row--)
        {
            var value = workingRhs[row];
            for (var column = row + 1; column < size; column++)
            {
                value -= workingMatrix[row, column] * solution[column];
            }

            var pivot = workingMatrix[row, row];
            if (Math.Abs(pivot) <= 1e-9)
            {
                return false;
            }

            solution[row] = value / pivot;
        }

        return true;
    }
}
