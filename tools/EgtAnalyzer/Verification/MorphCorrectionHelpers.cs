namespace EgtAnalyzer.Verification;

internal static class MorphCorrectionHelpers
{
    internal static void AccumulateMorphGainFit(
        sbyte currentDeltaByte,
        float residual,
        float factor,
        ref double numerator,
        ref double denominator)
    {
        var basis = factor * currentDeltaByte;
        numerator += residual * basis;
        denominator += basis * basis;
    }

    internal static void AccumulateMorphAffineFit(
        sbyte currentDeltaByte,
        float residual,
        float factor,
        ref double sumX,
        ref double sumY,
        ref double sumXX,
        ref double sumXY)
    {
        var x = (double)currentDeltaByte;
        var y = x + (residual / factor);
        sumX += x;
        sumY += y;
        sumXX += x * x;
        sumXY += x * y;
    }

    internal static void AccumulateMorphRowSample(
        sbyte sourceDeltaByte,
        sbyte currentDeltaByte,
        float residual,
        float factor,
        ref double sumX,
        ref double sumY,
        ref double sumXX,
        ref double sumYY,
        ref double sumXY)
    {
        var x = (double)currentDeltaByte;
        var y = sourceDeltaByte + (residual / factor);
        sumX += x;
        sumY += y;
        sumXX += x * x;
        sumYY += y * y;
        sumXY += x * y;
    }

    internal static void AccumulateMorphRowSimilarityResidual(
        sbyte sourceDeltaByte,
        sbyte currentDeltaByte,
        float residual,
        float factor,
        double gain,
        double affineScale,
        double affineBias,
        ref double targetMae,
        ref double gainFitMae,
        ref double affineFitMae)
    {
        var x = (double)currentDeltaByte;
        var y = sourceDeltaByte + (residual / factor);
        targetMae += Math.Abs(y - x);
        gainFitMae += Math.Abs(y - (gain * x));
        affineFitMae += Math.Abs(y - ((affineScale * x) + affineBias));
    }

    internal static void ApplyMorphContentCorrection(
        sbyte currentDeltaByte,
        float residual,
        float factor,
        out float correctedValue,
        float currentValue,
        ref double sumAbsRequiredByteDelta,
        ref double sumAbsClipByte,
        ref float maxAbsRequiredByteDelta,
        ref float maxAbsClipByte,
        ref int inRangeCount)
    {
        var requiredDelta = currentDeltaByte + (residual / factor);
        var clippedDelta = Math.Clamp(requiredDelta, -128f, 127f);
        var requiredByteDelta = MathF.Abs(requiredDelta - currentDeltaByte);
        var clipByte = MathF.Abs(requiredDelta - clippedDelta);

        sumAbsRequiredByteDelta += requiredByteDelta;
        sumAbsClipByte += clipByte;
        maxAbsRequiredByteDelta = Math.Max(maxAbsRequiredByteDelta, requiredByteDelta);
        maxAbsClipByte = Math.Max(maxAbsClipByte, clipByte);
        if (clipByte <= 1e-6f)
        {
            inRangeCount++;
        }

        correctedValue = currentValue + ((clippedDelta - currentDeltaByte) * factor);
    }

    internal static void ApplyMorphGainCorrection(
        sbyte currentDeltaByte,
        float factor,
        double gain,
        out float correctedValue,
        float currentValue,
        ref double sumAbsByteDelta,
        ref double sumAbsClipByte,
        ref float maxAbsByteDelta,
        ref float maxAbsClipByte,
        ref int inRangeCount)
    {
        var scaledDelta = (float)(currentDeltaByte * gain);
        var clippedDelta = Math.Clamp(scaledDelta, -128f, 127f);
        var byteDelta = MathF.Abs(scaledDelta - currentDeltaByte);
        var clipByte = MathF.Abs(scaledDelta - clippedDelta);

        sumAbsByteDelta += byteDelta;
        sumAbsClipByte += clipByte;
        maxAbsByteDelta = Math.Max(maxAbsByteDelta, byteDelta);
        maxAbsClipByte = Math.Max(maxAbsClipByte, clipByte);
        if (clipByte <= 1e-6f)
        {
            inRangeCount++;
        }

        correctedValue = currentValue + ((clippedDelta - currentDeltaByte) * factor);
    }

    internal static void ApplyMorphAffineCorrection(
        sbyte currentDeltaByte,
        float factor,
        double scale,
        double bias,
        out float correctedValue,
        float currentValue,
        ref double sumAbsByteDelta,
        ref double sumAbsClipByte,
        ref float maxAbsByteDelta,
        ref float maxAbsClipByte,
        ref int inRangeCount)
    {
        var affineDelta = (float)((currentDeltaByte * scale) + bias);
        var clippedDelta = Math.Clamp(affineDelta, -128f, 127f);
        var byteDelta = MathF.Abs(affineDelta - currentDeltaByte);
        var clipByte = MathF.Abs(affineDelta - clippedDelta);

        sumAbsByteDelta += byteDelta;
        sumAbsClipByte += clipByte;
        maxAbsByteDelta = Math.Max(maxAbsByteDelta, byteDelta);
        maxAbsClipByte = Math.Max(maxAbsClipByte, clipByte);
        if (clipByte <= 1e-6f)
        {
            inRangeCount++;
        }

        correctedValue = currentValue + ((clippedDelta - currentDeltaByte) * factor);
    }
}
