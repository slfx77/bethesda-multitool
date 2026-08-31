using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>
///     Extracted ESM records from a memory dump.
///     Detects both subrecords (original) and main record headers (enhanced).
/// </summary>
public record EsmRecordScanResult
{
    /// <summary>Detected game/engine, resolved from the plugin header (and master list) when available.</summary>
    public BethesdaGame Game { get; set; } = BethesdaGame.Unknown;

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

    /// <summary>
    ///     pAllForms entries whose FormType *could* be this build's LAND, populated only when the
    ///     FormID-correlation heuristic could not identify it (too few carved LAND records to
    ///     calibrate against).
    ///     <para>
    ///         The record enumeration moved during development, so the LAND FormType byte differs
    ///         between dumps and cannot be read off the final build's PDB. Candidates here are
    ///         narrowed by the one invariant that holds in every build — a LAND record has no
    ///         EditorID — and the winner is chosen later by <c>RuntimeDataEnricher</c>, which has the
    ///         runtime reader needed to test which candidate actually yields terrain meshes.
    ///     </para>
    /// </summary>
    public List<RuntimeEditorIdEntry> RuntimeLandCandidateEntries { get; init; } = [];

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

    /// <summary>
    ///     PGRD FormID → parent CELL FormID mapping, resolved structurally from the Cell Children
    ///     GRUP hierarchy exactly like <see cref="LandToCellMap" />. TES4-era pathgrids carry no cell
    ///     linkage in their own data, so this map is the only parentage source for them.
    /// </summary>
    public Dictionary<uint, uint> PathgridToCellMap { get; init; } = [];

    /// <summary>Cell FormID → child REFR/ACHR/ACRE FormIDs (from ESM GRUP hierarchy type 8/9/10).</summary>
    public Dictionary<uint, List<uint>> CellToRefrMap { get; init; } = [];

    /// <summary>DIAL FormID → child INFO FormIDs (from ESM GRUP hierarchy type 7).</summary>
    public Dictionary<uint, List<uint>> TopicToInfoMap { get; init; } = [];

    /// <summary>
    ///     CELLs sitting directly under a Type-1 World Children GRUP without an enclosing Type-4/5
    ///     exterior block — the worldspace persistent-cell containers ("dummy" cells), resolved
    ///     structurally. TES4 dummies can carry XCLC (0,0) while TES4 exterior cells can omit XCLC,
    ///     so the grid-presence heuristic alone misclassifies both. Empty for structure-less inputs
    ///     (memory dumps), where the heuristic still applies.
    /// </summary>
    public HashSet<uint> PersistentCellContainerFormIds { get; init; } = [];

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
