namespace FalloutXbox360Utils.Core;

public sealed class BufferExplorationResult
{
    public List<ManagerWalkResult> ManagerResults { get; } = [];
    public StringPoolSummary? StringPools { get; set; }
    public List<DiscoveredBuffer> DiscoveredBuffers { get; } = [];
    public PointerGraphSummary? PointerGraph { get; set; }
}