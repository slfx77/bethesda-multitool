using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Semantic;
using BethesdaMultitool.Core.VersionTracking.Models;

namespace BethesdaMultitool.Core.VersionTracking.Extraction;

/// <summary>Shared extraction pipeline that loads an ESM/DMP semantically and maps it to a <see cref="VersionSnapshot" />.</summary>
internal static class VersionSnapshotPipeline
{
    /// <summary>Loads <paramref name="filePath" /> per the options, then maps its records into a version snapshot, forwarding weighted progress.</summary>
    internal static async Task<VersionSnapshot> ExtractAsync(
        string filePath,
        BuildInfo buildInfo,
        VersionSnapshotPipelineOptions options,
        IProgress<(int percent, string phase)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report((0, options.AnalysisPhaseLabel));

        using var loaded = await SemanticFileLoader.LoadAsync(
            filePath,
            new SemanticFileLoadOptions
            {
                FileType = options.FileType,
                IncludeMetadata = options.IncludeMetadata,
                VerboseMinidumpAnalysis = options.VerboseMinidumpAnalysis,
                AnalysisProgress = progress != null
                    ? new Progress<AnalysisProgress>(p =>
                        progress.Report(((int)(p.PercentComplete * options.AnalysisProgressWeight), p.Phase)))
                    : null,
                ParseProgress = progress != null
                    ? new Progress<(int percent, string phase)>(p =>
                        progress.Report((
                            options.AnalysisProgressWeight + (int)(p.percent * (options.ParseProgressWeight / 100.0)),
                            p.phase)))
                    : null
            },
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return BuildFromLoadedSource(
            new SemanticSource
            {
                FilePath = loaded.FilePath,
                FileType = loaded.FileType,
                Records = loaded.Records,
                Resolver = loaded.Resolver,
                RawResult = loaded.RawResult,
                MinidumpInfo = loaded.RawResult.MinidumpInfo
            },
            buildInfo,
            progress,
            cancellationToken);
    }

    internal static VersionSnapshot BuildFromLoadedSource(
        SemanticSource source,
        BuildInfo buildInfo,
        IProgress<(int percent, string phase)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report((95, "Building snapshot..."));
        var snapshot = SnapshotMapper.MapToSnapshot(source.Records, buildInfo);
        progress?.Report((100, "Complete"));
        return snapshot;
    }
}
