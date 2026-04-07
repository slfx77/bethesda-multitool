using FalloutXbox360Utils.Core.Semantic;

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
        return await SemanticFileLoader.LoadAsync(
            filePath,
            new SemanticFileLoadOptions
            {
                AnalysisProgress = progress
            },
            cancellationToken);
    }
}
