using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using static EgtAnalyzer.Verification.DeltaTextureHelpers;

namespace EgtAnalyzer.Verification;

/// <summary>
/// Sweeps a single morph coefficient across a fixed factor range and reports the factor that
/// minimizes the mean-absolute RGB error against the shipped texture.
/// </summary>
internal static class NpcFaceGenMorphSweepDumper
{
    private const int MorphFactorSweepMinStep = 0;
    private const int MorphFactorSweepMaxStep = 56;

    internal static void DumpMorphCoefficientSweep(
        NpcAppearance appearance,
        EgtParser egt,
        float[] coefficients,
        DecodedTexture shipped,
        int morphIndex,
        double baselineMae,
        int baselineMaxError,
        double baselineMouthMae)
    {
        if (morphIndex < 0 ||
            morphIndex >= coefficients.Length ||
            morphIndex >= egt.SymmetricMorphs.Length)
        {
            return;
        }

        var currentCoefficient = coefficients[morphIndex];
        if (MathF.Abs(currentCoefficient) < 0.01f)
        {
            return;
        }

        var bestFactor = 1f;
        var bestCoefficient = currentCoefficient;
        var bestMae = baselineMae;
        var bestMax = baselineMaxError;
        var bestMouthMae = baselineMouthMae;

        for (var step = MorphFactorSweepMinStep; step <= MorphFactorSweepMaxStep; step++)
        {
            var factor = step / 32f;
            var candidateCoefficients = (float[])coefficients.Clone();
            candidateCoefficients[morphIndex] = currentCoefficient * factor;

            var generated = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                egt, candidateCoefficients,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
            if (generated == null)
            {
                continue;
            }

            var sweepMetrics = NpcTextureComparison.CompareRgb(
                generated.Pixels, shipped.Pixels,
                generated.Width, generated.Height);
            var mouthMae = GetRegionMae(generated, shipped, "mouth");

            if (sweepMetrics.MeanAbsoluteRgbError < bestMae - 1e-9 ||
                (Math.Abs(sweepMetrics.MeanAbsoluteRgbError - bestMae) <= 1e-9 &&
                 Math.Abs(factor - 1f) < Math.Abs(bestFactor - 1f)))
            {
                bestMae = sweepMetrics.MeanAbsoluteRgbError;
                bestFactor = factor;
                bestCoefficient = candidateCoefficients[morphIndex];
                bestMax = sweepMetrics.MaxAbsoluteRgbError;
                bestMouthMae = mouthMae;
            }
        }

        Console.WriteLine(
            $"  MORPH-SWEEP 0x{appearance.NpcFormId:X8}: " +
            $"[{morphIndex:D2}] currentCoeff={currentCoefficient:F4} currentScale={egt.SymmetricMorphs[morphIndex].Scale:F4} " +
            $"bestFactor={bestFactor:F5} bestCoeff={bestCoefficient:F4} " +
            $"bestMAE={bestMae:F4} (Δ={bestMae - baselineMae:+0.0000;-0.0000}) max={bestMax} " +
            $"mouthMAE={bestMouthMae:F4}");
    }
}
