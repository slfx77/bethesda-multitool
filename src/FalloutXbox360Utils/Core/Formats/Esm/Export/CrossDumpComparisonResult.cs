using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal sealed record CrossDumpComparisonResult
{
    public required CrossDumpRecordIndex Index { get; init; }
    public IReadOnlyList<SemanticSource> Sources { get; init; } = [];
    public IReadOnlyList<CrossDumpSourceSummary> SourceSummaries { get; init; } = [];
    public bool HasBaseBuild { get; init; }
}

internal sealed record CrossDumpStreamingComparisonResult
{
    public required IReadOnlyList<DumpSnapshot> Dumps { get; init; }
    public required IReadOnlyList<CrossDumpRecordTypeSummary> RecordTypes { get; init; }
    public required IReadOnlyList<string> WrittenFiles { get; init; }
    public IReadOnlyList<CrossDumpSourceSummary> SourceSummaries { get; init; } = [];
    public bool HasBaseBuild { get; init; }
}

internal sealed record CrossDumpSourceSummary(
    string FilePath,
    int WeaponCount,
    int NpcCount,
    int CellCount,
    string? SkillEraSummary);

internal sealed record CrossDumpRecordTypeSummary(
    string RecordType,
    int FormIdCount,
    IReadOnlyList<int> DumpCounts);
