namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Pure bounds used while preparing the per-frame sun-shadow replay. Cascade instance sets are
///     stored as prefixes of each batch, so anything beyond the widest prefix can never contribute
///     to a compatible replay and a draw whose four prefixes are empty need not be captured.
/// </summary>
internal static class ShadowCascadeSubmissionPolicy
{
    internal const int CascadeCount = 4;

    /// <summary>
    ///     Returns the number of source tail instances that can contribute to any cascade. A stale
    ///     or incomplete prefix table deliberately falls back to the whole tail for correctness.
    /// </summary>
    internal static int UsefulSourceTailCount(
        int sourceCount,
        ReadOnlySpan<int> cascadePrefixes,
        bool prefixesCompatible)
    {
        sourceCount = Math.Max(sourceCount, 0);
        if (!prefixesCompatible || cascadePrefixes.Length < CascadeCount)
        {
            return sourceCount;
        }

        var widestPrefix = 0;
        for (var cascade = 0; cascade < CascadeCount; cascade++)
        {
            widestPrefix = Math.Max(widestPrefix, cascadePrefixes[cascade]);
        }

        return Math.Clamp(widestPrefix, 0, sourceCount);
    }

    /// <summary>Clamps one cascade prefix to the range the draw can actually address.</summary>
    internal static int ClampInstanceCount(int drawCount, int cascadeCount)
        => Math.Min(Math.Max(drawCount, 0), Math.Max(cascadeCount, 0));

    /// <summary>
    ///     True when at least one of the four cascade prefixes contains an addressable instance.
    ///     A short prefix table has the renderer's existing conservative uniform-count semantics.
    /// </summary>
    internal static bool HasAnyInstances(int drawCount, ReadOnlySpan<int> cascadeCounts)
    {
        if (drawCount <= 0)
        {
            return false;
        }

        if (cascadeCounts.Length < CascadeCount)
        {
            return true;
        }

        for (var cascade = 0; cascade < CascadeCount; cascade++)
        {
            if (ClampInstanceCount(drawCount, cascadeCounts[cascade]) > 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasAnyInstances(
        int drawCount,
        int cascade0,
        int cascade1,
        int cascade2,
        int cascade3)
        => ClampInstanceCount(drawCount, cascade0) > 0
           || ClampInstanceCount(drawCount, cascade1) > 0
           || ClampInstanceCount(drawCount, cascade2) > 0
           || ClampInstanceCount(drawCount, cascade3) > 0;
}
