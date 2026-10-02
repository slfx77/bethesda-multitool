using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Publishes one upload and retains failed preparation until that exact recording retires.</summary>
/// <param name="entry">Exact candidate reserved before native acquisition.</param>
/// <param name="updateStats">Refreshes logical ownership after failed preparation is released.</param>
internal sealed class GpuTerrainCellPreparation12(
    ResourceResidencyEntry<(int gx, int gy), GpuTerrainCellResources12> entry,
    Action updateStats) : ISubmissionParticipant, IDisposable
{
    private readonly GpuTerrainCellRetirement12 _retirement = new(entry, updateStats);
    private bool _published;

    /// <summary>Applies the actual recording outcome and permanently transfers published lifetime to the cache.</summary>
    /// <param name="outcome">Classification supplied by the existing submission owner.</param>
    public void OnSubmissionOutcome(SubmissionOutcome outcome)
    {
        entry.OnSubmissionOutcome(outcome);
        _published = entry.State == ResourceResidencyState.Resident;
    }

    /// <summary>Releases failed preparation after its recording retires; published cells need their later eviction proof.</summary>
    /// <remarks>Current entry state alone is insufficient: an older upload fence may complete after a later eviction.</remarks>
    public void Dispose()
    {
        if (!_published) _retirement.Dispose();
    }
}
