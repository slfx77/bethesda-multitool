using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>Buckets all runtime string hits by ownership status (owned / referenced-but-unknown / unreferenced) with category and claim-source tallies.</summary>
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
