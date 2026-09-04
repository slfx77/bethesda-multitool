using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     Buckets all runtime string hits by ownership status (owned / referenced-but-unknown / unreferenced) with
///     category and claim-source tallies.
/// </summary>
public sealed class RuntimeStringOwnershipAnalysis
{
    public List<RuntimeStringHit> AllHits { get; } = [];
    public List<RuntimeStringHit> OwnedHits { get; } = [];
    public List<RuntimeStringHit> ReferencedOwnerUnknownHits { get; } = [];
    public List<RuntimeStringHit> UnreferencedHits { get; } = [];
    public Dictionary<StringCategory, int> CategoryCounts { get; } = [];
    public Dictionary<RuntimeStringOwnershipStatus, int> StatusCounts { get; } = [];
    public Dictionary<ClaimSource, int> ClaimSourceCounts { get; } = [];

    /// <summary>
    ///     What runtime objects exist in the dump and how many of the still-unowned strings they
    ///     could account for. Null when the dump has no recoverable RTTI — a synthetic fixture, or a
    ///     dump whose game module was not captured.
    /// </summary>
    public RuntimeObjectCensus? ObjectCensus { get; set; }
}
