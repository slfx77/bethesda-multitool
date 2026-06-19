using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>Aggregate output of a runtime-buffer exploration: manager walks, string hits/pools/ownership, discovered buffers, and the pointer graph.</summary>
public sealed class BufferExplorationResult
{
    public List<ManagerWalkResult> ManagerResults { get; } = [];
    public List<RuntimeStringHit> StringHits { get; } = [];
    public StringPoolSummary? StringPools { get; set; }
    public RuntimeStringOwnershipAnalysis? StringOwnership { get; set; }
    public List<DiscoveredBuffer> DiscoveredBuffers { get; } = [];
    public PointerGraphSummary? PointerGraph { get; set; }
}
