using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal sealed record CrossDumpComparisonResult
{
    public required CrossDumpRecordIndex Index { get; init; }
    public IReadOnlyList<SemanticSource> Sources { get; init; } = [];
    public IReadOnlyList<CrossDumpSourceSummary> SourceSummaries { get; init; } = [];
    public bool HasBaseBuild { get; init; }
}
