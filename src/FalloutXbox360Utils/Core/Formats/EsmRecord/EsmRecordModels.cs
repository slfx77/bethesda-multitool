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
    public List<TextSubrecord> FullNames { get; init; } = [];      // FULL - display names
    public List<TextSubrecord> Descriptions { get; init; } = [];   // DESC - descriptions
    public List<TextSubrecord> ModelPaths { get; init; } = [];     // MODL - model paths
    public List<TextSubrecord> IconPaths { get; init; } = [];      // ICON/MICO - icon paths
    public List<TextSubrecord> TexturePaths { get; init; } = [];   // TX00-TX07 - texture sets

    // FormID reference subrecords
    public List<FormIdSubrecord> ScriptRefs { get; init; } = [];   // SCRI - script references
    public List<FormIdSubrecord> EffectRefs { get; init; } = [];   // ENAM - effect references
    public List<FormIdSubrecord> SoundRefs { get; init; } = [];    // SNAM - sound references
    public List<FormIdSubrecord> QuestRefs { get; init; } = [];    // QNAM - quest references

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

/// <summary>
///     Main record header detected in memory dump.
///     Structure: [TYPE:4][SIZE:4][FLAGS:4][FORMID:4][VCS1:4][VCS2:4] = 24 bytes
/// </summary>
public record DetectedMainRecord(
    string RecordType,
    uint DataSize,
    uint Flags,
    uint FormId,
    long Offset,
    bool IsBigEndian)
{
    /// <summary>Whether this is a compressed record.</summary>
    public bool IsCompressed => (Flags & 0x00040000) != 0;

    /// <summary>Whether this is a deleted record.</summary>
    public bool IsDeleted => (Flags & 0x00000020) != 0;

    /// <summary>Plugin index from FormID (upper 8 bits).</summary>
    public byte PluginIndex => (byte)(FormId >> 24);

    /// <summary>Local FormID (lower 24 bits).</summary>
    public uint LocalFormId => FormId & 0x00FFFFFF;
}

/// <summary>
///     NAME subrecord - base object FormID reference.
///     Common in REFR, ACHR, ACRE records.
/// </summary>
public record NameSubrecord(uint BaseFormId, long Offset, bool IsBigEndian);

/// <summary>
///     DATA subrecord with position and rotation.
///     24 bytes: 3 floats (position) + 3 floats (rotation)
/// </summary>
public record PositionSubrecord(
    float X, float Y, float Z,
    float RotX, float RotY, float RotZ,
    long Offset, bool IsBigEndian);

/// <summary>
///     ACBS subrecord - Actor Base Stats.
///     24 bytes in NPC_/CREA records.
/// </summary>
public record ActorBaseSubrecord(
    uint Flags,
    ushort FatigueBase,
    ushort BarterGold,
    short Level,
    ushort CalcMin,
    ushort CalcMax,
    ushort SpeedMultiplier,
    float KarmaAlignment,
    short DispositionBase,
    ushort TemplateFlags,
    long Offset,
    bool IsBigEndian);

/// <summary>
///     NAM1 subrecord - Dialogue response text.
///     Variable length null-terminated string in INFO records.
/// </summary>
public record ResponseTextSubrecord(string Text, long Offset);

/// <summary>
///     TRDT subrecord - Dialogue response data.
///     20 bytes: emotionType(4) + emotionValue(4) + unused(4) + responseNumber(1) + unused(3) + soundFile(4)
/// </summary>
public record ResponseDataSubrecord(
    uint EmotionType,
    int EmotionValue,
    byte ResponseNumber,
    long Offset);

/// <summary>
///     Comprehensive ESM file scan result.
///     Used when scanning actual ESM files (not memory dumps).
/// </summary>
public record EsmFileScanResult
{
    /// <summary>ESM file header information.</summary>
    public EsmFileHeader? Header { get; init; }

    /// <summary>Count of records by type signature.</summary>
    public Dictionary<string, int> RecordTypeCounts { get; init; } = [];

    /// <summary>Total number of records.</summary>
    public int TotalRecords { get; init; }

    /// <summary>All parsed records (for small files or when requested).</summary>
    public List<ParsedMainRecord> Records { get; init; } = [];

    /// <summary>Record info list (signature, FormID, offset) for all records.</summary>
    public List<RecordInfo> RecordInfos { get; init; } = [];

    /// <summary>FormID to EditorID mapping.</summary>
    public Dictionary<uint, string> FormIdToEditorId { get; init; } = [];

    /// <summary>Records by category.</summary>
    public Dictionary<RecordCategory, int> RecordsByCategory { get; init; } = [];
}

/// <summary>
///     Game Setting (GMST) record.
/// </summary>
public record GmstRecord(string Name, long Offset, int Length);

/// <summary>
///     Editor ID (EDID) record.
/// </summary>
public record EdidRecord(string Name, long Offset);

/// <summary>
///     Script Source Text (SCTX) record.
/// </summary>
public record SctxRecord(string Text, long Offset, int Length);

/// <summary>
///     Script Object Reference (SCRO) record.
/// </summary>
public record ScroRecord(uint FormId, long Offset);

/// <summary>
///     Generic text-containing subrecord (FULL, DESC, MODL, ICON, etc.).
/// </summary>
public record TextSubrecord(string SubrecordType, string Text, long Offset);

/// <summary>
///     Generic FormID reference subrecord (SCRI, ENAM, SNAM, QNAM, etc.).
/// </summary>
public record FormIdSubrecord(string SubrecordType, uint FormId, long Offset);

/// <summary>
///     CTDA subrecord - Condition data.
///     Used in quests, dialogues, and packages.
/// </summary>
public record ConditionSubrecord(
    byte Type,
    byte Operator,
    float ComparisonValue,
    ushort FunctionIndex,
    uint Param1,
    uint Param2,
    long Offset);

// =============================================================================
// Full Record Extraction Models (for visualization/export)
// =============================================================================

/// <summary>
///     Extracted LAND record with heightmap and texture data.
///     Enables terrain visualization from memory dumps.
/// </summary>
public record ExtractedLandRecord
{
    /// <summary>Parent main record information.</summary>
    public required DetectedMainRecord Header { get; init; }

    /// <summary>VHGT heightmap data - 33×33 grid of height deltas.</summary>
    public LandHeightmap? Heightmap { get; init; }

    /// <summary>Cell X coordinate (from parent CELL or inferred).</summary>
    public int? CellX { get; init; }

    /// <summary>Cell Y coordinate (from parent CELL or inferred).</summary>
    public int? CellY { get; init; }

    /// <summary>Texture layers (ATXT/BTXT).</summary>
    public List<LandTextureLayer> TextureLayers { get; init; } = [];
}

/// <summary>
///     VHGT heightmap data from a LAND record.
///     Contains 33×33 = 1089 height values.
/// </summary>
public record LandHeightmap
{
    /// <summary>Base height offset for the cell.</summary>
    public float HeightOffset { get; init; }

    /// <summary>33×33 grid of height deltas (sbyte values).</summary>
    public required sbyte[] HeightDeltas { get; init; }

    /// <summary>Offset in the dump where VHGT was found.</summary>
    public long Offset { get; init; }

    /// <summary>
    ///     Calculate actual heights for visualization.
    ///     Heights are cumulative: each row starts from the previous row's end value.
    /// </summary>
    public float[,] CalculateHeights()
    {
        var heights = new float[33, 33];
        var rowStart = HeightOffset;

        for (var y = 0; y < 33; y++)
        {
            var height = rowStart;
            for (var x = 0; x < 33; x++)
            {
                height += HeightDeltas[y * 33 + x] * 8; // Height scale factor
                heights[y, x] = height;
            }
            // Next row starts from the first column of current row
            rowStart = heights[y, 0];
        }

        return heights;
    }
}

/// <summary>
///     Texture layer information from ATXT/BTXT subrecords.
/// </summary>
public record LandTextureLayer(
    uint TextureFormId,
    byte Quadrant,
    short Layer,
    long Offset);

/// <summary>
///     Extracted REFR (placed object) with full placement data.
///     Links base object to position for visualization.
/// </summary>
public record ExtractedRefrRecord
{
    /// <summary>Parent main record information.</summary>
    public required DetectedMainRecord Header { get; init; }

    /// <summary>NAME - Base object FormID being placed.</summary>
    public uint BaseFormId { get; init; }

    /// <summary>DATA - Position in world coordinates.</summary>
    public PositionSubrecord? Position { get; init; }

    /// <summary>XSCL - Scale factor (1.0 = normal).</summary>
    public float Scale { get; init; } = 1.0f;

    /// <summary>XOWN - Owner FormID.</summary>
    public uint? OwnerFormId { get; init; }

    /// <summary>Parent cell FormID (if known).</summary>
    public uint? ParentCellFormId { get; init; }

    /// <summary>Editor ID of base object (if resolved).</summary>
    public string? BaseEditorId { get; init; }
}

/// <summary>
///     Directly detected VHGT heightmap subrecord from memory dump.
///     Unlike ExtractedLandRecord, this doesn't require a valid LAND main record header.
/// </summary>
public record DetectedVhgtHeightmap
{
    /// <summary>Offset in the dump where VHGT signature was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether detected as big-endian (Xbox 360) or little-endian (PC).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Base height offset for the cell.</summary>
    public float HeightOffset { get; init; }

    /// <summary>33×33 grid of height deltas (sbyte values).</summary>
    public required sbyte[] HeightDeltas { get; init; }

    /// <summary>
    ///     Calculate actual heights for visualization.
    /// </summary>
    public float[,] CalculateHeights()
    {
        var heights = new float[33, 33];
        var rowStart = HeightOffset;

        for (var y = 0; y < 33; y++)
        {
            var height = rowStart;
            for (var x = 0; x < 33; x++)
            {
                height += HeightDeltas[y * 33 + x] * 8; // Height scale factor
                heights[y, x] = height;
            }
            rowStart = heights[y, 0];
        }

        return heights;
    }
}

/// <summary>
///     XCLC cell grid subrecord - contains cell X/Y coordinates.
///     Critical for positioning heightmaps in a worldspace.
/// </summary>
public record CellGridSubrecord
{
    /// <summary>Cell X coordinate in the grid.</summary>
    public int GridX { get; init; }

    /// <summary>Cell Y coordinate in the grid.</summary>
    public int GridY { get; init; }

    /// <summary>Land flags byte.</summary>
    public byte LandFlags { get; init; }

    /// <summary>Offset in the dump where XCLC was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }
}

/// <summary>
///     Generic detected subrecord from memory dump.
///     Used for any subrecord type defined in the schema.
/// </summary>
public record DetectedSubrecord
{
    /// <summary>4-character subrecord signature (e.g., "DATA", "EDID").</summary>
    public required string Signature { get; init; }

    /// <summary>Offset in the dump where subrecord was found.</summary>
    public long Offset { get; init; }

    /// <summary>Size of subrecord data in bytes.</summary>
    public int DataSize { get; init; }

    /// <summary>Whether detected as big-endian.</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Raw data bytes (for post-processing).</summary>
    public byte[]? RawData { get; init; }

    /// <summary>Parsed field values (if schema was applied).</summary>
    public Dictionary<string, object?>? Fields { get; init; }
}
