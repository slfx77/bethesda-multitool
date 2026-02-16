using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Extraction;

/// <summary>
///     Extracts a VersionSnapshot from a complete ESM file.
///     Uses EsmFileAnalyzer → RecordParser → RecordCollection → SnapshotMapper pipeline.
/// </summary>
public static class EsmSnapshotExtractor
{
    /// <summary>
    ///     Extracts a VersionSnapshot from an ESM file.
    /// </summary>
    public static async Task<VersionSnapshot> ExtractAsync(
        string esmPath,
        BuildInfo buildInfo,
        IProgress<(int percent, string phase)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report((0, "Analyzing ESM file..."));

        // Use existing EsmFileAnalyzer pipeline
        var analysisProgress = progress != null
            ? new Progress<AnalysisProgress>(p =>
                progress.Report(((int)(p.PercentComplete * 0.8), p.Phase)))
            : null;

        var analysisResult = await EsmFileAnalyzer.AnalyzeAsync(esmPath, analysisProgress, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (analysisResult.EsmRecords == null)
        {
            throw new InvalidOperationException($"No ESM records found in: {Path.GetFileName(esmPath)}");
        }

        progress?.Report((80, "Reconstructing records..."));

        // Create RecordParser and reconstruct all records
        var fileInfo = new FileInfo(esmPath);
        using var mmf = MemoryMappedFile.CreateFromFile(esmPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords,
            analysisResult.FormIdMap,
            accessor,
            fileInfo.Length);

        var records = parser.ReconstructAll(progress != null
            ? new Progress<(int percent, string phase)>(p =>
                progress!.Report((80 + (int)(p.percent * 0.15), p.phase)))
            : null);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report((95, "Building snapshot..."));

        var snapshot = SnapshotMapper.MapToSnapshot(records, buildInfo);

        progress?.Report((100, "Complete"));
        return snapshot;
    }
}
