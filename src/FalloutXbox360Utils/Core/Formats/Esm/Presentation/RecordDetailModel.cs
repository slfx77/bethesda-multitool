namespace FalloutXbox360Utils.Core.Formats.Esm.Presentation;

internal enum RecordDetailEntryKind
{
    Scalar,
    Link,
    List,
    TextBlock,
    CodeBlock
}

internal sealed record RecordDetailListItem
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public uint? LinkedFormId { get; init; }
}

internal sealed record RecordDetailEntry
{
    public required RecordDetailEntryKind Kind { get; init; }
    public required string Label { get; init; }
    public string? Value { get; init; }
    public uint? LinkedFormId { get; init; }
    public IReadOnlyList<RecordDetailListItem>? Items { get; init; }
    public bool ExpandByDefault { get; init; }
}

internal sealed record RecordDetailSection
{
    public required string Title { get; init; }
    public IReadOnlyList<RecordDetailEntry> Entries { get; init; } = [];
}

internal sealed record RecordDetailModel
{
    public required string RecordSignature { get; init; }
    public required uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? DisplayName { get; init; }
    public IReadOnlyList<RecordDetailSection> Sections { get; init; } = [];
}
