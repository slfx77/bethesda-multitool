namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Domain-specific normalization for reportable world/cell heights.
/// </summary>
internal static class WorldHeightNormalizer
{
    internal const float MaxReportableAbsHeight = 100_000f;

    // Bethesda's "no water in this cell" marker — written as the IEEE 754 bit pattern
    // 0x7F7FFFFF (float.MaxValue) in XCLW and worldspace DNAM water-height fields.
    // Distinct from "no XCLW present" (null) so authoring intent survives the parse.
    internal const uint NoWaterSentinelBits = 0x7F7FFFFFu;

    // The canonical IEEE-754 value for the no-water sentinel. Any "no explicit water
    // height" marker collapses to this single value so a downstream IsNoWaterSentinel
    // check catches them all.
    internal static float NoWaterSentinel => BitConverter.UInt32BitsToSingle(NoWaterSentinelBits);

    internal static float NormalizeReportableHeight(float value)
    {
        return IsReportableHeight(value) ? value : 0f;
    }

    internal static float? NormalizeReportableHeight(float? value)
    {
        return value.HasValue ? NormalizeReportableHeight(value.Value) : null;
    }

    internal static bool IsReportableHeight(float value)
    {
        return !float.IsNaN(value)
               && !float.IsInfinity(value)
               && MathF.Abs(value) <= MaxReportableAbsHeight;
    }

    internal static bool IsNoWaterSentinel(float value)
    {
        return BitConverter.SingleToUInt32Bits(value) == NoWaterSentinelBits;
    }

    internal static bool IsNoWaterSentinel(float? value)
    {
        return value.HasValue && IsNoWaterSentinel(value.Value);
    }

    // Used from raw-bytes water-height parsers (XCLW / worldspace DNAM water). A real,
    // in-range height passes through unchanged. Everything else — the canonical FLT_MAX
    // sentinel (0x7F7FFFFF), Skyrim's additional "no explicit height" markers (e.g.
    // -2147483648 / ~4.29e9 seen on Tamriel coast cells), and NaN/Inf — collapses to the
    // canonical no-water sentinel. Collapsing rather than clamping to 0 is load-bearing:
    // the water resolvers read the sentinel as "no override — fall back to the worldspace
    // default water height," whereas a 0 reads as a real sea-level plane and floods the
    // entire cell (the Skyrim "water at the wrong level" bug).
    internal static float PreserveSentinelOrNormalize(float value)
    {
        return IsReportableHeight(value) ? value : NoWaterSentinel;
    }

    internal static float? PreserveSentinelOrNormalize(float? value)
    {
        return value.HasValue ? PreserveSentinelOrNormalize(value.Value) : null;
    }
}
