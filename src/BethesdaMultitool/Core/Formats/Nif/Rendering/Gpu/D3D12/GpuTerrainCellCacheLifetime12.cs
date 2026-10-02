using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Adapts the existing recorder and fence-delayed queue without introducing a second retirement clock.</summary>
/// <param name="RetireAfterFrame">Retains a candidate until the actual current recording retires, including uncertainty.</param>
/// <param name="Enlist">Reports the actual submission outcome to the exact residency entry.</param>
/// <param name="RetireAfterDelay">Retains an evicted cell until every earlier draw has retired.</param>
internal sealed record GpuTerrainCellCacheLifetime12(
    Action<IDisposable> RetireAfterFrame,
    Action<ISubmissionParticipant> Enlist,
    Action<IDisposable> RetireAfterDelay);
