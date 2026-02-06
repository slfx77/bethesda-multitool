namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Parsed main record with subrecords.
/// </summary>
public record ParsedMainRecord
{
    public required MainRecordHeader Header { get; init; }
    public long Offset { get; init; }
    public List<ParsedSubrecord> Subrecords { get; init; } = [];

    public string? EditorId => Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
}
