namespace FalloutXbox360Utils.Core.Json;

/// <summary>
///     Analysis result for JSON serialization.
///     This is a simplified version of Core.AnalysisResult for trim-compatible JSON output.
/// </summary>
public sealed class JsonAnalysisResult
{
    public string? FilePath { get; set; }
    public long FileSize { get; set; }
    public string? BuildType { get; set; }
    public bool IsXbox360 { get; set; }
    public int ModuleCount { get; set; }
    public int MemoryRegionCount { get; set; }
    public List<JsonCarvedFileInfo> CarvedFiles { get; set; } = [];
    public JsonEsmRecordSummary? EsmRecords { get; set; }
    public Dictionary<uint, string> FormIdMap { get; set; } = [];
}
