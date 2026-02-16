namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight placed reference snapshot for version tracking.
///     Only notable placements (those with EditorIDs or map marker names) are tracked.
/// </summary>
public record TrackedPlacement
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? MarkerName { get; init; }
    public uint BaseFormId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public bool IsMapMarker { get; init; }
}
