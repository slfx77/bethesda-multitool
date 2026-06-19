using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm;

/// <summary>
///     Parsed main record with subrecords.
/// </summary>
public record ParsedMainRecord
{
    public required MainRecordHeader Header { get; init; }
    public long Offset { get; init; }
    public List<ParsedSubrecord> Subrecords { get; init; } = [];

    /// <summary>The record's editor ID, read from its EDID subrecord (null if absent).</summary>
    public string? EditorId => Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
}
