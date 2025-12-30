using System.IO.MemoryMappedFiles;
using System.Text;
using Xbox360MemoryCarver.Core.Carving;
using Xbox360MemoryCarver.Core.Minidump;
using Xbox360MemoryCarver.Core.Parsers;

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

        // Extract compiled scripts if requested
        var compiledScriptsCount = 0;
        if (options.ExtractCompiledScripts)
        {
            compiledScriptsCount = await ExtractCompiledScriptsAsync(filePath, options, progress);
        }

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
                PercentComplete = p * 100, CurrentOperation = "Extracting files..."
            }))
            : null;

        // Perform extraction using the full carver
        var entries = await carver.CarveDumpAsync(filePath, carverProgress);

        // Build set of extracted offsets for UI update
        var extractedOffsets = entries
            .Select(e => e.Offset)
            .ToHashSet();

        // Return summary
        return new ExtractionSummary
        {
            TotalExtracted = entries.Count + moduleCount + compiledScriptsCount,
            DdxConverted = carver.DdxConvertedCount,
            DdxFailed = carver.DdxConvertFailedCount,
            TypeCounts = carver.Stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExtractedOffsets = extractedOffsets,
            ModulesExtracted = moduleCount,
            CompiledScriptsExtracted = compiledScriptsCount
        };
    }

    /// <summary>
    ///     Extract compiled scripts from minidump using ScriptInfo scanning.
    /// </summary>
    private static async Task<int> ExtractCompiledScriptsAsync(
        string filePath,
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress)
    {
        var minidumpInfo = MinidumpParser.Parse(filePath);
        if (!minidumpInfo.IsValid)
        {
            if (options.Verbose) Console.WriteLine("[CompiledScripts] Minidump not valid, skipping script extraction");
            return 0;
        }

        // Create scripts output directory
        var dumpName = Path.GetFileNameWithoutExtension(filePath);
        var sanitizedName = SanitizeFilename(dumpName);
        var scriptsDir = Path.Combine(options.OutputPath, sanitizedName, "compiled_scripts");
        Directory.CreateDirectory(scriptsDir);

        // Read file data
        var fileData = await File.ReadAllBytesAsync(filePath);

        progress?.Report(new ExtractionProgress
        {
            CurrentOperation = "Scanning for compiled scripts..."
        });

        // Scan for ScriptInfo structures
        var scriptMatches = ScriptInfoScanner.ScanForScriptInfo(fileData, maxResults: 1000, verbose: false);

        if (scriptMatches.Count == 0)
        {
            if (options.Verbose) Console.WriteLine("[CompiledScripts] No compiled scripts found");
            return 0;
        }

        if (options.Verbose) Console.WriteLine($"[CompiledScripts] Found {scriptMatches.Count} potential scripts");

        var extracted = 0;
        var decompiled = 0;

        foreach (var match in scriptMatches)
        {
            var bytecode = ScriptInfoScanner.TryExtractBytecode(fileData, match, minidumpInfo);
            if (bytecode == null) continue;

            var baseName = $"script_0x{match.Offset:X8}_{match.ScriptType}";

            // Write binary bytecode
            var binPath = Path.Combine(scriptsDir, baseName + ".bin");
            await File.WriteAllBytesAsync(binPath, bytecode);
            extracted++;

            // Try to extract variable names
            var variables = ScriptInfoScanner.TryExtractVariables(fileData, match, minidumpInfo);
            var refVars = ScriptInfoScanner.TryExtractRefVariables(fileData, match, minidumpInfo);

            // Try to decompile
            try
            {
                var decompiler = new ScriptDecompiler(bytecode, 0, bytecode.Length, isBigEndian: true);

                // Pass variable names to decompiler if available
                if (variables != null)
                {
                    foreach (var v in variables)
                    {
                        if (!string.IsNullOrEmpty(v.Name))
                        {
                            decompiler.SetVariableName(v.Index, v.Name, v.Type);
                        }
                    }
                }

                if (refVars != null)
                {
                    foreach (var r in refVars)
                    {
                        if (!string.IsNullOrEmpty(r.Name))
                        {
                            decompiler.SetRefVariableName(r.Index, r.Name);
                        }
                    }
                }

                var result = decompiler.Decompile();

                if (!string.IsNullOrWhiteSpace(result.DecompiledText))
                {
                    var header = new StringBuilder();
                    header.AppendLine($"; Decompiled script from ScriptInfo at 0x{match.Offset:X8}");
                    header.AppendLine($"; Type: {match.ScriptType}");
                    header.AppendLine($"; DataLength: {match.DataLength}, NumRefs: {match.NumRefs}, VarCount: {match.VarCount}");
                    header.AppendLine($"; Bytecode size: {bytecode.Length} bytes");

                    // Show variable names if found
                    if (variables != null && variables.Count > 0)
                    {
                        header.AppendLine(";");
                        header.AppendLine("; Variables:");
                        foreach (var v in variables)
                        {
                            header.AppendLine($";   [{v.Index}] {v.Type}: {v.Name ?? "(unnamed)"}");
                        }
                    }

                    if (refVars != null && refVars.Count > 0)
                    {
                        header.AppendLine(";");
                        header.AppendLine("; References:");
                        foreach (var r in refVars)
                        {
                            header.AppendLine($";   [{r.Index}] {r.Name ?? "(unnamed)"} -> Form@0x{r.FormPointer:X8}");
                        }
                    }

                    if (!result.Success)
                        header.AppendLine($"; NOTE: Partial decompilation - {result.ErrorMessage}");
                    header.AppendLine(";");
                    header.AppendLine();

                    var txtPath = Path.Combine(scriptsDir, baseName + ".txt");
                    await File.WriteAllTextAsync(txtPath, header.ToString() + result.DecompiledText);
                    decompiled++;
                }
            }
            catch (Exception ex)
            {
                if (options.Verbose) Console.WriteLine($"[CompiledScripts] Failed to decompile {baseName}: {ex.Message}");
            }

            progress?.Report(new ExtractionProgress
            {
                CurrentOperation = $"Extracting script: {baseName}",
                FilesProcessed = extracted,
                TotalFiles = scriptMatches.Count
            });
        }

        if (options.Verbose)
        {
            Console.WriteLine($"[CompiledScripts] Extracted {extracted} bytecode files, decompiled {decompiled}");
        }

        return extracted;
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
    public int CompiledScriptsExtracted { get; init; }
    public Dictionary<string, int> TypeCounts { get; init; } = [];
    public HashSet<long> ExtractedOffsets { get; init; } = [];
}
