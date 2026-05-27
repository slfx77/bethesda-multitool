namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     Caravan Money (CMNY) record.
/// </summary>
public record CaravanMoneyRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public uint Value { get; init; }
    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
