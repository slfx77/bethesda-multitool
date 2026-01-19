namespace Xbox360MemoryCarver.Core.Formats.EsmRecord;

/// <summary>
///     Extracted ESM records from a memory dump.
///     This legacy result type is used for memory dump scanning.
/// </summary>
public record EsmRecordScanResult
{
    public List<GmstRecord> GameSettings { get; init; } = [];
    public List<EdidRecord> EditorIds { get; init; } = [];
    public List<SctxRecord> ScriptSources { get; init; } = [];
    public List<ScroRecord> FormIdReferences { get; init; } = [];
}

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
