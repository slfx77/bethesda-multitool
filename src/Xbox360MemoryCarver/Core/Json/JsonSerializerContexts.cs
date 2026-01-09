using System.Text.Json.Serialization;
using Xbox360MemoryCarver.Core.Carving;

namespace Xbox360MemoryCarver.Core.Json;

/// <summary>
///     Source-generated JSON serializer context for trim-compatible serialization.
///     This avoids reflection-based serialization that breaks with IL trimming.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<CarveEntry>))]
[JsonSerializable(typeof(CarveEntry))]
[JsonSerializable(typeof(JsonAnalysisResult))]
[JsonSerializable(typeof(Dictionary<string, object>))]
internal partial class CarverJsonContext : JsonSerializerContext;

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
    public List<JsonScdaRecordInfo> ScdaRecords { get; set; } = [];
    public Dictionary<uint, string> FormIdMap { get; set; } = [];
}

/// <summary>
///     Summary of ESM records found in the dump.
/// </summary>
public sealed class JsonEsmRecordSummary
{
    public int EdidCount { get; set; }
    public int GmstCount { get; set; }
    public int SctxCount { get; set; }
    public int ScroCount { get; set; }
}

/// <summary>
///     Information about a carved file.
/// </summary>
public sealed class JsonCarvedFileInfo
{
    public string? FileType { get; set; }
    public long Offset { get; set; }
    public long Length { get; set; }
    public string? FileName { get; set; }
}

/// <summary>
///     Information about an SCDA (compiled script) record.
/// </summary>
public sealed class JsonScdaRecordInfo
{
    public long Offset { get; set; }
    public int BytecodeLength { get; set; }
    public string? ScriptName { get; set; }
}
