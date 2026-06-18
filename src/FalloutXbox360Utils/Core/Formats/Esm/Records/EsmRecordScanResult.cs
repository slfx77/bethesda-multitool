using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Records;

/// <summary>
///     Extracted ESM records from a memory dump.
///     Detects both subrecords (original) and main record headers (enhanced).
/// </summary>
public record EsmRecordScanResult
{
    /// <summary>Detected game version (FO3 vs FNV), auto-detected from TES4/HEDR if available.</summary>
    public FalloutGame Game { get; set; } = FalloutGame.Unknown;

    /// <summary>
    ///     True when the source is a Morrowind (TES3) plugin. TES3 uses a flat record stream with
    ///     4-byte subrecord sizes and wholly different record/subrecord layouts, so the TES4 typed
    ///     parsers don't apply — <see cref="Parsing.RecordParser" /> routes these to the dedicated
    ///     <see cref="Tes3.Tes3RecordParser" /> instead.
    /// </summary>
    public bool IsTes3 { get; set; }

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
    // `set` (not `init`): EsmWorldExtractor.ExtractLandRecords publishes the fully-built list via a
    // single atomic reference assignment instead of mutating it in place, so a concurrent reader (the
    // GUI runs extraction as an unawaited background task) can't observe a torn List during a resize.
    public List<ExtractedLandRecord> LandRecords { get; set; } = [];
    public List<ExtractedRefrRecord> RefrRecords { get; init; } = [];

    // Runtime asset string pool detections
    public List<DetectedAssetString> AssetStrings { get; init; } = [];

    // Runtime Editor ID entries with FormID associations (from hash table following)
    public List<RuntimeEditorIdEntry> RuntimeEditorIds { get; init; } = [];

    // Runtime LAND form entries from pAllForms hash table (LAND records lack editor IDs)
    public List<RuntimeEditorIdEntry> RuntimeLandFormEntries { get; init; } = [];

    // Runtime REFR/ACHR/ACRE form entries from pAllForms hash table
    public List<RuntimeEditorIdEntry> RuntimeRefrFormEntries { get; init; } = [];

    /// <summary>
    ///     Virtual address of the engine's pAllForms hash table (NiTMapBase&lt;uint, TESForm*&gt;)
    ///     discovered during the data-section pointer-triple scan in <see cref="EsmEditorIdExtractor" />.
    ///     0 when discovery failed or the dump didn't expose the triple. Consumers that walk pAllForms
    ///     for additional form-type enumeration (e.g. the runtime cell enumerator for NAVM discovery)
    ///     read this VA rather than re-scanning the data section.
    /// </summary>
    public uint PAllFormsVa { get; set; }

    /// <summary>Cell FormID → parent Worldspace FormID mapping (from ESM GRUP hierarchy).</summary>
    public Dictionary<uint, uint> CellToWorldspaceMap { get; init; } = [];

    /// <summary>LAND FormID → parent Worldspace FormID mapping (from ESM GRUP hierarchy).</summary>
    public Dictionary<uint, uint> LandToWorldspaceMap { get; init; } = [];

    /// <summary>
    ///     LAND FormID → parent CELL FormID mapping, resolved structurally from the Cell Children
    ///     GRUP hierarchy (types 8/9/10). Authoritative parentage used by
    ///     <see cref="EsmWorldExtractor" /> in preference to the offset-proximity fallback.
    ///     Empty for structure-less inputs (memory dumps), where the proximity fallback still runs.
    /// </summary>
    public Dictionary<uint, uint> LandToCellMap { get; init; } = [];

    /// <summary>Cell FormID → child REFR/ACHR/ACRE FormIDs (from ESM GRUP hierarchy type 8/9/10).</summary>
    public Dictionary<uint, List<uint>> CellToRefrMap { get; init; } = [];

    /// <summary>DIAL FormID → child INFO FormIDs (from ESM GRUP hierarchy type 7).</summary>
    public Dictionary<uint, List<uint>> TopicToInfoMap { get; init; } = [];

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
