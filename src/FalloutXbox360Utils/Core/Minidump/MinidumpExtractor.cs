using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Carving;
using FalloutXbox360Utils.Core.Extraction;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Ddx;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Extracts files from memory dumps based on analysis results.
///     Uses the MemoryCarver for actual extraction with proper DDX handling.
/// </summary>
public static class MinidumpExtractor
{
    /// <summary>
    ///     Extract files from a memory dump based on prior analysis.
    /// </summary>
    /// <param name="filePath">Path to the memory dump file.</param>
    /// <param name="options">Extraction options.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="analysisResult">Optional analysis result for ESM report generation.</param>
    public static async Task<ExtractionSummary> Extract(
        string filePath,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress,
        AnalysisResult? analysisResult = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(options.OutputPath);

        var dumpName = Path.GetFileNameWithoutExtension(filePath);
        var sanitizedName = BinaryUtils.SanitizeFilename(dumpName);
        var extractDir = Path.Combine(options.OutputPath, sanitizedName);

        // Extract modules from minidump first
        var (moduleCount, moduleOffsets) = await ExtractModulesAsync(filePath, options, progress);

        // Always skip inline DDX conversion - batch conversion after carving is much faster
        var skipDdxConversion = options.ConvertDdx;

        // Create carver with options for signature-based extraction
        using var carver = new MemoryCarver(
            options.OutputPath,
            options.MaxFilesPerType,
            options.ConvertDdx,
            options.FileTypes,
            options.Verbose,
            options.SaveAtlas,
            skipDdxConversion);

        // Progress wrapper
        var carverProgress = progress != null
            ? new Progress<double>(p => progress.Report(new ExtractionProgress
            {
                PercentComplete = p * 60, // 0-60% for carving
                CurrentOperation = "Extracting files..."
            }))
            : null;

        // Perform extraction using the full carver
        var entries = await carver.CarveDumpAsync(filePath, carverProgress);

        // Build set of extracted offsets for UI update
        var extractedOffsets = entries
            .Select(e => e.Offset)
            .ToHashSet();

        // Build set of failed conversion offsets
        var failedConversionOffsets = carver.FailedConversionOffsets.ToHashSet();

        // Batch DDX conversion -- always use batch mode (much faster than per-file subprocess)
        var ddxConverted = carver.DdxConvertedCount;
        var ddxFailed = carver.DdxConvertFailedCount;
        if (options.ConvertDdx)
        {
            var ddxDir = Path.Combine(extractDir, "ddx");
            var hasDdxToConvert = Directory.Exists(ddxDir) &&
                                  Directory.EnumerateFiles(ddxDir, "*.ddx").Any();

            if (hasDdxToConvert)
            {
                var (batchResult, batchFailedOffsets) = await BatchConvertDdxAsync(
                    extractDir, options.PcFriendly, progress, entries);
                ddxConverted = batchResult.Converted;
                ddxFailed = batchResult.Failed;

                // Merge batch failure offsets into the overall failure set for UI reporting
                foreach (var offset in batchFailedOffsets)
                {
                    failedConversionOffsets.Add(offset);
                }
            }
            else if (options.Verbose)
            {
                Console.WriteLine("[DDX] No DDX files to convert");
            }
        }

        // Generate ESM reports, heightmaps, and runtime asset exports
        var esmReportGenerated = false;
        var heightmapsExported = 0;
        var scriptsExtracted = 0;
        var runtimeTexturesExported = 0;
        var runtimeMeshesExported = 0;
        if (options.GenerateEsmReports && analysisResult?.EsmRecords != null)
        {
            var (reportGen, heightmaps, scripts, textures, meshes, semanticResult) =
                await MinidumpExtractionReporter.GenerateEsmOutputsAsync(
                    analysisResult, filePath, extractDir, progress);
            esmReportGenerated = reportGen;
            heightmapsExported = heightmaps;
            scriptsExtracted = scripts;
            runtimeTexturesExported = textures;
            runtimeMeshesExported = meshes;

            // Post-carve enrichment: rename XMA files using SOUN EditorIDs
            if (semanticResult != null)
            {
                await MinidumpExtractionReporter.EnrichCarvedSoundNames(entries, extractDir, semanticResult);
            }
        }

        // Return summary
        return new ExtractionSummary
        {
            TotalExtracted = entries.Count + moduleCount,
            DdxConverted = ddxConverted,
            DdxFailed = ddxFailed,
            TypeCounts = carver.Stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExtractedOffsets = extractedOffsets,
            FailedConversionOffsets = failedConversionOffsets,
            ExtractedModuleOffsets = moduleOffsets,
            ModulesExtracted = moduleCount,
            EsmReportGenerated = esmReportGenerated,
            HeightmapsExported = heightmapsExported,
            ScriptsExtracted = scriptsExtracted,
            RuntimeTexturesExported = runtimeTexturesExported,
            RuntimeMeshesExported = runtimeMeshesExported
        };
    }

    /// <summary>
    ///     Extract modules from minidump metadata.
    /// </summary>
    private static async Task<(int count, HashSet<long> offsets)> ExtractModulesAsync(
        string filePath,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress)
    {
        var extractedOffsets = new HashSet<long>();

        // Modules are always extracted from minidump metadata
        // (they don't use signature scanning, so they bypass the file type filter)

        var minidumpInfo = MinidumpParser.Parse(filePath);
        if (!minidumpInfo.IsValid)
        {
            if (options.Verbose)
            {
                Console.WriteLine("[Module] Minidump is not valid");
            }

            return (0, extractedOffsets);
        }

        if (minidumpInfo.Modules.Count == 0)
        {
            if (options.Verbose)
            {
                Console.WriteLine("[Module] No modules found in minidump");
            }

            return (0, extractedOffsets);
        }

        if (options.Verbose)
        {
            Console.WriteLine($"[Module] Found {minidumpInfo.Modules.Count} modules in minidump");
        }

        // Create modules output directory matching the MemoryCarver pattern:
        // {output_dir}/{dmp_filename}/modules/
        var dumpName = Path.GetFileNameWithoutExtension(filePath);
        var sanitizedName = BinaryUtils.SanitizeFilename(dumpName);
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
            {
                if (options.Verbose)
                {
                    Console.WriteLine($"[Module] Skipping {Path.GetFileName(module.Name)} - not captured in dump");
                }

                continue;
            }

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
                extractedOffsets.Add(fileRange.Value.fileOffset);

                if (options.Verbose)
                {
                    Console.WriteLine($"[Module] Extracted {fileName} ({size:N0} bytes)");
                }

                progress?.Report(new ExtractionProgress
                {
                    CurrentOperation = $"Extracting module: {fileName}",
                    FilesProcessed = extractedCount,
                    TotalFiles = minidumpInfo.Modules.Count
                });
            }
            catch (Exception ex)
            {
                if (options.Verbose)
                {
                    Console.WriteLine($"[Module] Failed to extract {fileName}: {ex.Message}");
                }
            }
        }

        return (extractedCount, extractedOffsets);
    }

    /// <summary>
    ///     Batch convert DDX files to DDS with pc-friendly option.
    ///     Tracks failed conversion offsets for UI reporting.
    /// </summary>
    private static async Task<(BatchConversionResult result, HashSet<long> failedOffsets)> BatchConvertDdxAsync(
        string extractDir,
        bool pcFriendly,
        IProgress<ExtractionProgress>? progress,
        IReadOnlyList<CarveEntry> manifest)
    {
        var failedOffsets = new HashSet<long>();

        // DDX files are saved to "ddx" folder during carving (DdxFormat.OutputFolder = "ddx")
        // Convert them and output to "textures" folder
        var ddxDir = Path.Combine(extractDir, "ddx");
        var texturesDir = Path.Combine(extractDir, "textures");

        if (!Directory.Exists(ddxDir))
        {
            return (new BatchConversionResult(), failedOffsets);
        }

        progress?.Report(new ExtractionProgress
        {
            PercentComplete = 65,
            CurrentOperation = pcFriendly
                ? "Converting textures with PC-friendly normal maps..."
                : "Converting textures..."
        });

        try
        {
            // Build filename -> offset map from manifest for DDX entries
            var filenameToOffset = manifest
                .Where(e => e.FileType.StartsWith("ddx", StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => e.Filename, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Offset, StringComparer.OrdinalIgnoreCase);

            var converter = new DdxSubprocessConverter();
            var result = await converter.ConvertBatchAsync(
                ddxDir,
                texturesDir,
                (inputPath, status, _) =>
                {
                    if (status is "FAIL" or "UNSUPPORTED")
                    {
                        var filename = Path.GetFileName(inputPath);
                        if (filenameToOffset.TryGetValue(filename, out var offset))
                        {
                            lock (failedOffsets)
                            {
                                failedOffsets.Add(offset);
                            }
                        }
                    }
                },
                default,
                pcFriendly);

            progress?.Report(new ExtractionProgress
            {
                PercentComplete = 75,
                CurrentOperation = $"Converted {result.Converted} textures ({result.Failed} failed)"
            });

            return (result, failedOffsets);
        }
        catch (FileNotFoundException)
        {
            // DDXConv not available
            return (new BatchConversionResult(), failedOffsets);
        }
    }
}
