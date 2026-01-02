namespace Xbox360MemoryCarver.Core;

/// <summary>
///     Result of analyzing a memory dump.
/// </summary>
public class AnalysisResult
{
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public List<CarvedFileInfo> CarvedFiles { get; } = [];
    public Dictionary<string, int> TypeCounts { get; } = [];
    public TimeSpan AnalysisTime { get; set; }
}

/// <summary>
///     Information about a carved file.
/// </summary>
public class CarvedFileInfo
{
    public long Offset { get; set; }
    public long Length { get; set; }
    public string FileType { get; set; } = "";
    public string? FileName { get; set; }
    public string? SubType { get; set; }
    public byte[]? Header { get; set; }
    public bool IsExtracted { get; set; }
    public string? ExtractedPath { get; set; }
    public string? Error { get; set; }

    /// <summary>
    ///     Gets a display name - filename if available, otherwise the file type.
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(FileName) ? FileName : FileType;
}

/// <summary>
///     Options for file extraction.
/// </summary>
public record ExtractionOptions
{
    public string OutputPath { get; init; } = "output";
    public bool ConvertDdx { get; init; } = true;
    public bool SaveAtlas { get; init; }
    public bool SaveRaw { get; init; }
    public bool Verbose { get; init; }
    public bool SkipEndian { get; init; }
    public int ChunkSize { get; init; } = 10 * 1024 * 1024;
    public int MaxFilesPerType { get; init; } = 10000;
    public List<string>? FileTypes { get; init; }

    /// <summary>
    ///     Extract compiled scripts (SCDA records) from release dumps.
    ///     Scripts are grouped by quest name for easier analysis.
    /// </summary>
    public bool ExtractScripts { get; init; } = true;
}

/// <summary>
///     Progress information for analysis operations.
/// </summary>
public class AnalysisProgress
{
    public long BytesProcessed { get; set; }
    public long TotalBytes { get; set; }
    public int FilesFound { get; set; }
    public string CurrentType { get; set; } = "";
    public double PercentComplete => TotalBytes > 0 ? BytesProcessed * 100.0 / TotalBytes : 0;
}

/// <summary>
///     Progress information for extraction operations.
/// </summary>
public class ExtractionProgress
{
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public CarvedFileInfo? CurrentFile { get; set; }
    public string CurrentOperation { get; set; } = "";
    public double PercentComplete { get; set; }

    /// <summary>
    ///     Gets the calculated percent complete, using the set value if positive, otherwise calculating from files processed.
    /// </summary>
    public double GetEffectivePercentComplete()
    {
        return PercentComplete > 0 ? PercentComplete : CalculateFromFilesProcessed();
    }

    private double CalculateFromFilesProcessed()
    {
        return TotalFiles > 0 ? FilesProcessed * 100.0 / TotalFiles : 0;
    }
}
