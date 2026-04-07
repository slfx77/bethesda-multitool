namespace FalloutXbox360Utils.Core.Semantic;

/// <summary>
///     Descriptor for loading a semantic source into a shared source set.
/// </summary>
internal sealed record SemanticSourceRequest
{
    public required string FilePath { get; init; }
    public AnalysisFileType? FileType { get; init; }
    public bool IncludeMetadata { get; init; } = true;
    public bool VerboseMinidumpAnalysis { get; init; }
}
