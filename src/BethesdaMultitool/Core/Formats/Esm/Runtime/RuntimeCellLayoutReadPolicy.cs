namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

/// <summary>
///     Decides whether runtime world/cell structural reads are safe for a selected layout.
/// </summary>
internal static class RuntimeCellLayoutReadPolicy
{
    internal const int HighAbsoluteScoreThreshold = 100;

    /// <summary>
    ///     Returns true if structural world/cell reads are safe for the selected layout —
    ///     always for proto offsets, otherwise only when the probe is high-confidence or scores high enough.
    /// </summary>
    public static bool ShouldAllowStructuralReads(
        bool useProtoOffsets,
        RuntimeWorldCellLayoutProbeResult? probe)
    {
        if (useProtoOffsets)
        {
            return true;
        }

        if (probe is { IsHighConfidence: true })
        {
            return true;
        }

        // Low margin but strong absolute score: the winner is plausible in isolation.
        if (probe is { WinnerScore: >= HighAbsoluteScoreThreshold })
        {
            return true;
        }

        return probe?.IsHighConfidence != false;
    }
}
