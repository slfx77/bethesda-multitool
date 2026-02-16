namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight note snapshot for version tracking.
/// </summary>
public record TrackedNote
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public byte NoteType { get; init; }
    public string? Text { get; init; }
}
