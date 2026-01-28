using System.Text.Json.Serialization;
using FalloutXbox360Utils.Core.Carving;

namespace FalloutXbox360Utils.Core.Json;

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
    // Original subrecord counts
    public int EdidCount { get; set; }
    public int GmstCount { get; set; }
    public int SctxCount { get; set; }
    public int ScroCount { get; set; }

    // Main record detection
    public int MainRecordCount { get; set; }
    public int LittleEndianRecords { get; set; }
    public int BigEndianRecords { get; set; }
    public Dictionary<string, int> MainRecordTypes { get; set; } = [];

    // Extended subrecords
    public int NameRefCount { get; set; }
    public int PositionCount { get; set; }
    public int ActorBaseCount { get; set; }

    // Dialogue subrecords
    public int Nam1Count { get; set; }
    public int TrdtCount { get; set; }

    // Text subrecords
    public int FullNameCount { get; set; }
    public int DescriptionCount { get; set; }
    public int ModelPathCount { get; set; }
    public int IconPathCount { get; set; }
    public int TexturePathCount { get; set; }

    // FormID reference subrecords
    public int ScriptRefCount { get; set; }
    public int EffectRefCount { get; set; }
    public int SoundRefCount { get; set; }
    public int QuestRefCount { get; set; }

    // Conditions
    public int ConditionCount { get; set; }

    // Terrain/worldspace data
    public int HeightmapCount { get; set; }
    public int CellGridCount { get; set; }

    // Generic schema-defined subrecords
    public int GenericSubrecordCount { get; set; }
    public Dictionary<string, int> GenericSubrecordTypes { get; set; } = [];
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
