using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Extraction;

/// <summary>
///     Extracts a VersionSnapshot from a memory dump file.
///     Uses MinidumpAnalyzer → RecordParser → RecordCollection → SnapshotMapper pipeline.
/// </summary>
public static class DmpSnapshotExtractor
{
    /// <summary>
    ///     Extracts a VersionSnapshot from a DMP file.
    /// </summary>
    public static async Task<VersionSnapshot> ExtractAsync(
        string dmpPath,
        BuildInfo buildInfo,
        IProgress<(int percent, string phase)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report((0, "Analyzing memory dump..."));

        // Use existing MinidumpAnalyzer pipeline (instance method)
        var analyzer = new MinidumpAnalyzer();
        var analysisProgress = progress != null
            ? new Progress<AnalysisProgress>(p =>
                progress.Report(((int)(p.PercentComplete * 0.7), p.Phase)))
            : null;

        var analysisResult = await analyzer.AnalyzeAsync(dmpPath, analysisProgress, true, false, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (analysisResult.EsmRecords == null)
        {
            throw new InvalidOperationException($"No ESM records found in DMP: {Path.GetFileName(dmpPath)}");
        }

        progress?.Report((70, "Reconstructing records..."));

        // Create RecordParser with DMP data
        using var mmf = MemoryMappedFile.CreateFromFile(dmpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, analysisResult.FileSize, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords,
            analysisResult.FormIdMap,
            accessor,
            analysisResult.FileSize,
            analysisResult.MinidumpInfo);

        var records = parser.ReconstructAll(progress != null
            ? new Progress<(int percent, string phase)>(p =>
                progress!.Report((70 + (int)(p.percent * 0.25), p.phase)))
            : null);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report((95, "Building snapshot..."));

        var snapshot = SnapshotMapper.MapToSnapshot(records, buildInfo);

        progress?.Report((100, "Complete"));
        return snapshot;
    }
}
