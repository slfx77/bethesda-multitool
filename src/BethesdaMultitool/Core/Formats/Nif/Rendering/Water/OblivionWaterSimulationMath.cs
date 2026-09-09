using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Algebra of installed WATERDISPLACE002/003/005/006/007. Sampling and render-target conversion
///     are separate operations; these functions do not emulate GPU rounding, saturation or coverage.
/// </summary>
internal static class OblivionWaterSimulationMath
{
    internal const uint InitialPackedClear = 0xFF7F7F7F;
    internal const ushort InitialUnorm16Rgb = 0x7F7F;
    internal static Vector4 InitialState => new(127f / 255f, 127f / 255f, 127f / 255f, 1f);
    internal static Vector4 StampState => new(0.9f, 0.5f, 0.5f, 0.5f);

    internal static Vector4 Evolve(Vector4 center, Vector4 cardinalRed, Vector3 controls)
    {
        var laplacian = cardinalRed.X + cardinalRed.Y + cardinalRed.Z + cardinalRed.W - 4f * center.X;
        var value = center - new Vector4(0.5f);
        value.Y += controls.X * laplacian;
        value.X += controls.Y * value.Y;
        return new Vector4(0.5f) + controls.Z * value;
    }

    /// <summary>Encodes the recorded cardinal and diagonal height gradients into the ordinary normal output.</summary>
    /// <param name="cardinal">West, east, north, south red samples.</param>
    /// <param name="diagonal">Northwest, northeast, southwest, southeast red samples.</param>
    /// <param name="dampener">The recorded fDamp.x.</param>
    internal static Vector4 EncodeNormal(Vector4 cardinal, Vector4 diagonal, float dampener)
    {
        var gradientX = dampener * (2f * MathF.Abs(cardinal.Y) - 2f * MathF.Abs(cardinal.X) +
            MathF.Abs(diagonal.Y) + MathF.Abs(diagonal.W) - MathF.Abs(diagonal.X) - MathF.Abs(diagonal.Z));
        var gradientY = dampener * (2f * MathF.Abs(cardinal.W) - 2f * MathF.Abs(cardinal.Z) +
            MathF.Abs(diagonal.Z) + MathF.Abs(diagonal.W) - MathF.Abs(diagonal.X) - MathF.Abs(diagonal.Y));
        var normal = Vector3.Normalize(new Vector3(-gradientX, gradientY, 1f));
        return new Vector4(normal * 0.5f + new Vector3(0.5f), 1f);
    }

    internal static Vector4 BlendHeight(float fftHeight, float rainInputHeight, float dampener, float amount)
    {
        var first = 0.8f / dampener * MathF.Abs(fftHeight);
        var second = MathF.Abs(rainInputHeight);
        var height = first + (second - first) * amount;
        return new Vector4(height, height, height, 1f);
    }

    internal static Vector2 RecenterSampleUv(Vector2 uv, Vector2 offset)
    {
        return uv + offset;
    }

    internal static Vector4 Recenter(Vector2 originalUv, Vector4 offsetSample)
    {
        var fade = Math.Clamp((Vector2.Distance(originalUv, new Vector2(0.5f)) - 0.4f) * 10f, 0f, 1f);
        return Vector4.Lerp(offsetSample, new Vector4(0.5f, 0.5f, 0.5f, 1f), fade);
    }

    /// <summary>Raw rand results are recorded inputs; this helper never chooses a seed.</summary>
    internal static float RainClipOffset(int recordedRandom)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recordedRandom);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recordedRandom, 32767);
        // 007DF08E..007DF0A4 stores the quotient as float before doubling/subtracting.
        var unit = (float)(recordedRandom / 32767.0);
        return 2f * unit - 1f;
    }

    /// <summary>
    ///     007DF05E..007DF06A multiplies an int32 rate by a stored float using x87 before truncation.
    ///     Its helper's SSE arm first stores binary64; its other arm truncates an x87 value. The
    ///     active FPU precision/control is not recovered. Admit only products whose counts agree
    ///     across outward rounding to 24 significant bits: this also bounds 53/64-bit intermediates.
    ///     Ambiguity and overflow are outside this replay cohort, not permission to choose a mode.
    /// </summary>
    internal static int RainEventCount(int rate, float accumulatedSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rate);
        if (!float.IsFinite(accumulatedSeconds) || accumulatedSeconds < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(accumulatedSeconds));
        }

        var bits = BitConverter.SingleToUInt32Bits(accumulatedSeconds);
        var exponentBits = (int)((bits >> 23) & 255);
        var significand = bits & 0x7FFFFF;
        if (exponentBits != 0)
        {
            significand |= 0x800000;
        }

        var exponent = exponentBits == 0 ? -149 : exponentBits - 150;
        var product = (ulong)(uint)rate * significand;
        if (product == 0)
        {
            return 0;
        }

        // The integer*float product has at most 55 bits, so its exact significand fits UInt64.
        // Outward bounds include all rounding directions at precision >=24, including a later
        // binary64 store. This is an eligibility check, not a guessed hardware quantizer.
        var discardedBits = Math.Max(0, BitOperations.Log2(product) + 1 - 24);
        var lower = (product >> discardedBits) << discardedBits;
        var upper = lower == product ? lower : lower + (1UL << discardedBits);
        var lowerCount = TruncateProduct(lower, exponent);
        var upperCount = TruncateProduct(upper, exponent);
        if (lowerCount != upperCount)
        {
            throw new ArgumentException("Recorded rain count depends on the unrecovered FPU/CRT conversion mode.",
                nameof(accumulatedSeconds));
        }

        return lowerCount;
    }

    private static int TruncateProduct(ulong product, int exponent)
    {
        ulong count;
        if (exponent >= 0)
        {
            if (exponent >= 31 || product > (ulong)int.MaxValue >> exponent)
            {
                throw new ArgumentOutOfRangeException(nameof(product), "Recorded rain count exceeds Int32.");
            }

            count = product << exponent;
        }
        else
        {
            count = exponent <= -64 ? 0 : product >> -exponent;
        }

        if (count > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(product), "Recorded rain count exceeds Int32.");
        }

        return (int)count;
    }
}

/// <summary>Proposed CPU policy seam; no live WaterRenderer12 consumer is changed by this staging.</summary>
internal static class OblivionWaterSimulationRoutePolicy
{
    internal static uint ApplyOrdinaryRippleToggle(
        BethesdaGame game, bool ripplesEnabled, uint ordinaryNormal, uint flatNormal)
    {
        return game is BethesdaGame.Oblivion or BethesdaGame.Morrowind || ripplesEnabled || flatNormal == uint.MaxValue
            ? ordinaryNormal
            : flatNormal;
    }
}
