using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using static EgtAnalyzer.Verification.LinearAlgebraUtils;

namespace EgtAnalyzer.Verification;

internal static class PcaCoefficientAnalyzer
{
    internal static List<double[]> BuildCenteredFamilyDifferenceVectors(
        IReadOnlyList<float[]> familyCoefficients,
        float[] currentCoefficients,
        int count)
    {
        var vectors = new List<double[]>(familyCoefficients.Count);
        foreach (var candidate in familyCoefficients)
        {
            if (candidate.Length < count)
            {
                continue;
            }

            var vector = new double[count];
            var sumSq = 0d;
            for (var index = 0; index < count; index++)
            {
                var delta = candidate[index] - currentCoefficients[index];
                vector[index] = delta;
                sumSq += delta * delta;
            }

            if (sumSq > 1e-12)
            {
                vectors.Add(vector);
            }
        }

        return vectors;
    }

    internal static double[,] BuildCovarianceMatrix(
        IReadOnlyList<double[]> differenceVectors,
        int count)
    {
        var covariance = new double[count, count];
        foreach (var vector in differenceVectors)
        {
            for (var row = 0; row < count; row++)
            {
                var rowValue = vector[row];
                if (Math.Abs(rowValue) < 1e-18)
                {
                    continue;
                }

                for (var col = row; col < count; col++)
                {
                    covariance[row, col] += rowValue * vector[col];
                }
            }
        }

        var scale = 1d / differenceVectors.Count;
        for (var row = 0; row < count; row++)
        {
            for (var col = row; col < count; col++)
            {
                covariance[row, col] *= scale;
                covariance[col, row] = covariance[row, col];
            }
        }

        return covariance;
    }

    internal static PrincipalComponentSet? ComputeTopPrincipalComponents(
        double[,] covariance,
        int maxComponentCount)
    {
        var size = covariance.GetLength(0);
        if (size == 0 || maxComponentCount <= 0)
        {
            return null;
        }

        var trace = 0d;
        for (var index = 0; index < size; index++)
        {
            trace += covariance[index, index];
        }

        if (trace <= 1e-12)
        {
            return null;
        }

        var eigenvalues = new List<double>(maxComponentCount);
        var eigenvectors = new List<double[]>(maxComponentCount);
        for (var component = 0; component < maxComponentCount; component++)
        {
            var vector = CreatePrincipalComponentSeed(size, component);
            Orthogonalize(vector, eigenvectors);
            var norm = VectorNorm(vector);
            if (norm <= 1e-12)
            {
                break;
            }

            ScaleVector(vector, 1d / norm);

            for (var iteration = 0; iteration < 128; iteration++)
            {
                var next = MultiplyMatrixVector(covariance, vector);
                Orthogonalize(next, eigenvectors);
                var nextNorm = VectorNorm(next);
                if (nextNorm <= 1e-12)
                {
                    vector = Array.Empty<double>();
                    break;
                }

                ScaleVector(next, 1d / nextNorm);
                var delta = VectorDifferenceNormSquared(next, vector);
                var negDelta = VectorSumNormSquared(next, vector);
                vector = next;
                if (Math.Min(delta, negDelta) <= 1e-18)
                {
                    break;
                }
            }

            if (vector.Length == 0)
            {
                break;
            }

            var eigenvalue = Math.Max(0d, RayleighQuotient(covariance, vector));
            if (eigenvalue <= trace * 1e-9)
            {
                break;
            }

            eigenvalues.Add(eigenvalue);
            eigenvectors.Add(vector);
        }

        if (eigenvalues.Count == 0)
        {
            return null;
        }

        return new PrincipalComponentSet(eigenvalues.ToArray(), eigenvectors.ToArray());
    }

    internal static int SelectPrincipalComponentCount(
        IReadOnlyList<double> eigenvalues,
        int minPreferredCount,
        int maxCount)
    {
        if (eigenvalues.Count == 0 || maxCount <= 0)
        {
            return 0;
        }

        var totalVariance = eigenvalues.Sum();
        if (totalVariance <= 0d)
        {
            return 0;
        }

        var limit = Math.Min(maxCount, eigenvalues.Count);
        var minCount = Math.Min(minPreferredCount, limit);
        var cumulative = 0d;
        var selected = 0;
        while (selected < limit)
        {
            cumulative += eigenvalues[selected];
            selected++;
            if (selected >= minCount && cumulative / totalVariance >= 0.90d)
            {
                break;
            }
        }

        return selected;
    }

    internal static AxisProjectionRange[] ComputeAxisProjectionRanges(
        IReadOnlyList<double[]> axisCoefficients,
        IReadOnlyList<double[]> differenceVectors)
    {
        var ranges = new AxisProjectionRange[axisCoefficients.Count];
        for (var axisIndex = 0; axisIndex < axisCoefficients.Count; axisIndex++)
        {
            var axis = axisCoefficients[axisIndex];
            var min = 0d;
            var max = 0d;
            var initialized = false;
            foreach (var difference in differenceVectors)
            {
                var projection = DotProduct(axis, difference);
                if (!initialized)
                {
                    min = projection;
                    max = projection;
                    initialized = true;
                    continue;
                }

                min = Math.Min(min, projection);
                max = Math.Max(max, projection);
            }

            ranges[axisIndex] = new AxisProjectionRange(min, max);
        }

        return ranges;
    }

    internal static float[] BuildResidualTargetVector(
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var pixelCount = currentNative.R.Length;
        var target = new float[pixelCount * 3];
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var baseOffset = pixelIndex * 3;
            target[baseOffset] = shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex];
            target[baseOffset + 1] = shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex];
            target[baseOffset + 2] = shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex];
        }

        return target;
    }

    internal static float[][] BuildCoefficientAxisPixelBasis(
        EgtParser egt,
        IReadOnlyList<double[]> axisCoefficients,
        int count)
    {
        var pixelCount = egt.Cols * egt.Rows;
        var basis = new float[axisCoefficients.Count][];

        for (var axisIndex = 0; axisIndex < axisCoefficients.Count; axisIndex++)
        {
            var vector = new float[pixelCount * 3];
            var axis = axisCoefficients[axisIndex];
            for (var morphIndex = 0; morphIndex < count; morphIndex++)
            {
                var morphWeight = axis[morphIndex];
                if (Math.Abs(morphWeight) <= 1e-12)
                {
                    continue;
                }

                var morph = egt.SymmetricMorphs[morphIndex];
                var scale256 = (int)(morph.Scale * 256f);
                if (scale256 == 0)
                {
                    continue;
                }

                var scaleFactor = (float)(morphWeight * (scale256 / 65536f));
                for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    var baseOffset = pixelIndex * 3;
                    vector[baseOffset] += morph.DeltaR[pixelIndex] * scaleFactor;
                    vector[baseOffset + 1] += morph.DeltaG[pixelIndex] * scaleFactor;
                    vector[baseOffset + 2] += morph.DeltaB[pixelIndex] * scaleFactor;
                }
            }

            basis[axisIndex] = vector;
        }

        return basis;
    }

    internal static double[]? SolveAxisWeights(
        IReadOnlyList<float[]> axisBasis,
        float[] targetResidual)
    {
        if (axisBasis.Count == 0)
        {
            return null;
        }

        var count = axisBasis.Count;
        var ata = new double[count, count];
        var aty = new double[count];
        for (var row = 0; row < count; row++)
        {
            aty[row] = DotProduct(axisBasis[row], targetResidual);
            for (var col = row; col < count; col++)
            {
                var dot = DotProduct(axisBasis[row], axisBasis[col]);
                ata[row, col] = dot;
                ata[col, row] = dot;
            }
        }

        var diagonalMean = 0d;
        for (var index = 0; index < count; index++)
        {
            diagonalMean += ata[index, index];
        }

        diagonalMean = diagonalMean > 0d ? diagonalMean / count : 1d;
        var regularization = diagonalMean * 1e-8;
        for (var index = 0; index < count; index++)
        {
            ata[index, index] += regularization;
        }

        return SolveLinearSystem(ata, aty);
    }

    internal static int[] BuildQuantizedCoefficientVector(
        float[] currentCoefficients,
        IReadOnlyList<double[]> axisCoefficients,
        IReadOnlyList<double> axisWeights,
        int count)
    {
        var quantized = new int[count];
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var coefficient = morphIndex < currentCoefficients.Length
                ? currentCoefficients[morphIndex]
                : 0f;
            for (var axisIndex = 0; axisIndex < axisCoefficients.Count; axisIndex++)
            {
                coefficient += (float)(axisCoefficients[axisIndex][morphIndex] * axisWeights[axisIndex]);
            }

            quantized[morphIndex] = (int)Math.Round(coefficient * 256f, MidpointRounding.AwayFromZero);
        }

        return quantized;
    }
}

