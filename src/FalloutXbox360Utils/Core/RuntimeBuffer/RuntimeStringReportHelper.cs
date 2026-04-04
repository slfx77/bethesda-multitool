using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Coverage;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

internal static class RuntimeStringReportHelper
{
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
            result.EsmRecords?.GameSettings);

        var stringData = bufferAnalyzer.ExtractStringDataOnly();
        RuntimeBufferAnalyzer.CrossReferenceWithCarvedFiles(stringData.StringPool, result.CarvedFiles);
        return stringData;
    }
}
