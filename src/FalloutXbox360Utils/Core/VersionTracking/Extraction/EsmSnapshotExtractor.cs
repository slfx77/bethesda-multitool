using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Extraction;

/// <summary>
///     Extracts a VersionSnapshot from a complete ESM file.
/// </summary>
public static class EsmSnapshotExtractor
{
    /// <summary>
    ///     Extracts a VersionSnapshot from an ESM file.
    /// </summary>
    public static Task<VersionSnapshot> ExtractAsync(
        string esmPath,
        BuildInfo buildInfo,
        IProgress<(int percent, string phase)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return VersionSnapshotPipeline.ExtractAsync(
            esmPath,
            buildInfo,
            new VersionSnapshotPipelineOptions
            {
                FileType = AnalysisFileType.EsmFile,
                AnalysisPhaseLabel = "Analyzing ESM file...",
                AnalysisProgressWeight = 80,
                ParseProgressWeight = 15,
                IncludeMetadata = false,
                VerboseMinidumpAnalysis = false
            },
            progress,
            cancellationToken);
    }
}
