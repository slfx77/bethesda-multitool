namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal sealed record CrossDumpStreamingComparisonResult
{
    public required IReadOnlyList<DumpSnapshot> Dumps { get; init; }
    public required IReadOnlyList<CrossDumpRecordTypeSummary> RecordTypes { get; init; }
    public required IReadOnlyList<string> WrittenFiles { get; init; }
    public IReadOnlyList<CrossDumpSourceSummary> SourceSummaries { get; init; } = [];
    public bool HasBaseBuild { get; init; }
}
