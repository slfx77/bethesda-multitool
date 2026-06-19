namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

internal sealed record RecordDetailListItem
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public uint? LinkedFormId { get; init; }
}
