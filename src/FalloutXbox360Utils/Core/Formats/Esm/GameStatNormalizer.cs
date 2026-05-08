namespace FalloutXbox360Utils.Core.Formats.Esm;

using System.Buffers.Binary;

/// <summary>
///     Domain guards for values read from mixed ESM/runtime sources before they enter reports.
/// </summary>
internal static class GameStatNormalizer
{
    internal static int ArmorDamageResistance(int value)
    {
        return value is >= 0 and <= 200 ? value : 0;
    }

    internal static float ArmorDamageThreshold(float value)
    {
        return IsFinite(value) && value is >= 0 and <= 200 ? value : 0f;
    }

    internal static bool IsPlausibleEffectMagnitude(float value)
    {
        return IsFinite(value) && MathF.Abs(value) <= 100_000f;
    }

    internal static float EffectMagnitude(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 4)
        {
            return 0f;
        }

        var decoded = bigEndian
            ? BinaryPrimitives.ReadSingleBigEndian(data)
            : BinaryPrimitives.ReadSingleLittleEndian(data);
        var raw = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);

        if (raw == 0)
        {
            return 0f;
        }

        if (IsPlausibleEffectMagnitude(decoded) && MathF.Abs(decoded) >= 1e-30f)
        {
            return decoded;
        }

        if (raw <= 100_000)
        {
            return raw;
        }

        return IsPlausibleEffectMagnitude(decoded) ? decoded : 0f;
    }

    internal static bool IsPlausibleEffectArea(uint value)
    {
        return value <= 100_000;
    }

    internal static bool IsPlausibleEffectDuration(uint value)
    {
        return value <= 1_000_000;
    }

    internal static bool IsPlausibleEffectTarget(uint value)
    {
        return value <= 2;
    }

    internal static bool IsPlausibleActorValue(int value)
    {
        return value is >= -1 and <= 10_000;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
