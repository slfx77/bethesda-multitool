using BethesdaMultitool.Core.WorldData;

namespace BethesdaRendererProfiler;

/// <summary>
///     Recognizes the capture-ready fixpoint: a clean scene census that remains exactly unchanged
///     across a caller-defined number of consecutive observations. The caller owns the observation
///     cadence; the profile harness samples every 250 ms.
/// </summary>
internal sealed class ProfileSceneSettlementTracker
{
    private readonly int _requiredConsecutive;
    private CaptureSceneCensus? _previous;

    internal ProfileSceneSettlementTracker(int requiredConsecutive = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredConsecutive);
        _requiredConsecutive = requiredConsecutive;
    }

    /// <summary>Number of consecutive clean observations equal to the preceding observation.</summary>
    internal int Consecutive { get; private set; }

    /// <summary>Number of clean, stable comparisons required before admission.</summary>
    internal int RequiredConsecutive => _requiredConsecutive;

    /// <summary>Most recent non-empty explanation for a reset, retained for timeout diagnostics.</summary>
    internal string LastDirt { get; private set; } = string.Empty;

    /// <summary>
    ///     Observes one census. The first observation establishes the comparison baseline and cannot
    ///     admit the scene. Dirty or changed observations reset the consecutive-match count.
    /// </summary>
    internal bool Observe(in CaptureSceneCensus census)
    {
        if (_previous is not { } previous)
        {
            Consecutive = 0;
            RetainDirt(census.DescribeDirt(census));
            _previous = census;
            return false;
        }

        if (census.IsClean && census == previous)
        {
            Consecutive++;
        }
        else
        {
            Consecutive = 0;
            RetainDirt(census.DescribeDirt(previous));
        }

        _previous = census;
        return Consecutive >= _requiredConsecutive;
    }

    private void RetainDirt(string dirt)
    {
        if (dirt.Length > 0)
        {
            LastDirt = dirt;
        }
    }
}
