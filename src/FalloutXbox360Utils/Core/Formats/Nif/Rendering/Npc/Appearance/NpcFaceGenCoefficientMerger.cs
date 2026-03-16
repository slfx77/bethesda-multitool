namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal static class NpcFaceGenCoefficientMerger
{
    /// <summary>
    ///     RMS clamp threshold for merged coefficients. When > 0, if the RMS of the
    ///     merged coefficient array exceeds this value, all coefficients are scaled down
    ///     by (threshold / rms). Matches BSFaceGenManager::MergeFaceGenCoord behavior.
    ///     Set to 0 to disable (engine default in MemDebug build).
    /// </summary>
    internal static float RmsClampThreshold { get; set; }

    internal static float[]? Merge(float[]? npcCoefficients, float[]? raceCoefficients)
    {
        if (npcCoefficients == null)
        {
            return raceCoefficients;
        }

        if (raceCoefficients == null)
        {
            return npcCoefficients;
        }

        var count = Math.Min(npcCoefficients.Length, raceCoefficients.Length);
        var merged = new float[count];
        for (var i = 0; i < count; i++)
        {
            merged[i] = npcCoefficients[i] + raceCoefficients[i];
        }

        if (RmsClampThreshold > 0f && count > 0)
        {
            ApplyRmsNormalization(merged, RmsClampThreshold);
        }

        return merged;
    }

    /// <summary>
    ///     Matches MergeFaceGenCoord normalization:
    ///     lengthSqr = sum(coeff[i]^2)
    ///     rms = sqrt(lengthSqr / count)
    ///     if rms > threshold: scale all by threshold / rms
    /// </summary>
    private static void ApplyRmsNormalization(float[] coefficients, float threshold)
    {
        var sumOfSquares = 0.0;
        for (var i = 0; i < coefficients.Length; i++)
        {
            sumOfSquares += (double)coefficients[i] * coefficients[i];
        }

        var rms = (float)Math.Sqrt(sumOfSquares / coefficients.Length);
        if (rms > threshold)
        {
            var scale = threshold / rms;
            for (var i = 0; i < coefficients.Length; i++)
            {
                coefficients[i] *= scale;
            }
        }
    }
}
