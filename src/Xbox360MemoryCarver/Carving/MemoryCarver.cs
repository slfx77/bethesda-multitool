using System.Buffers;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using Spectre.Console;
using Xbox360MemoryCarver.Converters;
using Xbox360MemoryCarver.Minidump;
using Xbox360MemoryCarver.Models;
using Xbox360MemoryCarver.Parsers;
using Xbox360MemoryCarver.Utils;

namespace Xbox360MemoryCarver.Carving;

/// <summary>
/// High-performance memory dump file carver using:
/// - Aho-Corasick multi-pattern search (single pass for all signatures)
/// - Memory-mapped file I/O for zero-copy reads
/// - Parallel extraction with concurrent collections
/// - ArrayPool buffer reuse to minimize GC pressure
/// </summary>
public sealed class MemoryCarver
{
    private readonly string _outputDir;
    private readonly int _maxFilesPerType;
    private readonly ConcurrentDictionary<string, int> _stats;
    private readonly ConcurrentBag<CarveEntry> _manifest;
    private readonly ConcurrentDictionary<long, byte> _processedOffsets;
    private readonly bool _convertDdxToDds;
    private readonly bool _verbose;
    private readonly DdxSubprocessConverter? _ddxConverter;
    private int _ddxConvertedCount;
    private int _ddxConvertFailedCount;
    private readonly AhoCorasick _signatureMatcher;
    private readonly Dictionary<string, SignatureInfo> _signatureInfoMap;

    private static readonly System.Text.Json.JsonSerializerOptions CachedJsonOptions = new()
    {
        WriteIndented = true
    };

    public MemoryCarver(
        string outputDir,
        int maxFilesPerType = 10000,
        bool convertDdxToDds = false,
        List<string>? fileTypes = null,
        bool verbose = false)
    {
        _outputDir = outputDir;
        _maxFilesPerType = maxFilesPerType;
        _stats = new ConcurrentDictionary<string, int>();
        _manifest = [];
        _processedOffsets = new ConcurrentDictionary<long, byte>();
        _convertDdxToDds = convertDdxToDds;
        _verbose = verbose;

        // Build signature matcher
        _signatureMatcher = new AhoCorasick();
        _signatureInfoMap = GetSignaturesToSearch(fileTypes);

        foreach (var (name, info) in _signatureInfoMap)
        {
            _signatureMatcher.AddPattern(name, info.Magic);
            _stats[name] = 0;
        }

        _signatureMatcher.Build();

        if (_convertDdxToDds)
        {
            if (DdxSubprocessConverter.IsAvailable())
            {
                _ddxConverter = new DdxSubprocessConverter(verbose: _verbose);
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Warning: DDXConv not found. DDX files will be saved without conversion.[/]");
                _convertDdxToDds = false;
            }
        }
    }

    private static Dictionary<string, SignatureInfo> GetSignaturesToSearch(List<string>? fileTypes)
    {
        if (fileTypes == null || fileTypes.Count == 0)
            return FileSignatures.Signatures;

        return FileSignatures.Signatures
            .Where(kvp => fileTypes.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Carve files from a memory dump using high-performance techniques.
    /// </summary>
    public async Task<List<CarveEntry>> CarveDumpAsync(
        string dumpPath,
        IProgress<double>? progress = null)
    {
        var dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        var outputPath = Path.Combine(_outputDir, BinaryUtils.SanitizeFilename(dumpName));
        Directory.CreateDirectory(outputPath);

        _manifest.Clear();
        _processedOffsets.Clear();
        foreach (var key in _stats.Keys)
            _stats[key] = 0;

        var fileInfo = new FileInfo(dumpPath);
        long fileSize = fileInfo.Length;

        // Extract minidump modules first
        await ExtractMinidumpModulesAsync(dumpPath, outputPath);

        // Use memory-mapped file for high-performance reading
        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

        // Find all signature matches in a single pass
        var matches = FindAllMatches(accessor, fileSize, progress);

        // Extract files in parallel
        await ExtractMatchesParallelAsync(accessor, fileSize, matches, outputPath);

        // Save manifest
        await SaveManifestAsync(outputPath);

        return [.. _manifest];
    }

    private List<(string SigName, long Offset)> FindAllMatches(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        IProgress<double>? progress)
    {
        const int chunkSize = 64 * 1024 * 1024; // 64MB chunks
        int maxPatternLength = _signatureInfoMap.Values.Max(s => s.Magic.Length);

        var allMatches = new List<(string SigName, long Offset)>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + maxPatternLength);

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                int toRead = (int)Math.Min(chunkSize + maxPatternLength, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                var span = buffer.AsSpan(0, toRead);
                var matches = _signatureMatcher.Search(span, offset);

                foreach (var (name, _, position) in matches)
                {
                    // Check if we've hit the limit for this type
                    if (_stats.GetValueOrDefault(name, 0) < _maxFilesPerType)
                    {
                        allMatches.Add((name, position));
                    }
                }

                // Move forward, but overlap by max pattern length to catch patterns at boundaries
                offset += chunkSize;
                progress?.Report(Math.Min((double)offset / fileSize, 0.5)); // First half is scanning
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

    private async Task ExtractMatchesParallelAsync(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        List<(string SigName, long Offset)> matches,
        string outputPath)
    {
        // Process in parallel with degree limited by CPU cores
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        await Parallel.ForEachAsync(matches, options, async (match, ct) =>
        {
            if (_stats.GetValueOrDefault(match.SigName, 0) >= _maxFilesPerType)
                return;

            // Skip if already processed
            if (!_processedOffsets.TryAdd(match.Offset, 0))
                return;

            var sigInfo = _signatureInfoMap[match.SigName];

            try
            {
                var extractionData = PrepareExtraction(accessor, fileSize, match.Offset, match.SigName, sigInfo, outputPath);

                if (extractionData != null)
                {
                    _stats.AddOrUpdate(match.SigName, 1, (_, v) => v + 1);

                    await WriteFileAsync(
                        extractionData.Value.outputFile,
                        extractionData.Value.data,
                        match.Offset,
                        match.SigName,
                        extractionData.Value.fileSize);
                }
            }
            catch
            {
                // Ignore extraction errors for individual files
            }
        });
    }

    private static (string outputFile, byte[] data, int fileSize)? PrepareExtraction(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        long offset,
        string sigName,
        SignatureInfo sigInfo,
        string outputPath)
    {
        // Read header data (enough for parsing)
        int headerSize = Math.Min(sigInfo.MaxSize, 64 * 1024); // Read up to 64KB for header parsing
        headerSize = (int)Math.Min(headerSize, fileSize - offset);

        var headerBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            accessor.ReadArray(offset, headerBuffer, 0, headerSize);
            var headerSpan = headerBuffer.AsSpan(0, headerSize);

            // Get parser for this file type
            var parser = ParserFactory.GetParser(sigName);
            int fileDataSize;
            string? customFilename = null;

            if (parser != null)
            {
                var parseResult = parser.ParseHeader(headerSpan);
                if (parseResult == null)
                    return null;

                fileDataSize = parseResult.EstimatedSize;

                if (parseResult.Metadata.TryGetValue("safeName", out var safeName))
                {
                    customFilename = safeName.ToString();
                }
            }
            else
            {
                fileDataSize = Math.Min(sigInfo.MaxSize, headerSize);
            }

            // Validate size
            if (fileDataSize < sigInfo.MinSize || fileDataSize > sigInfo.MaxSize)
                return null;

            // Ensure we don't read past end of file
            fileDataSize = (int)Math.Min(fileDataSize, fileSize - offset);

            // Create output directory
            string typeFolder = string.IsNullOrEmpty(sigInfo.Folder) ? sigName : sigInfo.Folder;
            string typePath = Path.Combine(outputPath, typeFolder);
            Directory.CreateDirectory(typePath);

            // Generate filename
            string filename = customFilename ?? $"{offset:X8}";
            string outputFile = Path.Combine(typePath, $"{filename}{sigInfo.Extension}");

            // Ensure unique filename
            int counter = 1;
            while (File.Exists(outputFile))
            {
                outputFile = Path.Combine(typePath, $"{filename}_{counter++}{sigInfo.Extension}");
            }

            // Read full file data
            byte[] fileData;
            if (fileDataSize <= headerSize)
            {
                fileData = headerSpan[..fileDataSize].ToArray();
            }
            else
            {
                fileData = new byte[fileDataSize];
                accessor.ReadArray(offset, fileData, 0, fileDataSize);
            }

            return (outputFile, fileData, fileDataSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private async Task WriteFileAsync(string outputFile, byte[] data, long offset, string sigName, int fileSize)
    {
        try
        {
            // Check if this is a DDX file and conversion is enabled
            if (_convertDdxToDds && _ddxConverter != null &&
                (sigName == "ddx_3xdo" || sigName == "ddx_3xdr") &&
                DdxSubprocessConverter.IsDdxFile(data))
            {
                var result = _ddxConverter.ConvertFromMemoryWithResult(data);

                if (result.Success && result.DdsData != null)
                {
                    var ddsOutputFile = outputFile
                        .Replace(Path.DirectorySeparatorChar + "ddx" + Path.DirectorySeparatorChar,
                                 Path.DirectorySeparatorChar + "textures" + Path.DirectorySeparatorChar);
                    ddsOutputFile = Path.ChangeExtension(ddsOutputFile, ".dds");

                    var texturesDir = Path.GetDirectoryName(ddsOutputFile);
                    if (texturesDir != null)
                        Directory.CreateDirectory(texturesDir);

                    await File.WriteAllBytesAsync(ddsOutputFile, result.DdsData);

                    _manifest.Add(new CarveEntry
                    {
                        FileType = sigName,
                        Offset = offset,
                        SizeInDump = fileSize,
                        SizeOutput = result.DdsData.Length,
                        Filename = Path.GetFileName(ddsOutputFile),
                        IsCompressed = true,
                        ContentType = result.IsPartial ? "dds_partial" : "dds_converted",
                        IsPartial = result.IsPartial,
                        Notes = result.Notes
                    });

                    Interlocked.Increment(ref _ddxConvertedCount);
                    return;
                }

                Interlocked.Increment(ref _ddxConvertFailedCount);
            }

            // Use async for all file writes
            await File.WriteAllBytesAsync(outputFile, data);

            _manifest.Add(new CarveEntry
            {
                FileType = sigName,
                Offset = offset,
                SizeInDump = fileSize,
                SizeOutput = fileSize,
                Filename = Path.GetFileName(outputFile)
            });
        }
        catch
        {
            _stats.AddOrUpdate(sigName, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    private async Task SaveManifestAsync(string outputPath)
    {
        var manifestPath = Path.Combine(outputPath, "manifest.json");
        var manifestList = _manifest.ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(manifestList, CachedJsonOptions);
        await File.WriteAllTextAsync(manifestPath, json);
    }

    private async Task ExtractMinidumpModulesAsync(string dumpPath, string outputPath)
    {
        try
        {
            await using var checkFs = new FileStream(dumpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var magic = new byte[4];
            if (await checkFs.ReadAsync(magic) < 4) return;

            if (magic[0] != 'M' || magic[1] != 'D' || magic[2] != 'M' || magic[3] != 'P')
                return;

            var executablesPath = Path.Combine(outputPath, "executables");
            Directory.CreateDirectory(executablesPath);

            var extractor = new MinidumpExtractor(executablesPath);
            var (modules, dumpInfo) = await extractor.ExtractModulesAsync(dumpPath);

            LogDumpInfo(dumpInfo);
            AddModulesToManifest(modules);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[grey]Note: Could not extract minidump modules: {ex.Message}[/]");
        }
    }

    private static void LogDumpInfo(DumpInfo? dumpInfo)
    {
        if (dumpInfo == null) return;

        if (dumpInfo.System != null)
            AnsiConsole.MarkupLine($"[blue]Dump Architecture: {dumpInfo.System.ProcessorArchitectureName}[/]");

        if (dumpInfo.Exception != null)
            AnsiConsole.MarkupLine($"[yellow]Exception: {dumpInfo.Exception.ExceptionCodeName} at 0x{dumpInfo.Exception.ExceptionAddress:X}[/]");

        if (dumpInfo.MemoryRegionCount > 0)
            AnsiConsole.MarkupLine($"[blue]Memory: {dumpInfo.MemoryRegionCount} regions, {BinaryUtils.FormatSize(dumpInfo.TotalMemorySize)} total[/]");
    }

    private void AddModulesToManifest(List<ModuleInfo> modules)
    {
        foreach (var module in modules)
        {
            var safeName = BinaryUtils.SanitizeFilename(Path.GetFileName(module.Name));
            var fileType = Path.GetExtension(safeName).ToLowerInvariant() switch
            {
                ".dll" => "dll",
                ".exe" => "exe",
                ".xex" => "xex",
                _ => "module"
            };

            _manifest.Add(new CarveEntry
            {
                FileType = fileType,
                Offset = (long)module.BaseAddress,
                SizeInDump = module.Size,
                SizeOutput = module.Size,
                Filename = safeName,
                ContentType = "minidump_module",
                Notes = $"Extracted from minidump module list: {module.Name}"
            });

            _stats.AddOrUpdate(fileType, 1, (_, v) => v + 1);
        }
    }

    public void PrintStats()
    {
        var table = new Table()
            .AddColumn("File Type")
            .AddColumn(new TableColumn("Count").RightAligned());

        int total = 0;
        foreach (var (type, count) in _stats.Where(s => s.Value > 0).OrderByDescending(s => s.Value))
        {
            table.AddRow(type, count.ToString());
            total += count;
        }

        table.AddEmptyRow();
        table.AddRow("[bold]Total[/]", $"[bold]{total}[/]");

        AnsiConsole.Write(table);

        if (_convertDdxToDds && (_ddxConvertedCount > 0 || _ddxConvertFailedCount > 0))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]DDX→DDS converted:[/] {_ddxConvertedCount}  [yellow]Failed:[/] {_ddxConvertFailedCount}");
        }
    }

    public Dictionary<string, int> GetStats() => new(_stats);
    public List<CarveEntry> GetManifest() => [.. _manifest];
}
