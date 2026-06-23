namespace BethesdaMultitool.Core.Formats.Esm.Export.Comparison;

/// <summary>
///     Result of a per-record-type, per-field parity audit between an ESM
///     load and a DMP load taken from the same build. Counts whether each
///     field is filled by both sides, only one side, or disagrees on value.
/// </summary>
public sealed record ParityAuditResult
{
    public required string EsmLabel { get; init; }
    public required string DmpLabel { get; init; }
    public required IReadOnlyList<RecordTypeParity> RecordTypes { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Per-record-type parity counts (matched/ESM-only/DMP-only records) plus per-field parity for that type.</summary>
public sealed record RecordTypeParity
{
    public required string TypeName { get; init; }
    public int EsmRecordCount { get; init; }
    public int DmpRecordCount { get; init; }
    public int MatchedRecordCount { get; init; }
    public int EsmOnlyRecordCount { get; init; }
    public int DmpOnlyRecordCount { get; init; }
    public required IReadOnlyList<FieldParity> Fields { get; init; }
}

/// <summary>Per-field parity counts between ESM and DMP loads (ESM-only / DMP-only / agree / disagree) with examples.</summary>
public sealed record FieldParity
{
    public required string FieldName { get; init; }
    public int EsmOnly { get; init; }
    public int DmpOnly { get; init; }
    public int Agree { get; init; }
    public int Disagree { get; init; }
    public IReadOnlyList<FieldExample> Examples { get; init; } = [];
}

/// <summary>One sample FormID illustrating a field-parity discrepancy, with the ESM and DMP values and their status.</summary>
public sealed record FieldExample(
    uint FormId,
    string EsmValue,
    string DmpValue,
    FieldStatus Status);

/// <summary>How a single field value compared between an ESM and a DMP load.</summary>
public enum FieldStatus
{
    EsmOnly,
    DmpOnly,
    Disagree
}
