namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight ammo snapshot for version tracking.
/// </summary>
public record TrackedAmmo
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public float Speed { get; init; }
    public byte Flags { get; init; }
    public uint Value { get; init; }
    public float Weight { get; init; }
    public uint? ProjectileFormId { get; init; }
}
