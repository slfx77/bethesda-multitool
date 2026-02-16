namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight leveled list snapshot for version tracking.
/// </summary>
public record TrackedLeveledList
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public required string ListType { get; init; }
    public byte ChanceNone { get; init; }
    public byte Flags { get; init; }
    public uint? GlobalFormId { get; init; }
    public List<TrackedLeveledEntry> Entries { get; init; } = [];
}

/// <summary>
///     Single entry in a leveled list for version tracking.
/// </summary>
public record TrackedLeveledEntry(ushort Level, uint FormId, ushort Count);
