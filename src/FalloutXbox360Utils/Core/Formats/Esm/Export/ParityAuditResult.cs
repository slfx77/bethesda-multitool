namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

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

public sealed record FieldParity
{
    public required string FieldName { get; init; }
    public int EsmOnly { get; init; }
    public int DmpOnly { get; init; }
    public int Agree { get; init; }
    public int Disagree { get; init; }
    public IReadOnlyList<FieldExample> Examples { get; init; } = [];
}

public sealed record FieldExample(
    uint FormId,
    string EsmValue,
    string DmpValue,
    FieldStatus Status);

public enum FieldStatus
{
    EsmOnly,
    DmpOnly,
    Disagree
}
