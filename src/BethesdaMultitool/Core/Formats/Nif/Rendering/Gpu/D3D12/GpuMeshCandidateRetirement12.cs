using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Transfers exact retired mesh ownership from a completed recording or eviction delay to cache-owned cleanup.</summary>
/// <remarks>Registered before native acquisition or while queueing eviction. Resident entries remain
/// cache-owned. Transfer does not release resources or reject still-live batch, skinner or recording
/// pins; the cache retries actual cleanup after those users return their pins.</remarks>
internal sealed class GpuMeshCandidateRetirement12(
    ResourceResidencyEntry<string, GpuMeshResources12> entry,
    RetiredResourceDisposal deferredReleases) : IDisposable
{
    private bool _transferred;

    /// <summary>Transfers a retiring entry once; failed admission leaves this holder responsible for retry.</summary>
    /// <remarks>Released and still-current entries are no-ops. Every call verifies creating-thread
    /// access through the exact entry state before inspecting the local transfer flag.</remarks>
    public void Dispose()
    {
        if (entry.State != ResourceResidencyState.Retiring || _transferred) return;
        deferredReleases.Add(entry, "retired mesh residency entry");
        _transferred = true;
    }
}
