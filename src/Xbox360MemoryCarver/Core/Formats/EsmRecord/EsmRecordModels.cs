namespace Xbox360MemoryCarver.Core.Formats.EsmRecord;

/// <summary>
///     Extracted ESM records from a memory dump.
/// </summary>
public record EsmRecordScanResult
{
    public List<GmstRecord> GameSettings { get; init; } = [];
    public List<EdidRecord> EditorIds { get; init; } = [];
    public List<SctxRecord> ScriptSources { get; init; } = [];
    public List<ScroRecord> FormIdReferences { get; init; } = [];
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
