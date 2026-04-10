namespace FalloutXbox360Utils.Core.Formats.Esm.Presentation;

internal sealed record RecordDetailEntry
{
    public required RecordDetailEntryKind Kind { get; init; }
    public required string Label { get; init; }
    public string? Value { get; init; }
    public uint? LinkedFormId { get; init; }
    public IReadOnlyList<RecordDetailListItem>? Items { get; init; }
    public bool ExpandByDefault { get; init; }
}