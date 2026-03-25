namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Best-effort raw candidate for the still-unsettled early fixed-width `0x0C`
///     region that begins immediately after the first confirmed vertex block.
///     Current anchor samples suggest this region is not a uniform second float3
///     block. Instead, it appears to contain a leading float3 subregion keyed by
///     header word `0x2C`, followed by a `vertexCount`-sized table of 12-byte
///     triplets whose exact semantics are still under investigation. The triplet
///     rows often stay in mesh-index space, but outlier samples can fall out of
///     that domain near the end of the candidate region.
/// </summary>
internal readonly record struct TriEarlyMixedRegionCandidate(
    int Offset,
    int Length,
    int LeadingFloat3CountHint,
    int LeadingFloat3Offset,
    int LeadingFloat3Length,
    int TripletCountHint,
    int TripletOffset,
    int TripletLength,
    uint TripletMinObservedValue,
    uint TripletMaxObservedValue,
    int UniqueTripletValueCount,
    int TripletValueCountBelowLeadingFloat3CountHint,
    int TripletValueCountBelowTripletCountHint,
    int MeshIndexTripletRowCount,
    int ContiguousMeshIndexTripletRowCount,
    int FirstNonMeshIndexTripletRowIndex,
    int SuccessiveTripletRowsSharingTwoOrMoreValuesCount);
