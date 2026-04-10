namespace FalloutXbox360Utils.Core.Formats.Esm.Presentation;

internal sealed record RecordDetailSection
{
    public required string Title { get; init; }
    public IReadOnlyList<RecordDetailEntry> Entries { get; init; } = [];
}