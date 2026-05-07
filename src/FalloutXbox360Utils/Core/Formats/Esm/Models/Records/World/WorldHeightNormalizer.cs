namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Domain-specific normalization for reportable world/cell heights.
/// </summary>
internal static class WorldHeightNormalizer
{
    internal const float MaxReportableAbsHeight = 100_000f;

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
}
