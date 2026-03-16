namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Per-dump census result with metadata about the dump file.
/// </summary>
public sealed class DumpCensusResult
{
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public string? BuildType { get; init; }
    public uint? PeTimestamp { get; init; }
    public int ClassCount { get; init; }
    public int TotalInstances { get; init; }
    public required List<CensusEntry> Entries { get; init; }
}