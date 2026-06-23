using BethesdaMultitool.Core.Semantic;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Comparison;

internal sealed record CrossDumpComparisonResult
{
    public required CrossDumpRecordIndex Index { get; init; }
    public IReadOnlyList<SemanticSource> Sources { get; init; } = [];
    public IReadOnlyList<CrossDumpSourceSummary> SourceSummaries { get; init; } = [];
    public bool HasBaseBuild { get; init; }
}
