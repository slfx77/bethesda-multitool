using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Coverage;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>Convenience entry point that runs runtime-buffer string-pool + ownership analysis for an analyzed dump.</summary>
internal static class RuntimeStringReportHelper
{
    /// <summary>
    ///     Builds the string-pool summary and ownership analysis for <paramref name="result" />, computing coverage if
    ///     not supplied; returns null when the file is not a minidump.
    /// </summary>
    internal static RuntimeStringReportData? Extract(
        AnalysisResult result,
        MemoryMappedViewAccessor accessor,
        CoverageResult? coverage = null)
    {
        if (result.MinidumpInfo == null)
        {
            return null;
        }

        coverage ??= CoverageAnalyzer.Analyze(result, accessor);

        var bufferAnalyzer = new RuntimeBufferAnalyzer(
            accessor,
            result.FileSize,
            result.MinidumpInfo,
            coverage,
            coverage.PdbAnalysis,
            result.EsmRecords?.RuntimeEditorIds,
            result.EsmRecords?.GameSettings,
            result.EsmRecords?.MainRecords);

        var stringData = bufferAnalyzer.ExtractStringDataOnly();
        RuntimeBufferAnalyzer.CrossReferenceWithCarvedFiles(stringData.StringPool, result.CarvedFiles);
        return stringData;
    }
}
