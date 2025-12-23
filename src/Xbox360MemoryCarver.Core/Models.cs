namespace Xbox360MemoryCarver.Core;

/// <summary>
/// Result of analyzing a memory dump.
/// </summary>
public class AnalysisResult
{
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public List<CarvedFileInfo> CarvedFiles { get; set; } = new();
    public Dictionary<string, int> TypeCounts { get; set; } = new();
    public TimeSpan AnalysisTime { get; set; }
}

/// <summary>
/// Information about a carved file.
/// </summary>
public class CarvedFileInfo
{
    public long Offset { get; set; }
    public long Length { get; set; }
    public string FileType { get; set; } = "";
    public string? SubType { get; set; }
    public byte[]? Header { get; set; }
    public bool IsExtracted { get; set; }
    public string? ExtractedPath { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Options for file extraction.
/// </summary>
public record ExtractionOptions
{
    public string OutputPath { get; init; } = "output";
    public bool ConvertDdx { get; init; } = true;
    public bool SaveAtlas { get; init; } = false;
    public bool SaveRaw { get; init; } = false;
    public bool Verbose { get; init; } = false;
    public bool SkipEndian { get; init; } = false;
    public int ChunkSize { get; init; } = 10 * 1024 * 1024;
    public int MaxFilesPerType { get; init; } = 10000;
    public List<string>? FileTypes { get; init; }
}

/// <summary>
/// Progress information for analysis operations.
/// </summary>
public class AnalysisProgress
{
    public long BytesProcessed { get; set; }
    public long TotalBytes { get; set; }
    public int FilesFound { get; set; }
    public string CurrentType { get; set; } = "";
    public double PercentComplete => TotalBytes > 0 ? (BytesProcessed * 100.0 / TotalBytes) : 0;
}

/// <summary>
/// Progress information for extraction operations.
/// </summary>
public class ExtractionProgress
{
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public CarvedFileInfo? CurrentFile { get; set; }
    public string CurrentOperation { get; set; } = "";
    private double _percentComplete;
    public double PercentComplete
    {
        get => _percentComplete > 0 ? _percentComplete : (TotalFiles > 0 ? (FilesProcessed * 100.0 / TotalFiles) : 0);
        set => _percentComplete = value;
    }
}
