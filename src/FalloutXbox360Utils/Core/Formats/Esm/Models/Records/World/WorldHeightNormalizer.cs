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

    // Preserves the no-water sentinel as-is; otherwise applies the reportable-height
    // normalization. Use this from raw-bytes parsers so the sentinel survives downstream.
    internal static float PreserveSentinelOrNormalize(float value)
    {
        return IsNoWaterSentinel(value) ? value : NormalizeReportableHeight(value);
    }

    internal static float? PreserveSentinelOrNormalize(float? value)
    {
        return value.HasValue ? PreserveSentinelOrNormalize(value.Value) : null;
    }
}
