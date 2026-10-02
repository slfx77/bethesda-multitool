using System.Numerics;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Correct rounding of an exact rational to float32 (round to nearest, ties to even) by integer arithmetic alone, so
///     a test can check a production numeric route against a value computed without any floating-point operation.
///     Normal-range results only (every value the Starfield <c>.mesh</c> tests round is far above the subnormal range).
/// </summary>
internal static class ExactFloat32
{
    /// <summary>The float32 nearest to <paramref name="numerator" /> / <paramref name="denominator" />, ties to even.</summary>
    /// <exception cref="ArgumentException">The denominator is zero.</exception>
    public static float Round(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.IsZero)
        {
            throw new ArgumentException("The denominator must not be zero.", nameof(denominator));
        }

        if (numerator.IsZero)
        {
            return 0f;
        }

        var negative = numerator.Sign < 0 != denominator.Sign < 0;
        var a = BigInteger.Abs(numerator);
        var b = BigInteger.Abs(denominator);

        // k = floor(log2(a / b)), from the bit lengths and one exact comparison.
        var k = (int)(a.GetBitLength() - b.GetBitLength());
        if (CompareWithPowerOfTwo(a, b, k) < 0)
        {
            k--;
        }

        // The 24-bit significand q = a * 2^(23 - k) / b lies in [2^23, 2^24) before rounding.
        var shift = 23 - k;
        var scaledNumerator = shift >= 0 ? a << shift : a;
        var scaledDenominator = shift >= 0 ? b : b << -shift;
        var q = BigInteger.DivRem(scaledNumerator, scaledDenominator, out var remainder);
        var half = (remainder * 2).CompareTo(scaledDenominator);
        if (half > 0 || (half == 0 && !q.IsEven))
        {
            q += 1;
        }

        if (q == BigInteger.One << 24)
        {
            q >>= 1;
            k++;
        }

        var value = MathF.ScaleB((float)q, k - 23);
        return negative ? -value : value;
    }

    /// <summary>Compares <paramref name="a" /> with <paramref name="b" /> x 2^<paramref name="k" /> exactly.</summary>
    private static int CompareWithPowerOfTwo(BigInteger a, BigInteger b, int k)
    {
        return k >= 0 ? a.CompareTo(b << k) : (a << -k).CompareTo(b);
    }
}
