using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Carving;
using FalloutXbox360Utils.Core.Converters;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Export;
using FalloutXbox360Utils.Core.Formats.Scda;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Extracts files from memory dumps based on analysis results.
///     Uses the MemoryCarver for actual extraction with proper DDX handling.
/// </summary>
public static class MemoryDumpExtractor
{
    // Cached invalid filename characters to avoid repeated array allocation
    private static readonly HashSet<char> InvalidFileNameChars = [.. Path.GetInvalidFileNameChars()];

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
        var sanitizedName = SanitizeFilename(dumpName);
        var extractDir = Path.Combine(options.OutputPath, sanitizedName);

        // Extract modules from minidump first
        var (moduleCount, moduleOffsets) = await ExtractModulesAsync(filePath, options, progress);

        // When PcFriendly is enabled, skip inline DDX conversion - we'll do batch conversion after
        var skipDdxConversion = options.PcFriendly && options.ConvertDdx;

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

        // Batch DDX conversion with pc-friendly when enabled
        // Run batch conversion if there are DDX files to convert (inline conversion was skipped)
        var ddxConverted = carver.DdxConvertedCount;
        var ddxFailed = carver.DdxConvertFailedCount;
        if (skipDdxConversion && options.ConvertDdx)
        {
            var ddxDir = Path.Combine(extractDir, "ddx");
            var hasDdxToConvert = Directory.Exists(ddxDir) &&
                                  Directory.EnumerateFiles(ddxDir, "*.ddx").Any();

            if (hasDdxToConvert)
            {
                var batchResult = await BatchConvertDdxAsync(extractDir, options.PcFriendly, progress);
                ddxConverted = batchResult.Converted;
                ddxFailed = batchResult.Failed;
            }
            else if (options.Verbose)
            {
                Console.WriteLine("[DDX] No DDX files to convert");
            }
        }

        // Extract compiled scripts if requested
        var scriptResult = new ScdaExtractionResult();
        if (options.ExtractScripts)
        {
            scriptResult = await ExtractScriptsAsync(filePath, options, progress, analysisResult);
        }

        // Generate ESM reports and heightmaps if requested and analysis data is available
        var esmReportGenerated = false;
        var heightmapsExported = 0;
        if (options.GenerateEsmReports && analysisResult?.EsmRecords != null)
        {
            (esmReportGenerated, heightmapsExported) = await GenerateEsmOutputsAsync(
                analysisResult, filePath, extractDir, progress, scriptResult);
        }

        // Return summary
        return new ExtractionSummary
        {
            TotalExtracted = entries.Count + moduleCount + scriptResult.GroupedQuests + scriptResult.UngroupedScripts,
            DdxConverted = ddxConverted,
            DdxFailed = ddxFailed,
            XurConverted = carver.XurConvertedCount,
            XurFailed = carver.XurConvertFailedCount,
            TypeCounts = carver.Stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExtractedOffsets = extractedOffsets,
            FailedConversionOffsets = failedConversionOffsets,
            ExtractedModuleOffsets = moduleOffsets,
            ModulesExtracted = moduleCount,
            ScriptsExtracted = scriptResult.TotalRecords,
            ScriptQuestsGrouped = scriptResult.GroupedQuests,
            EsmReportGenerated = esmReportGenerated,
            HeightmapsExported = heightmapsExported
        };
    }

    /// <summary>
    ///     Extract compiled scripts (SCDA records) from the dump.
    /// </summary>
    private static async Task<ScdaExtractionResult> ExtractScriptsAsync(
        string filePath,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress,
        AnalysisResult? analysisResult = null)
    {
        progress?.Report(new ExtractionProgress
        {
            PercentComplete = 85,
            CurrentOperation = "Scanning for compiled scripts..."
        });

        // Set FormID map for SCRO resolution in script output
        if (analysisResult?.FormIdMap != null && analysisResult.FormIdMap.Count > 0)
        {
            ScdaFormatter.FormIdMap = analysisResult.FormIdMap;
        }

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

        var result = await ScdaExtractor.ExtractGroupedAsync(dumpData, scriptsDir, stringProgress);

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
    /// </summary>
    private static async Task<BatchConversionResult> BatchConvertDdxAsync(
        string extractDir,
        bool pcFriendly,
        IProgress<ExtractionProgress>? progress)
    {
        // DDX files are saved to "ddx" folder during carving (DdxFormat.OutputFolder = "ddx")
        // Convert them and output to "textures" folder
        var ddxDir = Path.Combine(extractDir, "ddx");
        var texturesDir = Path.Combine(extractDir, "textures");

        if (!Directory.Exists(ddxDir))
        {
            return new BatchConversionResult();
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
            var converter = new DdxSubprocessConverter();
            var result = await converter.ConvertBatchAsync(
                ddxDir,
                texturesDir,
                null,
                default,
                pcFriendly);

            progress?.Report(new ExtractionProgress
            {
                PercentComplete = 75,
                CurrentOperation = $"Converted {result.Converted} textures ({result.Failed} failed)"
            });

            return result;
        }
        catch (FileNotFoundException)
        {
            // DDXConv not available
            return new BatchConversionResult();
        }
    }

    /// <summary>
    ///     Generate ESM semantic report and heightmap images.
    /// </summary>
    private static async Task<(bool reportGenerated, int heightmapsExported)> GenerateEsmOutputsAsync(
        AnalysisResult analysisResult,
        string filePath,
        string extractDir,
        IProgress<ExtractionProgress>? progress,
        ScdaExtractionResult? scriptResult = null)
    {
        var reportGenerated = false;
        var heightmapsExported = 0;

        if (analysisResult.EsmRecords == null)
        {
            return (reportGenerated, heightmapsExported);
        }

        progress?.Report(new ExtractionProgress
        {
            PercentComplete = 92,
            CurrentOperation = "Generating ESM semantic report..."
        });

        try
        {
            // Open memory-mapped file for accessor-based reconstruction
            var fileInfo = new FileInfo(filePath);
            using var mmf =
                MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

            // Generate semantic reconstruction with accessor for full data access
            // Pass MinidumpInfo to enable runtime C++ struct reading for types with poor ESM coverage
            var reconstructor = new SemanticReconstructor(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileInfo.Length,
                analysisResult.MinidumpInfo);
            var semanticResult = reconstructor.ReconstructAll();

            // Generate split GECK-style reports (one file per record type)
            var splitReports = GeckReportGenerator.GenerateSplit(semanticResult, analysisResult.FormIdMap);
            var esmDir = Path.Combine(extractDir, "esm_data");
            Directory.CreateDirectory(esmDir);

            // Write all split report files
            foreach (var (filename, content) in splitReports)
            {
                var reportPath = Path.Combine(esmDir, filename);
                await File.WriteAllTextAsync(reportPath, content);
            }

            // Generate asset list report from runtime string pools
            if (analysisResult.EsmRecords.AssetStrings.Count > 0)
            {
                var assetReport = GeckReportGenerator.GenerateAssetListReport(analysisResult.EsmRecords.AssetStrings);
                await File.WriteAllTextAsync(Path.Combine(esmDir, "assets.txt"), assetReport);
            }

            // Generate runtime EditorIDs report with FormID associations
            if (analysisResult.EsmRecords.RuntimeEditorIds.Count > 0)
            {
                var runtimeEdidReport =
                    GeckReportGenerator.GenerateRuntimeEditorIdsReport(analysisResult.EsmRecords.RuntimeEditorIds);
                await File.WriteAllTextAsync(Path.Combine(esmDir, "runtime_editorids.csv"), runtimeEdidReport);
            }

            // Generate scripts summary report if SCDA extraction was done
            if (scriptResult != null && scriptResult.TotalRecords > 0)
            {
                var scriptsSummary = GeckReportGenerator.GenerateScriptsSummaryReport(scriptResult);
                await File.WriteAllTextAsync(Path.Combine(esmDir, "scripts_summary.txt"), scriptsSummary);
            }

            reportGenerated = splitReports.Count > 0;

            progress?.Report(new ExtractionProgress
            {
                PercentComplete = 95,
                CurrentOperation = "Exporting heightmap images..."
            });

            // Export heightmaps as PNG images (grayscale - individual cells lack context for color gradients)
            if (analysisResult.EsmRecords.Heightmaps.Count > 0)
            {
                var heightmapsDir = Path.Combine(esmDir, "heightmaps");
                await HeightmapPngExporter.ExportAsync(
                    analysisResult.EsmRecords.Heightmaps,
                    analysisResult.EsmRecords.CellGrids,
                    heightmapsDir,
                    false);
                heightmapsExported = analysisResult.EsmRecords.Heightmaps.Count;

                // Also try to generate composite worldmap (grayscale for consistency)
                // Use LAND records as primary positioning source when available
                if (analysisResult.EsmRecords.CellGrids.Count > 0 ||
                    analysisResult.EsmRecords.LandRecords.Count > 0)
                {
                    var worldmapPath = Path.Combine(esmDir, "worldmap_composite.png");
                    if (analysisResult.EsmRecords.LandRecords.Count > 0)
                    {
                        await HeightmapPngExporter.ExportCompositeWorldmapAsync(
                            analysisResult.EsmRecords.Heightmaps,
                            analysisResult.EsmRecords.CellGrids,
                            analysisResult.EsmRecords.LandRecords,
                            worldmapPath,
                            false);
                    }
                    else
                    {
                        await HeightmapPngExporter.ExportCompositeWorldmapAsync(
                            analysisResult.EsmRecords.Heightmaps,
                            analysisResult.EsmRecords.CellGrids,
                            worldmapPath,
                            false);
                    }
                }
            }

            progress?.Report(new ExtractionProgress
            {
                PercentComplete = 98,
                CurrentOperation = $"ESM report generated, {heightmapsExported} heightmaps exported"
            });
        }
        catch (Exception ex)
        {
            // Log but don't fail extraction if ESM report generation fails
            Console.WriteLine($"[ESM] Report generation failed: {ex.Message}");
        }

        return (reportGenerated, heightmapsExported);
    }

    /// <summary>
    ///     Sanitize a filename by removing invalid characters.
    /// </summary>
    private static string SanitizeFilename(string name)
    {
        var sanitized = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            sanitized[i] = InvalidFileNameChars.Contains(name[i]) ? '_' : name[i];
        }

        return new string(sanitized);
    }
}
