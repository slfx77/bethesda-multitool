namespace FalloutXbox360Utils.Core.VersionTracking.Extraction;

internal sealed record VersionSnapshotPipelineOptions
{
    public required AnalysisFileType FileType { get; init; }
    public required string AnalysisPhaseLabel { get; init; }
    public int AnalysisProgressWeight { get; init; } = 80;
    public int ParseProgressWeight { get; init; } = 15;
    public bool IncludeMetadata { get; init; }
    public bool VerboseMinidumpAnalysis { get; init; }
}