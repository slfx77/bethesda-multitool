using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

public sealed class RuntimeStringOwnershipAnalysis
{
    public List<RuntimeStringHit> AllHits { get; } = [];
    public List<RuntimeStringHit> OwnedHits { get; } = [];
    public List<RuntimeStringHit> ReferencedOwnerUnknownHits { get; } = [];
    public List<RuntimeStringHit> UnreferencedHits { get; } = [];
    public Dictionary<StringCategory, int> CategoryCounts { get; } = [];
    public Dictionary<RuntimeStringOwnershipStatus, int> StatusCounts { get; } = [];
    public Dictionary<ClaimSource, int> ClaimSourceCounts { get; } = [];
}
