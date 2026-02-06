using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

public sealed class BufferExplorationResult
{
    public List<ManagerWalkResult> ManagerResults { get; } = [];
    public StringPoolSummary? StringPools { get; set; }
    public List<DiscoveredBuffer> DiscoveredBuffers { get; } = [];
    public PointerGraphSummary? PointerGraph { get; set; }
}
