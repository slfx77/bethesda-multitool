namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight weapon snapshot for version tracking.
/// </summary>
public record TrackedWeapon
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public int Value { get; init; }
    public short Damage { get; init; }
    public byte ClipSize { get; init; }
    public float Weight { get; init; }
    public float Speed { get; init; }
    public float MinSpread { get; init; }
    public float MaxRange { get; init; }
    public uint? AmmoFormId { get; init; }
    public byte WeaponType { get; init; }
}
