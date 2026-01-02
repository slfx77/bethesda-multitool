using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core.Carving;
using Xbox360MemoryCarver.Core.Extractors;
using Xbox360MemoryCarver.Core.Minidump;

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

        Directory.CreateDirectory(options.OutputPath);

        // Extract modules from minidump first
        var moduleCount = await ExtractModulesAsync(filePath, options, progress);

        // Create carver with options for signature-based extraction
        using var carver = new MemoryCarver(
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
                PercentComplete = p * 80, // 0-80% for carving
                CurrentOperation = "Extracting files..."
            }))
            : null;

        // Perform extraction using the full carver
        var entries = await carver.CarveDumpAsync(filePath, carverProgress);

        // Build set of extracted offsets for UI update
        var extractedOffsets = entries
            .Select(e => e.Offset)
            .ToHashSet();

        // Extract compiled scripts if requested
        var scriptResult = new ScriptExtractor.ScriptExtractionResult();
        if (options.ExtractScripts)
        {
            scriptResult = await ExtractScriptsAsync(filePath, options, progress);
        }

        // Return summary
        return new ExtractionSummary
        {
            TotalExtracted = entries.Count + moduleCount + scriptResult.GroupedQuests + scriptResult.UngroupedScripts,
            DdxConverted = carver.DdxConvertedCount,
            DdxFailed = carver.DdxConvertFailedCount,
            TypeCounts = carver.Stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExtractedOffsets = extractedOffsets,
            ModulesExtracted = moduleCount,
            ScriptsExtracted = scriptResult.TotalRecords,
            ScriptQuestsGrouped = scriptResult.GroupedQuests
        };
    }

    /// <summary>
    ///     Extract compiled scripts (SCDA records) from the dump.
    /// </summary>
    private static async Task<ScriptExtractor.ScriptExtractionResult> ExtractScriptsAsync(
        string filePath,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress)
    {
        progress?.Report(new ExtractionProgress
        {
            PercentComplete = 85,
            CurrentOperation = "Scanning for compiled scripts..."
        });

        // Read the entire dump for SCDA scanning
        var dumpData = await File.ReadAllBytesAsync(filePath);

        // Create scripts output directory
        var dumpName = Path.GetFileNameWithoutExtension(filePath);
        var sanitizedName = SanitizeFilename(dumpName);
        var scriptsDir = Path.Combine(options.OutputPath, sanitizedName, "scripts");

        var stringProgress = progress != null
            ? new Progress<string>(msg => progress.Report(new ExtractionProgress
            {
                PercentComplete = 90,
                CurrentOperation = msg
            }))
            : null;

        var result = await ScriptExtractor.ExtractGroupedAsync(dumpData, scriptsDir, stringProgress, options.Verbose);

        progress?.Report(new ExtractionProgress
        {
            PercentComplete = 100,
            CurrentOperation = $"Extracted {result.TotalRecords} scripts ({result.GroupedQuests} quests)"
        });

        return result;
    }

    /// <summary>
    ///     Extract modules from minidump metadata.
    /// </summary>
    private static async Task<int> ExtractModulesAsync(
        string filePath,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress)
    {
        // Check if module extraction is requested
        if (options.FileTypes != null && !options.FileTypes.Contains("xex")) return 0;

        var minidumpInfo = MinidumpParser.Parse(filePath);
        if (!minidumpInfo.IsValid || minidumpInfo.Modules.Count == 0) return 0;

        // Create modules output directory matching the MemoryCarver pattern:
        // {output_dir}/{dmp_filename}/modules/
        var dumpName = Path.GetFileNameWithoutExtension(filePath);
        var sanitizedName = SanitizeFilename(dumpName);
        var modulesDir = Path.Combine(options.OutputPath, sanitizedName, "modules");
        Directory.CreateDirectory(modulesDir);

        var extractedCount = 0;
        var fileInfo = new FileInfo(filePath);

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        foreach (var module in minidumpInfo.Modules)
        {
            var fileRange = minidumpInfo.GetModuleFileRange(module);
            if (!fileRange.HasValue || fileRange.Value.size <= 0)
                continue;

            var fileName = Path.GetFileName(module.Name);
            var outputPath = Path.Combine(modulesDir, fileName);

            // Handle duplicate filenames
            var counter = 1;
            while (File.Exists(outputPath))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                outputPath = Path.Combine(modulesDir, $"{nameWithoutExt}_{counter++}{ext}");
            }

            try
            {
                var size = (int)Math.Min(fileRange.Value.size, fileInfo.Length - fileRange.Value.fileOffset);
                var buffer = new byte[size];
                accessor.ReadArray(fileRange.Value.fileOffset, buffer, 0, size);

                await File.WriteAllBytesAsync(outputPath, buffer);
                extractedCount++;

                if (options.Verbose) Console.WriteLine($"[Module] Extracted {fileName} ({size:N0} bytes)");

                progress?.Report(new ExtractionProgress
                {
                    CurrentOperation = $"Extracting module: {fileName}",
                    FilesProcessed = extractedCount,
                    TotalFiles = minidumpInfo.Modules.Count
                });
            }
            catch (Exception ex)
            {
                if (options.Verbose) Console.WriteLine($"[Module] Failed to extract {fileName}: {ex.Message}");
            }
        }

        return extractedCount;
    }

    /// <summary>
    ///     Sanitize a filename by removing invalid characters.
    /// </summary>
    private static string SanitizeFilename(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new char[name.Length];
        for (var i = 0; i < name.Length; i++) sanitized[i] = Array.IndexOf(invalidChars, name[i]) >= 0 ? '_' : name[i];
        return new string(sanitized);
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
    public int ModulesExtracted { get; init; }
    public int ScriptsExtracted { get; init; }
    public int ScriptQuestsGrouped { get; init; }
    public Dictionary<string, int> TypeCounts { get; init; } = [];
    public HashSet<long> ExtractedOffsets { get; init; } = [];
}
