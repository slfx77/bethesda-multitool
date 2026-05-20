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

    /// <summary>
    ///     Whether ESM/ESP analysis should emit verbose diagnostics.
    /// </summary>
    public bool VerboseEsmAnalysis { get; init; }

    /// <summary>
    ///     Explicit Cell FormID to Worldspace FormID authority map to apply to minidump
    ///     semantic records after parsing.
    /// </summary>
    public IReadOnlyDictionary<uint, uint>? CellWorldspaceAuthority { get; init; }

    /// <summary>
    ///     Optional worldspace FormID to EditorID/name map from the authority JSON. Used to
    ///     label synthesized worldspace records when a DMP has cells but no WRLD record.
    /// </summary>
    public IReadOnlyDictionary<uint, string>? CellWorldspaceAuthorityWorldspaceNames { get; init; }

    /// <summary>
    ///     Optional path to a cell authority JSON. When null and default authority loading is
    ///     enabled, the loader probes the packaged/current-directory default path.
    /// </summary>
    public string? CellWorldspaceAuthorityPath { get; init; }

    /// <summary>
    ///     Automatically apply the packaged/default authority JSON to minidump semantic loads
    ///     when no explicit authority map is provided.
    /// </summary>
    public bool ApplyDefaultCellWorldspaceAuthority { get; init; } = true;
}
