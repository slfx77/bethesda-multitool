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
        CoverageResult? coverage = null,
        AnalysisStages? stages = null)
    {
        if (result.MinidumpInfo == null)
        {
            return null;
        }

        stages ??= new AnalysisStages();
        coverage ??= stages.Run("coverage", stage =>
            CoverageAnalyzer.Analyze(result, accessor, stages.CancellationToken, stage));

        var bufferAnalyzer = new RuntimeBufferAnalyzer(
            accessor,
            result.FileSize,
            result.MinidumpInfo,
            coverage,
            coverage.PdbAnalysis,
            result.EsmRecords?.RuntimeEditorIds,
            result.EsmRecords?.GameSettings,
            result.EsmRecords?.MainRecords,
            stages);

        var stringData = bufferAnalyzer.ExtractStringDataOnly();
        RuntimeBufferAnalyzer.CrossReferenceWithCarvedFiles(stringData.StringPool, result.CarvedFiles);
        return stringData;
    }
}
