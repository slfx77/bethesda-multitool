using Spectre.Console;
using Xbox360MemoryCarver.Converters;
using Xbox360MemoryCarver.Models;
using Xbox360MemoryCarver.Parsers;
using Xbox360MemoryCarver.Utils;

namespace Xbox360MemoryCarver.Carving;

/// <summary>
/// Memory dump file carver with chunked processing for efficient handling of large dumps.
/// Supports automatic DDX to DDS conversion during extraction.
/// </summary>
public class MemoryCarver
{
    private readonly string _outputDir;
    private readonly int _chunkSize;
    private readonly int _maxFilesPerType;
    private readonly Dictionary<string, int> _stats;
    private readonly List<CarveEntry> _manifest;
    private readonly HashSet<long> _processedOffsets;
    private readonly bool _convertDdxToDds;
    private readonly DdxSubprocessConverter? _ddxConverter;
    private int _ddxConvertedCount;
    private int _ddxConvertFailedCount;

    public MemoryCarver(string outputDir, int chunkSize = 10 * 1024 * 1024, int maxFilesPerType = 10000, bool convertDdxToDds = false)
    {
        _outputDir = outputDir;
        _chunkSize = chunkSize;
        _maxFilesPerType = maxFilesPerType;
        _stats = [];
        _manifest = [];
        _processedOffsets = [];
        _convertDdxToDds = convertDdxToDds;
        
        if (_convertDdxToDds)
        {
            if (DdxSubprocessConverter.IsAvailable())
            {
                _ddxConverter = new DdxSubprocessConverter(verbose: false);
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Warning: DDXConv not found. DDX files will be saved without conversion.[/]");
                _convertDdxToDds = false;
            }
        }

        // Initialize stats for all signature types
        foreach (var key in FileSignatures.Signatures.Keys)
        {
            _stats[key] = 0;
        }
    }

    /// <summary>
    /// Carve files from a memory dump asynchronously.
    /// </summary>
    public async Task<List<CarveEntry>> CarveDumpAsync(
        string dumpPath,
        List<string>? fileTypes = null,
        IProgress<double>? progress = null)
    {
        var dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        var outputPath = Path.Combine(_outputDir, BinaryUtils.SanitizeFilename(dumpName));
        Directory.CreateDirectory(outputPath);

        _manifest.Clear();
        _processedOffsets.Clear();

        var fileInfo = new FileInfo(dumpPath);
        long fileSize = fileInfo.Length;

        var signaturesToSearch = GetSignaturesToSearch(fileTypes);
        const int overlapSize = 2048;

        // Use larger buffer for better I/O performance
        using var fs = new FileStream(dumpPath, FileMode.Open, FileAccess.Read, FileShare.Read, 
            bufferSize: 1024 * 1024, // 1MB buffer
            useAsync: true);
        
        var buffer = new byte[_chunkSize + overlapSize];

        long offset = 0;
        while (offset < fileSize)
        {
            long seekPos = Math.Max(0, offset - overlapSize);
            fs.Seek(seekPos, SeekOrigin.Begin);

            int toRead = (int)Math.Min(buffer.Length, fileSize - seekPos);
            int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, toRead));

            if (bytesRead == 0) break;

            await ProcessChunkAsync(fs, buffer, bytesRead, seekPos, signaturesToSearch, outputPath);

            offset += _chunkSize;
            progress?.Report((double)Math.Min(offset, fileSize) / fileSize);
        }

        // Save manifest
        await SaveManifestAsync(outputPath);

        return _manifest;
    }

    private Dictionary<string, SignatureInfo> GetSignaturesToSearch(List<string>? fileTypes)
    {
        if (fileTypes == null || fileTypes.Count == 0)
            return FileSignatures.Signatures;

        return FileSignatures.Signatures
            .Where(kvp => fileTypes.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private async Task ProcessChunkAsync(
        FileStream fs,
        byte[] chunkBuffer,
        int bytesRead,
        long chunkStart,
        Dictionary<string, SignatureInfo> signatures,
        string outputPath)
    {
        var chunkData = chunkBuffer.AsSpan(0, bytesRead);
        
        // Collect all file extraction tasks
        var extractionTasks = new List<Task>();
        
        foreach (var (sigName, sigInfo) in signatures)
        {
            if (_stats[sigName] >= _maxFilesPerType)
                continue;

            try
            {
                var tasks = SearchAndExtractAsync(fs, chunkData, chunkStart, sigName, sigInfo, outputPath);
                extractionTasks.AddRange(tasks);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning: Error searching {sigName} at offset 0x{chunkStart:X}: {ex.Message}[/]");
            }
        }

        // Wait for all extractions to complete
        if (extractionTasks.Count > 0)
        {
            await Task.WhenAll(extractionTasks);
        }
    }

    private List<Task> SearchAndExtractAsync(
        FileStream fs,
        ReadOnlySpan<byte> chunkData,
        long chunkStart,
        string sigName,
        SignatureInfo sigInfo,
        string outputPath)
    {
        var tasks = new List<Task>();
        int searchPos = 0;
        
        while (searchPos < chunkData.Length - sigInfo.Magic.Length)
        {
            int foundPos = BinaryUtils.FindPattern(chunkData.Slice(searchPos), sigInfo.Magic);
            if (foundPos < 0) break;

            int relativeOffset = searchPos + foundPos;
            long absoluteOffset = chunkStart + relativeOffset;

            // Skip if we've already processed this offset
            lock (_processedOffsets)
            {
                if (_processedOffsets.Contains(absoluteOffset))
                {
                    searchPos = relativeOffset + sigInfo.Magic.Length;
                    continue;
                }
            }

            if (_stats[sigName] >= _maxFilesPerType)
                break;

            // Try to parse and prepare extraction
            var dataSlice = chunkData.Slice(relativeOffset);
            var extractionData = PrepareExtraction(fs, dataSlice, absoluteOffset, sigName, sigInfo, outputPath);
            
            if (extractionData != null)
            {
                lock (_processedOffsets)
                {
                    _processedOffsets.Add(absoluteOffset);
                    _stats[sigName]++;
                }
                
                // Queue async file write
                tasks.Add(WriteFileAsync(extractionData.Value.outputFile, extractionData.Value.data, 
                    absoluteOffset, sigName, extractionData.Value.fileSize));
            }

            searchPos = relativeOffset + sigInfo.Magic.Length;
        }

        return tasks;
    }

    private (string outputFile, byte[] data, int fileSize)? PrepareExtraction(
        FileStream fs,
        ReadOnlySpan<byte> data,
        long offset,
        string sigName,
        SignatureInfo sigInfo,
        string outputPath)
    {
        // Get parser for this file type
        var parser = ParserFactory.GetParser(sigName);
        int fileSize;
        string? customFilename = null;

        if (parser != null)
        {
            var parseResult = parser.ParseHeader(data);
            if (parseResult == null)
                return null;

            fileSize = parseResult.EstimatedSize;

            // Get custom filename for scripts
            if (parseResult.Metadata.TryGetValue("safeName", out var safeName))
            {
                customFilename = safeName.ToString();
            }
        }
        else
        {
            // For types without parsers, use a reasonable default size
            fileSize = Math.Min(sigInfo.MaxSize, data.Length);
        }

        // Validate size
        if (fileSize < sigInfo.MinSize || fileSize > sigInfo.MaxSize)
            return null;

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
        if (data.Length >= fileSize)
        {
            fileData = data.Slice(0, fileSize).ToArray();
        }
        else
        {
            // Need to read more data from file
            fileData = new byte[fileSize];
            lock (fs)
            {
                long currentPos = fs.Position;
                fs.Seek(offset, SeekOrigin.Begin);
                fs.ReadExactly(fileData, 0, fileSize);
                fs.Seek(currentPos, SeekOrigin.Begin);
            }
        }

        return (outputFile, fileData, fileSize);
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
                // Try to convert DDX to DDS
                var ddsData = _ddxConverter.ConvertFromMemory(data);
                
                if (ddsData != null)
                {
                    // Save as DDS instead of DDX - only change the extension
                    var ddsOutputFile = Path.ChangeExtension(outputFile, ".dds");
                    
                    await File.WriteAllBytesAsync(ddsOutputFile, ddsData);
                    
                    lock (_manifest)
                    {
                        _manifest.Add(new CarveEntry
                        {
                            FileType = sigName,
                            Offset = offset,
                            SizeInDump = fileSize,
                            SizeOutput = ddsData.Length,
                            Filename = Path.GetFileName(ddsOutputFile),
                            IsCompressed = true, // Indicates conversion happened
                            ContentType = "dds_converted"
                        });
                        _ddxConvertedCount++;
                    }
                    
                    return;
                }
                else
                {
                    // Conversion failed, save original DDX
                    _ddxConvertFailedCount++;
                }
            }

            await File.WriteAllBytesAsync(outputFile, data);

            // Add to manifest (thread-safe)
            lock (_manifest)
            {
                _manifest.Add(new CarveEntry
                {
                    FileType = sigName,
                    Offset = offset,
                    SizeInDump = fileSize,
                    SizeOutput = fileSize,
                    Filename = Path.GetFileName(outputFile)
                });
            }
        }
        catch
        {
            // Decrement stats on failure
            lock (_stats)
            {
                _stats[sigName]--;
            }
        }
    }

    private async Task SaveManifestAsync(string outputPath)
    {
        var manifestPath = Path.Combine(outputPath, "manifest.json");
        var json = System.Text.Json.JsonSerializer.Serialize(_manifest, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(manifestPath, json);
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
        
        // Print DDX conversion stats if applicable
        if (_convertDdxToDds && (_ddxConvertedCount > 0 || _ddxConvertFailedCount > 0))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]DDX?DDS converted:[/] {_ddxConvertedCount}  [yellow]Failed:[/] {_ddxConvertFailedCount}");
        }
    }

    public Dictionary<string, int> GetStats() => new(_stats);
    public List<CarveEntry> GetManifest() => new(_manifest);
}
