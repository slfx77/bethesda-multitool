namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Top-level aggregated census report across all DMP files.
/// </summary>
public sealed class AggregatedCensusReport
{
    public int TotalDumps { get; init; }
    public int TotalClasses { get; init; }
    public long TotalInstances { get; init; }
    public int TesFormClasses { get; init; }
    public required List<AggregatedCensusEntry> Classes { get; init; }
    public required List<DumpCensusResult> Dumps { get; init; }
}