using System.Buffers;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core.FileTypes;
using Xbox360MemoryCarver.Core.Minidump;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Core;

/// <summary>
///     Analyzes memory dumps to identify extractable file types without extracting them.
/// </summary>
public class MemoryDumpAnalyzer
{
    private readonly SignatureMatcher _signatureMatcher;

    public MemoryDumpAnalyzer()
    {
        _signatureMatcher = new SignatureMatcher();

        // Register all signatures from the file type registry
        foreach (var typeDef in FileTypeRegistry.AllTypes)
        {
            foreach (var sig in typeDef.Signatures)
            {
                _signatureMatcher.AddPattern(sig.Id, sig.MagicBytes);
            }
        }

        _signatureMatcher.Build();
    }

    /// <summary>
    ///     Analyze a memory dump file to identify all extractable files.
    /// </summary>
    public AnalysisResult Analyze(string filePath, IProgress<AnalysisProgress>? progress, bool scanCompiledScripts = true)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new AnalysisResult { FilePath = filePath };

        var fileInfo = new FileInfo(filePath);
        result.FileSize = fileInfo.Length;

        // Parse minidump to get module information and memory mappings
        var minidumpInfo = MinidumpParser.Parse(filePath);

        if (minidumpInfo.IsValid)
        {
            Console.WriteLine(
                $"[Minidump] {minidumpInfo.Modules.Count} modules, {minidumpInfo.MemoryRegions.Count} memory regions, Xbox 360: {minidumpInfo.IsXbox360}");

            // Add modules directly to results
            AddModulesFromMinidump(result, minidumpInfo);
        }

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);

        // Build set of module file offsets to exclude from signature scanning
        var moduleOffsets = new HashSet<long>(
            minidumpInfo.Modules
                .Select(m => minidumpInfo.GetModuleFileRange(m))
                .Where(r => r.HasValue)
                .Select(r => r!.Value.fileOffset));

        var matches = FindAllMatches(accessor, result.FileSize, progress);

        // Convert matches to CarvedFileInfo using proper parsers
        foreach (var (signatureId, offset) in matches)
        {
            // Skip XEX signatures at module offsets (already added above)
            if (signatureId == "xex" && moduleOffsets.Contains(offset)) continue;

            var typeDef = FileTypeRegistry.GetBySignatureId(signatureId);
            if (typeDef == null) continue;

            var signature = typeDef.GetSignature(signatureId);
            if (signature == null) continue;

            var (length, fileName) =
                EstimateFileSizeAndExtractName(accessor, result.FileSize, offset, signatureId, typeDef);

            if (length > 0)
            {
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = offset,
                    Length = length,
                    FileType = signature.Description,
                    FileName = fileName
                });

                result.TypeCounts.TryGetValue(signatureId, out var count);
                result.TypeCounts[signatureId] = count + 1;
            }
        }

        // Scan for compiled script bytecode if requested and minidump is valid
        if (scanCompiledScripts && minidumpInfo.IsValid)
        {
            ScanForCompiledScripts(result, accessor, result.FileSize, minidumpInfo);
        }

        // Sort all results by offset
        var sortedFiles = result.CarvedFiles.OrderBy(f => f.Offset).ToList();
        result.CarvedFiles.Clear();
        foreach (var file in sortedFiles) result.CarvedFiles.Add(file);

        stopwatch.Stop();
        result.AnalysisTime = stopwatch.Elapsed;

        return result;
    }

    /// <summary>
    ///     Scan for compiled script bytecode using ScriptInfo structure detection.
    /// </summary>
    private static void ScanForCompiledScripts(
        AnalysisResult result,
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidump)
    {
        Console.WriteLine("[Analysis] Scanning for compiled script bytecode...");
        
        // Read file data for scanning
        var buffer = new byte[fileSize];
        accessor.ReadArray(0, buffer, 0, (int)fileSize);
        
        // Scan for ScriptInfo structures
        var scriptMatches = ScriptInfoScanner.ScanForScriptInfo(buffer, maxResults: 1000, verbose: false);
        
        if (scriptMatches.Count == 0)
        {
            Console.WriteLine("[Analysis] No compiled scripts found.");
            return;
        }
        
        Console.WriteLine($"[Analysis] Found {scriptMatches.Count} potential compiled scripts");
        
        var addedCount = 0;
        foreach (var match in scriptMatches)
        {
            // Try to map the bytecode pointer to a file offset
            var fileOffset = minidump.VirtualAddressToFileOffset(match.DataPointer);
            if (!fileOffset.HasValue) continue;
            
            var bytecodeOffset = (long)fileOffset.Value;
            var bytecodeLength = (int)match.DataLength;
            
            // Validate the bytecode is within file bounds
            if (bytecodeOffset < 0 || bytecodeOffset + bytecodeLength > fileSize) continue;
            
            // Store compiled script info for extraction
            result.CompiledScripts.Add(new CompiledScriptInfo
            {
                ScriptInfoOffset = match.Offset,
                BytecodeFileOffset = bytecodeOffset,
                BytecodeLength = bytecodeLength,
                ScriptType = match.ScriptType,
                NumRefs = (int)match.NumRefs,
                VarCount = (int)match.VarCount,
                DataPointer = match.DataPointer
            });
            
            // Add to carved files list for display in UI
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = bytecodeOffset,
                Length = bytecodeLength,
                FileType = $"Compiled Script ({match.ScriptType})",
                FileName = $"script_0x{match.Offset:X8}",
                ScriptInfoOffset = match.Offset,
                BytecodePointer = match.DataPointer
            });
            
            addedCount++;
        }
        
        if (addedCount > 0)
        {
            result.TypeCounts["compiled_script"] = addedCount;
            Console.WriteLine($"[Analysis] Added {addedCount} compiled scripts to results");
        }
    }

    private static void AddModulesFromMinidump(AnalysisResult result, MinidumpInfo minidumpInfo)
    {
        var xexTypeDef = FileTypeRegistry.GetByTypeId("xex");
        if (xexTypeDef == null) return;

        foreach (var module in minidumpInfo.Modules)
        {
            var fileName = Path.GetFileName(module.Name);
            var fileRange = minidumpInfo.GetModuleFileRange(module);

            if (fileRange.HasValue)
            {
                var captured = fileRange.Value.size;
                Console.WriteLine(
                    $"[Minidump]   Module: {fileName} at 0x{fileRange.Value.fileOffset:X8}, captured: {captured:N0} bytes");

                // Determine description based on extension
                var isExe = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                var fileType = isExe ? "Xbox 360 Module (EXE)" : "Xbox 360 Module (DLL)";

                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = fileRange.Value.fileOffset,
                    Length = captured,
                    FileType = fileType,
                    FileName = fileName
                });

                result.TypeCounts.TryGetValue("xex", out var modCount);
                result.TypeCounts["xex"] = modCount + 1;
            }
        }
    }

    private List<(string SignatureId, long Offset)> FindAllMatches(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        IProgress<AnalysisProgress>? progress)
    {
        const int chunkSize = 64 * 1024 * 1024; // 64MB chunks
        var maxPatternLength = _signatureMatcher.MaxPatternLength;

        var allMatches = new List<(string SignatureId, long Offset)>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + maxPatternLength);
        var progressData = new AnalysisProgress { TotalBytes = fileSize };

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                var toRead = (int)Math.Min(chunkSize + maxPatternLength, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                var span = buffer.AsSpan(0, toRead);
                var matches = _signatureMatcher.Search(span, offset);

                foreach (var (name, _, position) in matches) allMatches.Add((name, position));

                offset += chunkSize;

                progressData.BytesProcessed = Math.Min(offset, fileSize);
                progressData.FilesFound = allMatches.Count;
                progress?.Report(progressData);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Sort by offset and deduplicate
        return allMatches
            .DistinctBy(m => m.Offset)
            .OrderBy(m => m.Offset)
            .ToList();
    }

    private static (long length, string? fileName) EstimateFileSizeAndExtractName(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        long offset,
        string signatureId,
        FileTypeDefinition typeDef)
    {
        // For DDX files, we need to read some data before the signature to find the path
        // Read up to 512 bytes before and the header after
        const int preReadSize = 512;
        var actualPreRead = (int)Math.Min(preReadSize, offset);
        var readStart = offset - actualPreRead;

        var headerSize = Math.Min(typeDef.MaxSize, 64 * 1024);
        headerSize = (int)Math.Min(headerSize, fileSize - offset);
        var totalRead = actualPreRead + headerSize;

        var buffer = ArrayPool<byte>.Shared.Rent(totalRead);
        try
        {
            accessor.ReadArray(readStart, buffer, 0, totalRead);
            var span = buffer.AsSpan(0, totalRead);

            // The signature starts at actualPreRead offset in our buffer
            var sigOffset = actualPreRead;

            // Use parser for accurate size estimation
            var parser = ParserRegistry.GetParserForSignature(signatureId);
            if (parser != null)
            {
                var parseResult = parser.ParseHeader(span, sigOffset);
                if (parseResult != null)
                {
                    var estimatedSize = parseResult.EstimatedSize;
                    if (estimatedSize >= typeDef.MinSize && estimatedSize <= typeDef.MaxSize)
                    {
                        var length = Math.Min(estimatedSize, (int)(fileSize - offset));

                        // Extract filename for display in the file table
                        // Priority: fileName > scriptName > texturePath filename portion
                        string? fileName = null;

                        if (parseResult.Metadata.TryGetValue("fileName", out var fileNameObj) &&
                            fileNameObj is string fn && !string.IsNullOrEmpty(fn))
                            fileName = fn;
                        else if (parseResult.Metadata.TryGetValue("scriptName", out var scriptNameObj) &&
                                 scriptNameObj is string sn && !string.IsNullOrEmpty(sn))
                            fileName = sn;
                        else if (parseResult.Metadata.TryGetValue("texturePath", out var pathObj) &&
                                 pathObj is string texturePath)
                            // Fall back to extracting filename from path
                            fileName = Path.GetFileName(texturePath);

                        return (length, fileName);
                    }
                }

                // Parser returned null - invalid file, skip it
                return (0, null);
            }

            // Fallback for types without parsers
            return (Math.Min(typeDef.MaxSize, (int)(fileSize - offset)), null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
