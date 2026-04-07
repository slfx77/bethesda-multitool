namespace FalloutXbox360Utils.Core.Semantic;

/// <summary>
///     Options for loading a semantic analysis session from an ESM/ESP or DMP source.
/// </summary>
internal sealed record SemanticFileLoadOptions
{
    /// <summary>
    ///     Explicit file type override. When null, the loader auto-detects the source type.
    /// </summary>
    public AnalysisFileType? FileType { get; init; }

    /// <summary>
    ///     Progress sink for the format-specific analysis phase.
    /// </summary>
    public IProgress<AnalysisProgress>? AnalysisProgress { get; init; }

    /// <summary>
    ///     Progress sink for the semantic parsing phase.
    /// </summary>
    public IProgress<(int percent, string phase)>? ParseProgress { get; init; }

    /// <summary>
    ///     Whether minidump analysis should include metadata extraction.
    /// </summary>
    public bool IncludeMetadata { get; init; } = true;

    /// <summary>
    ///     Whether minidump analysis should run in verbose mode.
    /// </summary>
    public bool VerboseMinidumpAnalysis { get; init; }
}
