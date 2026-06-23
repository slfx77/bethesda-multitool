namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>
///     Best-effort raw EOF-aligned region candidate for the trailing `0x38`
///     statistical family. In the current anchor samples this region is a
///     contiguous chain of `nameLen -> name -> count -> uint32[count]` records.
/// </summary>
internal readonly record struct TriTrailingStatisticalRegionCandidate(
    int Offset,
    int Length,
    int RecordCount,
    int AggregatePayloadCount,
    bool HeaderWord2CMatchesAggregatePayloadCount,
    TriTrailingStatisticalRecordCandidate[] Records);
