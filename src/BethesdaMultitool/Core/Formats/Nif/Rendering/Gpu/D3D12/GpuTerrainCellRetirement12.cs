using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Returns an exact retired cell entry through the existing queue's retryable cleanup.</summary>
/// <param name="entry">Exact candidate retained before native acquisition or eviction.</param>
/// <param name="updateStats">Refreshes creating-thread counters after a release attempt.</param>
internal sealed class GpuTerrainCellRetirement12(
    ResourceResidencyEntry<(int gx, int gy), GpuTerrainCellResources12> entry,
    Action updateStats) : IDisposable
{
    /// <summary>Releases only retired entries; successful current residents stay owned by their cache.</summary>
    /// <remarks>The queue must prove actual GPU completion. Failure leaves this holder queued for retry.</remarks>
    public void Dispose()
    {
        if (entry.State != ResourceResidencyState.Retiring) return;
        try { entry.ReleaseAfterRetirement(); }
        finally { updateStats(); }
    }
}
