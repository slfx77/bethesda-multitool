using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Extraction;

/// <summary>
///     Extracts a VersionSnapshot from a memory dump file.
/// </summary>
public static class DmpSnapshotExtractor
{
    /// <summary>
    ///     Extracts a VersionSnapshot from a DMP file.
    /// </summary>
    public static Task<VersionSnapshot> ExtractAsync(
        string dmpPath,
        BuildInfo buildInfo,
        IProgress<(int percent, string phase)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return VersionSnapshotPipeline.ExtractAsync(
            dmpPath,
            buildInfo,
            new VersionSnapshotPipelineOptions
            {
                FileType = AnalysisFileType.Minidump,
                AnalysisPhaseLabel = "Analyzing memory dump...",
                AnalysisProgressWeight = 70,
                ParseProgressWeight = 25,
                IncludeMetadata = true,
                VerboseMinidumpAnalysis = false
            },
            progress,
            cancellationToken);
    }
}
