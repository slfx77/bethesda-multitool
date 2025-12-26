using Xbox360MemoryCarver.Core.Carving;

namespace Xbox360MemoryCarver.Core;

/// <summary>
///     Extracts files from memory dumps based on analysis results.
///     Uses the MemoryCarver for actual extraction with proper DDX handling.
/// </summary>
public static class MemoryDumpExtractor
{
    /// <summary>
    ///     Extract files from a memory dump based on prior analysis.
    /// </summary>
    public static async Task<ExtractionSummary> Extract(
        string filePath,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Create carver with options
        var carver = new MemoryCarver(
            options.OutputPath,
            options.MaxFilesPerType,
            options.ConvertDdx,
            options.FileTypes,
            options.Verbose,
            options.SaveAtlas);

        // Progress wrapper
        var carverProgress = progress != null
            ? new Progress<double>(p => progress.Report(new ExtractionProgress
            {
                PercentComplete = p * 100, CurrentOperation = "Extracting files..."
            }))
            : null;

        // Perform extraction using the full carver
        var entries = await carver.CarveDumpAsync(filePath, carverProgress);

        // Return summary
        return new ExtractionSummary
        {
            TotalExtracted = entries.Count,
            DdxConverted = carver.DdxConvertedCount,
            DdxFailed = carver.DdxConvertFailedCount,
            TypeCounts = carver.Stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
    }
}

/// <summary>
///     Summary of extraction results.
/// </summary>
public class ExtractionSummary
{
    public int TotalExtracted { get; init; }
    public int DdxConverted { get; init; }
    public int DdxFailed { get; init; }
    public Dictionary<string, int> TypeCounts { get; init; } = [];
}
