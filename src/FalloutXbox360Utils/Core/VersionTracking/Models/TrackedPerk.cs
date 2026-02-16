namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight perk snapshot for version tracking.
/// </summary>
public record TrackedPerk
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? Description { get; init; }
    public byte Trait { get; init; }
    public byte MinLevel { get; init; }
    public byte Ranks { get; init; }
    public byte Playable { get; init; }
    public int EntryCount { get; init; }
}
