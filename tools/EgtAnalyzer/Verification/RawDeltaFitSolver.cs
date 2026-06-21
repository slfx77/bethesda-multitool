using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using static EgtAnalyzer.Verification.DeltaTextureHelpers;
using static EgtAnalyzer.Verification.LinearAlgebraUtils;

namespace EgtAnalyzer.Verification;

internal static class RawDeltaFitSolver
{
    internal static RawDeltaLinearFitSolution? SolveRawDeltaCoefficientFitLinearSystem(
        EgtParser egt,
        (float[] R, float[] G, float[] B) shippedDecoded,
        int count)
    {
        if (count <= 0)
        {
            return null;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var sampleCount = pixelCount * 3;
        var target = new float[sampleCount];
        for (var index = 0; index < pixelCount; index++)
        {
            var baseOffset = index * 3;
            target[baseOffset] = shippedDecoded.R[index];
            target[baseOffset + 1] = shippedDecoded.G[index];
            target[baseOffset + 2] = shippedDecoded.B[index];
        }

        var basis = new float[count][];
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            var scaleFactor = scale256 / 65536f;
            var vector = new float[sampleCount];

            if (scale256 != 0)
            {
                for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    var baseOffset = pixelIndex * 3;
                    vector[baseOffset] = morph.DeltaR[pixelIndex] * scaleFactor;
                    vector[baseOffset + 1] = morph.DeltaG[pixelIndex] * scaleFactor;
                    vector[baseOffset + 2] = morph.DeltaB[pixelIndex] * scaleFactor;
                }
            }

            basis[morphIndex] = vector;
        }

        var ata = new double[count, count];
        var aty = new double[count];
        for (var i = 0; i < count; i++)
        {
            aty[i] = DotProduct(basis[i], target);
            for (var j = i; j < count; j++)
            {
                var dot = DotProduct(basis[i], basis[j]);
                ata[i, j] = dot;
                ata[j, i] = dot;
            }
        }

        var diagonalMean = 0.0;
        for (var i = 0; i < count; i++)
        {
            diagonalMean += ata[i, i];
        }

        diagonalMean = diagonalMean > 0 ? diagonalMean / count : 1.0;
        var regularization = diagonalMean * 1e-8;
        for (var i = 0; i < count; i++)
        {
            ata[i, i] += regularization;
        }

        var solved = SolveLinearSystem(ata, aty);
        return solved == null ? null : new RawDeltaLinearFitSolution(basis, solved);
    }

    internal static RawDeltaPixelBuffers AccumulateRawFitBuffers(
        IReadOnlyList<float[]> basis,
        IReadOnlyList<double> weights,
        int pixelCount)
    {
        var fitR = new float[pixelCount];
        var fitG = new float[pixelCount];
        var fitB = new float[pixelCount];
        for (var morphIndex = 0; morphIndex < basis.Count; morphIndex++)
        {
            var weight = (float)weights[morphIndex];
            if (Math.Abs(weight) <= 1e-12f)
            {
                continue;
            }

            var vector = basis[morphIndex];
            for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                var baseOffset = pixelIndex * 3;
                fitR[pixelIndex] += vector[baseOffset] * weight;
                fitG[pixelIndex] += vector[baseOffset + 1] * weight;
                fitB[pixelIndex] += vector[baseOffset + 2] * weight;
            }
        }

        return new RawDeltaPixelBuffers(fitR, fitG, fitB);
    }

    internal static RawDeltaCoefficientFitResult? SolveQuantizedRawDeltaCoefficientFit(
        EgtParser egt,
        (float[] R, float[] G, float[] B) shippedDecoded,
        float[] currentCoefficients)
    {
        var count = Math.Min(currentCoefficients.Length, egt.SymmetricMorphs.Length);
        if (count == 0)
        {
            return null;
        }

        var linearFit = SolveRawDeltaCoefficientFitLinearSystem(egt, shippedDecoded, count);
        if (linearFit == null)
        {
            return null;
        }

        var quantizedCoefficient256 = linearFit.SolvedCoefficient256
            .Select(v => (int)Math.Round(v, MidpointRounding.AwayFromZero))
            .ToArray();

        var pixelCount = egt.Cols * egt.Rows;
        var floatOracleBuffers = AccumulateRawFitBuffers(
            linearFit.Basis,
            linearFit.SolvedCoefficient256,
            pixelCount);
        var quantizedBuffers = AccumulateRawFitBuffers(
            linearFit.Basis,
            Array.ConvertAll(quantizedCoefficient256, static value => (double)value),
            pixelCount);

        var fittedRawMetrics = CompareFloatDeltaRgb(
            (quantizedBuffers.R, quantizedBuffers.G, quantizedBuffers.B),
            shippedDecoded);
        var floatOracleRawMetrics = CompareFloatDeltaRgb(
            (floatOracleBuffers.R, floatOracleBuffers.G, floatOracleBuffers.B),
            shippedDecoded);
        return new RawDeltaCoefficientFitResult(
            quantizedCoefficient256,
            fittedRawMetrics,
            floatOracleRawMetrics,
            floatOracleBuffers);
    }

    internal static RawDeltaResidualSubspaceFitResult? SolveQuantizedRawDeltaResidualSubspaceFit(
        EgtParser egt,
        float[] currentCoefficients,
        (float[] R, float[] G, float[] B) shippedDecoded,
        IReadOnlyList<int> residualSubspaceIndices)
    {
        var filteredIndices = residualSubspaceIndices
            .Where(index => index >= 0 && index < Math.Min(currentCoefficients.Length, egt.SymmetricMorphs.Length))
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        if (filteredIndices.Length == 0)
        {
            return null;
        }

        var currentNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (currentNative == null)
        {
            return null;
        }

        var deltaFit = SolveQuantizedRawResidualDeltaFit(
            egt,
            currentNative.Value,
            shippedDecoded,
            filteredIndices);
        if (deltaFit == null)
        {
            return null;
        }

        var absoluteCoefficients = (float[])currentCoefficients.Clone();
        var rows = new List<RawDeltaResidualSubspaceRow>(filteredIndices.Length);
        foreach (var (morphIndex, delta256) in filteredIndices.Zip(deltaFit.DeltaCoefficient256))
        {
            var current256 = morphIndex < currentCoefficients.Length
                ? (int)(currentCoefficients[morphIndex] * 256f)
                : 0;
            var fit256 = current256 + delta256;
            var fitCoeff = fit256 / 256f;
            if (morphIndex < absoluteCoefficients.Length)
            {
                absoluteCoefficients[morphIndex] = fitCoeff;
            }

            rows.Add(new RawDeltaResidualSubspaceRow(
                morphIndex,
                current256,
                fit256,
                delta256,
                morphIndex < currentCoefficients.Length ? currentCoefficients[morphIndex] : 0f,
                fitCoeff));
        }

        var fittedNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            absoluteCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (fittedNative == null)
        {
            return null;
        }

        var fittedRawMetrics = CompareFloatDeltaRgb(fittedNative.Value, shippedDecoded);
        return new RawDeltaResidualSubspaceFitResult(absoluteCoefficients, fittedRawMetrics, rows);
    }

    internal static HotspotDeltaFitResult? SolveQuantizedRawResidualDeltaFit(
        EgtParser egt,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded,
        IReadOnlyList<int> hotspotIndices)
    {
        if (hotspotIndices.Count == 0)
        {
            return null;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var sampleCount = pixelCount * 3;
        var target = new float[sampleCount];
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var baseOffset = pixelIndex * 3;
            target[baseOffset] = shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex];
            target[baseOffset + 1] = shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex];
            target[baseOffset + 2] = shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex];
        }

        var basis = new float[hotspotIndices.Count][];
        for (var hotspotOrder = 0; hotspotOrder < hotspotIndices.Count; hotspotOrder++)
        {
            var morphIndex = hotspotIndices[hotspotOrder];
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            var scaleFactor = scale256 / 65536f;
            var vector = new float[sampleCount];
            if (scale256 != 0)
            {
                for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    var baseOffset = pixelIndex * 3;
                    vector[baseOffset] = morph.DeltaR[pixelIndex] * scaleFactor;
                    vector[baseOffset + 1] = morph.DeltaG[pixelIndex] * scaleFactor;
                    vector[baseOffset + 2] = morph.DeltaB[pixelIndex] * scaleFactor;
                }
            }

            basis[hotspotOrder] = vector;
        }

        var ata = new double[hotspotIndices.Count, hotspotIndices.Count];
        var aty = new double[hotspotIndices.Count];
        for (var i = 0; i < hotspotIndices.Count; i++)
        {
            aty[i] = DotProduct(basis[i], target);
            for (var j = i; j < hotspotIndices.Count; j++)
            {
                var dot = DotProduct(basis[i], basis[j]);
                ata[i, j] = dot;
                ata[j, i] = dot;
            }
        }

        var diagonalMean = 0.0;
        for (var i = 0; i < hotspotIndices.Count; i++)
        {
            diagonalMean += ata[i, i];
        }

        diagonalMean = diagonalMean > 0 ? diagonalMean / hotspotIndices.Count : 1.0;
        var regularization = diagonalMean * 1e-8;
        for (var i = 0; i < hotspotIndices.Count; i++)
        {
            ata[i, i] += regularization;
        }

        var solved = SolveLinearSystem(ata, aty);
        if (solved == null)
        {
            return null;
        }

        var quantizedDelta256 = solved
            .Select(value => (int)Math.Round(value, MidpointRounding.AwayFromZero))
            .ToArray();

        var fitR = new float[pixelCount];
        var fitG = new float[pixelCount];
        var fitB = new float[pixelCount];
        for (var hotspotOrder = 0; hotspotOrder < hotspotIndices.Count; hotspotOrder++)
        {
            var weight = quantizedDelta256[hotspotOrder];
            if (weight == 0)
            {
                continue;
            }

            var vector = basis[hotspotOrder];
            for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                var baseOffset = pixelIndex * 3;
                fitR[pixelIndex] += vector[baseOffset] * weight;
                fitG[pixelIndex] += vector[baseOffset + 1] * weight;
                fitB[pixelIndex] += vector[baseOffset + 2] * weight;
            }
        }

        var fittedResidualMetrics = CompareFloatDeltaRgb(
            (fitR, fitG, fitB),
            DecodeResidualTarget(target, pixelCount));
        return new HotspotDeltaFitResult(quantizedDelta256, fittedResidualMetrics);
    }

    internal static (float[] R, float[] G, float[] B) DecodeResidualTarget(float[] target, int pixelCount)
    {
        var r = new float[pixelCount];
        var g = new float[pixelCount];
        var b = new float[pixelCount];
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var baseOffset = pixelIndex * 3;
            r[pixelIndex] = target[baseOffset];
            g[pixelIndex] = target[baseOffset + 1];
            b[pixelIndex] = target[baseOffset + 2];
        }

        return (r, g, b);
    }

    internal static RawDeltaChannelFreeFitResult? SolveQuantizedRawDeltaChannelFreeCoefficientFit(
        EgtParser egt,
        (float[] R, float[] G, float[] B) shippedDecoded,
        float[] currentCoefficients)
    {
        var count = Math.Min(currentCoefficients.Length, egt.SymmetricMorphs.Length);
        if (count == 0)
        {
            return null;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var basisR = new float[count][];
        var basisG = new float[count][];
        var basisB = new float[count][];

        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            var scaleFactor = scale256 / 65536f;

            var vectorR = new float[pixelCount];
            var vectorG = new float[pixelCount];
            var vectorB = new float[pixelCount];
            if (scale256 != 0)
            {
                for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    vectorR[pixelIndex] = morph.DeltaR[pixelIndex] * scaleFactor;
                    vectorG[pixelIndex] = morph.DeltaG[pixelIndex] * scaleFactor;
                    vectorB[pixelIndex] = morph.DeltaB[pixelIndex] * scaleFactor;
                }
            }

            basisR[morphIndex] = vectorR;
            basisG[morphIndex] = vectorG;
            basisB[morphIndex] = vectorB;
        }

        var solvedR = SolveChannelFit(basisR, shippedDecoded.R);
        var solvedG = SolveChannelFit(basisG, shippedDecoded.G);
        var solvedB = SolveChannelFit(basisB, shippedDecoded.B);
        if (solvedR == null || solvedG == null || solvedB == null)
        {
            return null;
        }

        var quantizedR = solvedR
            .Select(v => (int)Math.Round(v, MidpointRounding.AwayFromZero))
            .ToArray();
        var quantizedG = solvedG
            .Select(v => (int)Math.Round(v, MidpointRounding.AwayFromZero))
            .ToArray();
        var quantizedB = solvedB
            .Select(v => (int)Math.Round(v, MidpointRounding.AwayFromZero))
            .ToArray();

        var fitR = new float[pixelCount];
        var fitG = new float[pixelCount];
        var fitB = new float[pixelCount];
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var weightR = quantizedR[morphIndex];
            var weightG = quantizedG[morphIndex];
            var weightB = quantizedB[morphIndex];
            var vectorR = basisR[morphIndex];
            var vectorG = basisG[morphIndex];
            var vectorB = basisB[morphIndex];

            for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                fitR[pixelIndex] += vectorR[pixelIndex] * weightR;
                fitG[pixelIndex] += vectorG[pixelIndex] * weightG;
                fitB[pixelIndex] += vectorB[pixelIndex] * weightB;
            }
        }

        var fittedRawMetrics = CompareFloatDeltaRgb((fitR, fitG, fitB), shippedDecoded);
        return new RawDeltaChannelFreeFitResult(
            quantizedR,
            quantizedG,
            quantizedB,
            fitR,
            fitG,
            fitB,
            fittedRawMetrics);
    }

    internal static double[]? SolveChannelFit(float[][] basis, float[] target)
    {
        var count = basis.Length;
        var ata = new double[count, count];
        var aty = new double[count];
        for (var i = 0; i < count; i++)
        {
            aty[i] = DotProduct(basis[i], target);
            for (var j = i; j < count; j++)
            {
                var dot = DotProduct(basis[i], basis[j]);
                ata[i, j] = dot;
                ata[j, i] = dot;
            }
        }

        var diagonalMean = 0.0;
        for (var i = 0; i < count; i++)
        {
            diagonalMean += ata[i, i];
        }

        diagonalMean = diagonalMean > 0 ? diagonalMean / count : 1.0;
        var regularization = diagonalMean * 1e-8;
        for (var i = 0; i < count; i++)
        {
            ata[i, i] += regularization;
        }

        return SolveLinearSystem(ata, aty);
    }

}
