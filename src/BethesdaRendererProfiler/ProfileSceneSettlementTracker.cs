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
        return ObserveCore(census, acceptFrameCeilingMaintenance: false, referenceBatchBuildTrigger: 0);
    }

    /// <summary>
    ///     Capture-only observation that admits an otherwise-clean FrameCeiling maintenance sweep.
    ///     FrameCeiling periodically rebuilds the already-published reference batches by design; it
    ///     is not unfinished scene demand. Every other pending term remains fatal to the streak, and
    ///     <see cref="Observe" /> stays strict for live-profile settlement.
    /// </summary>
    internal bool ObserveForCapture(in CaptureSceneCensus census, int referenceBatchBuildTrigger)
    {
        return ObserveCore(
            census,
            acceptFrameCeilingMaintenance: true,
            referenceBatchBuildTrigger: referenceBatchBuildTrigger);
    }

    private bool ObserveCore(
        in CaptureSceneCensus census,
        bool acceptFrameCeilingMaintenance,
        int referenceBatchBuildTrigger)
    {
        var comparable = acceptFrameCeilingMaintenance &&
                         census.IsCleanOrFrameCeilingMaintenance(referenceBatchBuildTrigger)
            ? census with { ReferenceBatchBuildInProgress = false }
            : census;

        if (_previous is not { } previous)
        {
            Consecutive = 0;
            RetainDirt(comparable.DescribeDirt(comparable));
            _previous = comparable;
            return false;
        }

        if (comparable.IsClean && comparable == previous)
        {
            Consecutive++;
        }
        else
        {
            Consecutive = 0;
            RetainDirt(comparable.DescribeDirt(previous));
        }

        _previous = comparable;
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
