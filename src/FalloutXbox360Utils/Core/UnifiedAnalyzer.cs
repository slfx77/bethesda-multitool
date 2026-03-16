using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Format-agnostic file analysis. Auto-detects file type (ESM, DMP, save),
///     runs the appropriate analyzer, and produces a unified RecordCollection.
///     Mirrors what the GUI does in SingleFileTab.Analysis.cs.
/// </summary>
public static class UnifiedAnalyzer
{
    /// <summary>
    ///     Analyze any supported file and return parsed records.
    ///     The result is IDisposable — caller must dispose when done.
    /// </summary>
    public static async Task<UnifiedAnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fileType = FileTypeDetector.Detect(filePath);
        if (fileType == AnalysisFileType.Unknown)
        {
            throw new InvalidOperationException(
                $"Unrecognized file format: {Path.GetFileName(filePath)}. " +
                "Supported formats: ESM/ESP (TES4/4SET), DMP (MDMP), FXS/FOS (save files).");
        }

        // Phase 1: Run format-specific analyzer
        var analysisResult = fileType switch
        {
            AnalysisFileType.EsmFile => await EsmFileAnalyzer.AnalyzeAsync(filePath, progress, cancellationToken),
            AnalysisFileType.Minidump => await new MinidumpAnalyzer().AnalyzeAsync(
                filePath, progress, true, false, cancellationToken),
            AnalysisFileType.SaveFile => throw new NotSupportedException(
                "Save file analysis via UnifiedAnalyzer is not yet supported. Use 'save' commands instead."),
            _ => throw new InvalidOperationException($"Unsupported file type: {fileType}")
        };

        if (analysisResult.EsmRecords == null)
        {
            throw new InvalidOperationException(
                $"No records found in {Path.GetFileName(filePath)}. The file may be empty or corrupted.");
        }

        // Phase 2: Semantic parsing (same as GUI)
        var fileInfo = new FileInfo(filePath);
        var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var parser = new RecordParser(
            analysisResult.EsmRecords, analysisResult.FormIdMap,
            accessor, fileInfo.Length, analysisResult.MinidumpInfo);
        var records = parser.ParseAll();
        var resolver = records.CreateResolver();

        var result = new UnifiedAnalysisResult
        {
            FileType = fileType,
            Records = records,
            Resolver = resolver,
            RawResult = analysisResult,
            FilePath = filePath
        };
        result.SetDisposables(mmf, accessor);

        return result;
    }
}
