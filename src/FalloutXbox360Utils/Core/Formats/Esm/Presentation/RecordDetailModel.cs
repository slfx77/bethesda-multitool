namespace FalloutXbox360Utils.Core.Formats.Esm.Presentation;

internal sealed record RecordDetailModel
{
    public required string RecordSignature { get; init; }
    public required uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<RecordDetailSection> Sections { get; init; } = [];
}
