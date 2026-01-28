using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.Scda;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core;

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
    public byte[]? Header { get; set; }
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
    ///     True if this file was detected as potentially truncated due to a memory region gap.
    ///     Files crossing non-contiguous virtual address boundaries may be incomplete.
    /// </summary>
    public bool IsTruncated { get; set; }
}

/// <summary>
///     Options for file extraction.
/// </summary>
public record ExtractionOptions
{
    public string OutputPath { get; init; } = "output";
    public bool ConvertDdx { get; init; } = true;
    public bool SaveAtlas { get; init; }
    public bool Verbose { get; init; }
    public int MaxFilesPerType { get; init; } = 10000;
    public List<string>? FileTypes { get; init; }

    /// <summary>
    ///     Extract compiled scripts (SCDA records) from release dumps.
    ///     Scripts are grouped by quest name for easier analysis.
    /// </summary>
    public bool ExtractScripts { get; init; } = true;

    /// <summary>
    ///     Enable PC-friendly normal map conversion during DDX extraction.
    ///     This post-processes normal maps to merge specular data for PC compatibility.
    /// </summary>
    public bool PcFriendly { get; init; } = true;

    /// <summary>
    ///     Generate ESM semantic reports and heightmap PNGs during extraction.
    /// </summary>
    public bool GenerateEsmReports { get; init; } = true;
}

/// <summary>
///     Progress information for analysis operations.
/// </summary>
public class AnalysisProgress
{
    public long BytesProcessed { get; set; }
    public long TotalBytes { get; set; }
    public int FilesFound { get; set; }

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
    public string CurrentOperation { get; set; } = "";
    public double PercentComplete { get; set; }
}
