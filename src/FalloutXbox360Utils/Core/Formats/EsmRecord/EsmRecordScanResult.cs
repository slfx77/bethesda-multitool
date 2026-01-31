using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

/// <summary>
///     Extracted ESM records from a memory dump.
///     Detects both subrecords (original) and main record headers (enhanced).
/// </summary>
public record EsmRecordScanResult
{
    // Subrecord detections (original)
    public List<GmstRecord> GameSettings { get; init; } = [];
    public List<EdidRecord> EditorIds { get; init; } = [];
    public List<SctxRecord> ScriptSources { get; init; } = [];
    public List<ScroRecord> FormIdReferences { get; init; } = [];

    // Main record detections (new)
    public List<DetectedMainRecord> MainRecords { get; init; } = [];

    // Extended subrecord detections (new)
    public List<NameSubrecord> NameReferences { get; init; } = [];
    public List<PositionSubrecord> Positions { get; init; } = [];
    public List<ActorBaseSubrecord> ActorBases { get; init; } = [];

    // INFO (dialogue) subrecord detections
    public List<ResponseTextSubrecord> ResponseTexts { get; init; } = [];
    public List<ResponseDataSubrecord> ResponseData { get; init; } = [];

    // Text-containing subrecords
    public List<TextSubrecord> FullNames { get; init; } = []; // FULL - display names
    public List<TextSubrecord> Descriptions { get; init; } = []; // DESC - descriptions
    public List<TextSubrecord> ModelPaths { get; init; } = []; // MODL - model paths
    public List<TextSubrecord> IconPaths { get; init; } = []; // ICON/MICO - icon paths
    public List<TextSubrecord> TexturePaths { get; init; } = []; // TX00-TX07 - texture sets

    // FormID reference subrecords
    public List<FormIdSubrecord> ScriptRefs { get; init; } = []; // SCRI - script references
    public List<FormIdSubrecord> EffectRefs { get; init; } = []; // ENAM - effect references
    public List<FormIdSubrecord> SoundRefs { get; init; } = []; // SNAM - sound references
    public List<FormIdSubrecord> QuestRefs { get; init; } = []; // QNAM - quest references

    // Condition data (CTDA) - common in quests/dialogue
    public List<ConditionSubrecord> Conditions { get; init; } = [];

    // Direct VHGT heightmap detections (standalone, not from LAND records)
    public List<DetectedVhgtHeightmap> Heightmaps { get; init; } = [];

    // XCLC cell grid positions (for heightmap positioning)
    public List<CellGridSubrecord> CellGrids { get; init; } = [];

    // Generic schema-defined subrecord detections
    public List<DetectedSubrecord> GenericSubrecords { get; init; } = [];

    // Full record extractions (for visualization/export)
    public List<ExtractedLandRecord> LandRecords { get; init; } = [];
    public List<ExtractedRefrRecord> RefrRecords { get; init; } = [];

    // Runtime asset string pool detections
    public List<DetectedAssetString> AssetStrings { get; init; } = [];

    // Runtime Editor ID entries with FormID associations (from hash table following)
    public List<RuntimeEditorIdEntry> RuntimeEditorIds { get; init; } = [];

    /// <summary>
    ///     Statistics by record type.
    /// </summary>
    public Dictionary<string, int> MainRecordCounts => MainRecords
        .GroupBy(r => r.RecordType)
        .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    ///     Statistics by endianness.
    /// </summary>
    public int LittleEndianRecords => MainRecords.Count(r => !r.IsBigEndian);

    public int BigEndianRecords => MainRecords.Count(r => r.IsBigEndian);
}

// =============================================================================
// Full Record Extraction Models (for visualization/export)
// =============================================================================
