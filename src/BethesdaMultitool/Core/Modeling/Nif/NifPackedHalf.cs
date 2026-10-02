namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Exact IEEE 754 binary16 to binary32 widening, done on the bits so the result never depends on a runtime
///     conversion: every finite half (normal or subnormal) is representable as a float, so the widening is
///     bit-exact, infinities keep their sign and a NaN keeps its payload in the high mantissa bits.
/// </summary>
internal static class NifPackedHalf
{
    /// <summary>The bits of the half 1.0, the constant fourth component of every half4 channel.</summary>
    public const ushort OneBits = 0x3C00;

    /// <summary>Widens one half to a float exactly.</summary>
    /// <param name="bits">The binary16 bits, already in host order.</param>
    public static float ToSingle(ushort bits)
    {
        var sign = (uint)(bits & 0x8000) << 16;
        var exponent = (bits >> 10) & 0x1F;
        var mantissa = (uint)(bits & 0x03FF);
        if (exponent == 0)
        {
            if (mantissa == 0)
            {
                return BitConverter.UInt32BitsToSingle(sign);
            }

            // Subnormal: mantissa x 2^-24, exact in float (the mantissa has at most ten significant bits).
            var magnitude = mantissa * (1f / 16777216f);
            return sign == 0 ? magnitude : -magnitude;
        }

        if (exponent == 0x1F)
        {
            return BitConverter.UInt32BitsToSingle(sign | 0x7F800000u | (mantissa << 13));
        }

        return BitConverter.UInt32BitsToSingle(sign | ((uint)(exponent - 15 + 127) << 23) | (mantissa << 13));
    }
}
