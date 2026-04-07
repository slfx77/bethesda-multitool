using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Semantic;

/// <summary>
///     Canonical semantic loading path for ESM/ESP and DMP sources.
/// </summary>
internal static class SemanticFileLoader
{
    internal static AnalysisFileType ResolveSemanticFileType(
        string filePath,
        AnalysisFileType? fileTypeOverride = null)
    {
        var fileType = fileTypeOverride ?? FileTypeDetector.Detect(filePath);
        if (fileType == AnalysisFileType.Unknown)
        {
            throw new InvalidOperationException(
                $"Unrecognized file format: {Path.GetFileName(filePath)}. " +
                "Supported semantic formats: ESM/ESP (TES4/4SET) and DMP (MDMP).");
        }

        if (fileType == AnalysisFileType.SaveFile)
        {
            throw new NotSupportedException(
                "Save file analysis is not supported by the semantic loader. Use the save-specific pipeline instead.");
        }

        return fileType;
    }

    internal static async Task<UnifiedAnalysisResult> LoadAsync(
        string filePath,
        SemanticFileLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SemanticFileLoadOptions();

        var fileType = ResolveSemanticFileType(filePath, options.FileType);
        var analysisResult = await AnalyzeOnlyAsync(filePath, options, cancellationToken);
        return LoadFromAnalysisResult(filePath, analysisResult, fileType, options.ParseProgress);
    }

    internal static async Task<AnalysisResult> AnalyzeOnlyAsync(
        string filePath,
        SemanticFileLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SemanticFileLoadOptions();

        var fileType = ResolveSemanticFileType(filePath, options.FileType);
        return await AnalyzeAsync(filePath, fileType, options, cancellationToken);
    }

    internal static UnifiedAnalysisResult LoadFromAnalysisResult(
        string filePath,
        AnalysisResult analysisResult,
        AnalysisFileType fileType,
        IProgress<(int percent, string phase)>? parseProgress = null)
    {
        fileType = ResolveSemanticFileType(filePath, fileType);
        if (analysisResult.EsmRecords == null)
        {
            throw new InvalidOperationException(
                $"No records found in {Path.GetFileName(filePath)}. The file may be empty or corrupted.");
        }

        var fileInfo = new FileInfo(filePath);
        var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        try
        {
            var parser = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);
            var records = parser.ParseAll(parseProgress);
            var resolver = records.CreateResolver(analysisResult.FormIdMap);

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
        catch
        {
            accessor.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    private static async Task<AnalysisResult> AnalyzeAsync(
        string filePath,
        AnalysisFileType fileType,
        SemanticFileLoadOptions options,
        CancellationToken cancellationToken)
    {
        return fileType switch
        {
            AnalysisFileType.EsmFile => await EsmFileAnalyzer.AnalyzeAsync(
                filePath,
                options.AnalysisProgress,
                cancellationToken),
            AnalysisFileType.Minidump => await new MinidumpAnalyzer().AnalyzeAsync(
                filePath,
                options.AnalysisProgress,
                options.IncludeMetadata,
                options.VerboseMinidumpAnalysis,
                cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported semantic file type: {fileType}")
        };
    }
}
