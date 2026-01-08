using Xbox360MemoryCarver.Core.Formats;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Formats.Scda;
using Xbox360MemoryCarver.Core.Minidump;

namespace Xbox360MemoryCarver.Core;

/// <summary>
///     Unified result of analyzing a memory dump.
///     Contains both carved file information (for visualization) and metadata (for reporting).
/// </summary>
public class AnalysisResult
{
    // File identification
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public TimeSpan AnalysisTime { get; set; }

    // Carved files for visualization
    public List<CarvedFileInfo> CarvedFiles { get; } = [];
    public Dictionary<string, int> TypeCounts { get; } = [];

    // Minidump metadata
    public MinidumpInfo? MinidumpInfo { get; set; }
    public string? BuildType { get; set; }

    // Script data (SCDA records)
    public List<ScdaRecord> ScdaRecords { get; set; } = [];

    // ESM record data
    public EsmRecordScanResult? EsmRecords { get; set; }
    public Dictionary<uint, string> FormIdMap { get; set; } = [];
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
    ///     The signature ID used to identify this file (e.g., "xui_scene", "ddx_3xdo").
    ///     Used for efficient color lookup without string matching.
    /// </summary>
    public string? SignatureId { get; set; }

    /// <summary>
    ///     The file category for color coding.
    /// </summary>
    public FileCategory Category { get; set; }

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

    /// <summary>
    ///     Current phase of analysis (e.g., "Scanning", "Parsing", "Metadata").
    /// </summary>
    public string Phase { get; set; } = "Scanning";

    /// <summary>
    ///     Overall progress percentage across all phases (0-100).
    /// </summary>
    public double PercentComplete { get; set; }
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
